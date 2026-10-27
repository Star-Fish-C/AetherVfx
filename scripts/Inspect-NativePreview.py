"""Read-only PE/signature analysis. Output stays in ignored artifacts; never executes the game.

python -m pip install --target artifacts/tools/python capstone pefile
python scripts/Inspect-NativePreview.py <ffxiv_dx11.exe>
Decompiled local ClientStructs files may be placed in artifacts/native-analysis/structs.
This report establishes addresses/control flow, not runtime safety or visual acceptance.
"""
from pathlib import Path
import argparse
import bisect
import hashlib
import json
import re
import struct
import sys

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'artifacts/tools/python'))
import capstone
import pefile


def conservative_fallback_end(md, code, start_rva, image_base):
    """Bound a no-unwind stub at its first terminator; this is not a full CFG."""
    end = start_rva
    for ins in md.disasm(code, image_base + start_rva):
        end = ins.address - image_base + ins.size
        if ins.mnemonic in ('ret', 'retf', 'iret', 'iretd', 'iretq', 'jmp', 'int3', 'ud2'):
            break
    return end


def empty_reference_results(requested_refs):
    """Represent zero matches explicitly; the scan never covers indirect references."""
    return {hex(target): [] for target in sorted(requested_refs)}


def rip_displacement_candidates(data, start_rva, targets):
    """Find possible disp32 positions, including instructions with trailing immediates.

    These are only byte candidates; callers must decode from an instruction boundary.
    RIP-relative addressing uses the end of the full instruction, not the displacement.
    """
    for offset in range(len(data) - 3):
        displacement = struct.unpack_from('<i', data, offset)[0]
        after_disp = start_rva + offset + 4
        for immediate_size in (0, 1, 2, 4):
            target = after_disp + immediate_size + displacement
            if target in targets:
                yield offset, target


def matches_rip_reference(ins, displacement_rva, target, image_base):
    if ins.address - image_base + ins.disp_offset != displacement_rva or ins.disp_size != 4:
        return False
    return any(op.type == capstone.CS_OP_MEM and op.mem.base == capstone.x86.X86_REG_RIP and
               ins.address + ins.size + op.mem.disp - image_base == target for op in ins.operands)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('executable', type=Path)
    parser.add_argument('--function', action='append', default=[], help='Extra RVA or start:end range, in hex')
    parser.add_argument('--references', action='append', default=[], help='Find direct call/jump references to a hex RVA; indirect calls are not covered')
    parser.add_argument('--field-offset', action='append', default=[], help='Find memory operands with this hex displacement (candidates, not a type identification)')
    parser.add_argument('--rip-target', action='append', default=[], help='Find decoded RIP-relative references to this global RVA; excludes code without unwind metadata')
    parser.add_argument('--vtable', action='append', default=[], metavar='RVA:COUNT',
                        help='Read a candidate vtable from .rdata; RVA is hex, count is decimal (max 128)')
    parser.add_argument('--output-dir', type=Path, default=ROOT / 'artifacts/native-analysis',
                        help='Directory for this run; use a distinct path to preserve earlier evidence')
    parser.add_argument('--structs-dir', type=Path, default=ROOT / 'artifacts/native-analysis/structs',
                        help='Directory containing optional local ClientStructs ILSpy exports')
    args = parser.parse_args()
    binary = args.executable.read_bytes()
    pe = pefile.PE(data=binary)
    base = pe.OPTIONAL_HEADER.ImageBase
    image = pe.get_memory_mapped_image()
    text = next(s for s in pe.sections if s.Name.rstrip(b'\0') == b'.text')
    text_data = text.get_data()
    text_rva = text.VirtualAddress
    entries = sorted((e.struct.BeginAddress, e.struct.EndAddress) for e in pe.DIRECTORY_ENTRY_EXCEPTION)
    starts = [a for a, _ in entries]
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    md.detail = True
    output = args.output_dir
    output.mkdir(parents=True, exist_ok=True)
    report = {'ExecutableSHA256': hashlib.sha256(binary).hexdigest(), 'ImageBase': hex(base),
              'Signatures': {}, 'Vtables': {}, 'Functions': {}}
    functions = {}
    fallback_starts = set()
    # Decode from the enclosing unwind range to reject opcode bytes embedded in instructions.
    # This still is not a complete CFG: indirect callers and code without unwind metadata are omitted.
    boundaries = {}
    requested_refs = {int(value, 16) for value in args.references}
    report['DirectReferences'] = empty_reference_results(requested_refs)
    report['FieldCandidates'] = {}
    rip_targets = {int(value, 16) for value in args.rip_target}
    report['RipReferences'] = empty_reference_results(rip_targets)
    seen_rip = set()
    if rip_targets:
        for offset, target in rip_displacement_candidates(text_data, text_rva, rip_targets):
            site = text_rva + offset
            index = bisect.bisect_right(starts, site) - 1
            if index < 0 or not entries[index][0] <= site < entries[index][1]:
                continue
            start, end = entries[index]
            if start not in boundaries:
                boundaries[start] = {ins.address - base: ins for ins in md.disasm(image[start:end], base + start)}
            for address, ins in boundaries[start].items():
                if not matches_rip_reference(ins, site, target, base):
                    continue
                if (address, target) in seen_rip:
                    continue
                seen_rip.add((address, target))
                report['RipReferences'][hex(target)].append({'Site': hex(address), 'Instruction': f'{ins.mnemonic} {ins.op_str}', 'UnwindRange': [hex(start), hex(end)]})
                functions.setdefault(start, (end, f'RIP reference to {target:#x}'))
    for value in args.field_offset:
        displacement = int(value, 16)
        candidates = []
        encoded = struct.pack('<i', displacement)
        for match in re.finditer(re.escape(encoded), text_data):
            site = text_rva + match.start()
            index = bisect.bisect_right(starts, site) - 1
            if index < 0 or not entries[index][0] <= site < entries[index][1]:
                continue
            start, end = entries[index]
            if start not in boundaries:
                boundaries[start] = {ins.address - base: ins for ins in md.disasm(image[start:end], base + start)}
            for address, ins in boundaries[start].items():
                if not address <= site < address + ins.size:
                    continue
                if any(op.type == capstone.CS_OP_MEM and op.mem.disp == displacement for op in ins.operands):
                    candidates.append({'Site': hex(address), 'Instruction': f'{ins.mnemonic} {ins.op_str}', 'UnwindRange': [hex(start), hex(end)]})
                    functions.setdefault(start, (end, f'candidate field {displacement:#x}'))
        report['FieldCandidates'][hex(displacement)] = candidates
    for match in re.finditer(b'[\xe8\xe9]', text_data):
        if not requested_refs:
            break
        offset = match.start()
        if offset + 5 > len(text_data):
            continue
        site = text_rva + offset
        target = site + 5 + struct.unpack_from('<i', text_data, offset + 1)[0]
        if target not in requested_refs:
            continue
        index = bisect.bisect_right(starts, site) - 1
        if index < 0 or not entries[index][0] <= site < entries[index][1]:
            continue
        start, end = entries[index]
        if start not in boundaries:
            boundaries[start] = {ins.address - base: ins for ins in md.disasm(image[start:end], base + start)}
        ins = boundaries[start].get(site)
        if ins is None or ins.mnemonic not in ('call', 'jmp') or ins.size != 5:
            continue
        report['DirectReferences'].setdefault(hex(target), []).append({'Site': hex(site), 'Instruction': ins.mnemonic, 'UnwindRange': [hex(start), hex(end)]})
        functions.setdefault(start, (end, f'caller of {target:#x}'))

    def scan(signature):
        pattern = b''.join(b'.' if b == '??' else re.escape(bytes.fromhex(b)) for b in signature.split())
        return [text_rva + m.start() for m in re.finditer(pattern, text_data, re.DOTALL)]

    def add_function(rva, name):
        index = bisect.bisect_right(starts, rva) - 1
        if index >= 0 and entries[index][0] <= rva < entries[index][1]:
            start, end = entries[index]
        else:
            start = rva
            end = conservative_fallback_end(md, image[rva:rva + 256], rva, base)
            fallback_starts.add(start)
        functions.setdefault(start, (end, name))
        return start

    signatures = {}
    for source in args.structs_dir.glob('*.cs'):
        for name, sig, relative in re.findall(r'new Address\("([^\"]+)", "([^\"]+)", new ushort\[\d+\] \{ (\d+) \}', source.read_text(encoding='utf-8-sig')):
            signatures[name] = (sig, int(relative))
    signatures['ActorVfxCreate'] = ('40 53 55 56 57 48 81 EC ?? ?? ?? ?? 0F 29 B4 24 ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 0F B6 AC 24 ?? ?? ?? ?? 0F 28 F3 49 8B F8', None)
    signatures['ActorRemoveVtable'] = ('0F 11 48 10 48 8D 05', None)
    for name, (sig, relative) in signatures.items():
        hits = scan(sig)
        targets = []
        for hit in hits:
            if image[hit] in (0xE8, 0xE9):
                target = hit + 5 + struct.unpack_from('<i', image, hit + 1)[0]
            elif name.endswith(('.Instance', '.StaticVirtualTable')) and relative is not None:
                target = hit + relative + 4 + struct.unpack_from('<i', image, hit + relative)[0]
            else:
                target = hit
            targets.append(target)
            if not name.endswith(('.Instance', '.StaticVirtualTable')):
                add_function(target, name)
            elif name.endswith('.StaticVirtualTable'):
                # The signature points to a RIP-relative LEA, not executable vtable data.
                pointers = [struct.unpack_from('<Q', image, target + 8 * i)[0] - base for i in range(32)]
                report['Vtables'][hex(target)] = [hex(x) for x in pointers]
                for i in (30, 31):
                    if text_rva <= pointers[i] < text_rva + len(text_data):
                        add_function(pointers[i], f'{name}.vfunc{i}')
        report['Signatures'][name] = {'Matches': [hex(x) for x in hits], 'Targets': [hex(x) for x in targets],
                                      'Kind': 'Vtable' if name.endswith('.StaticVirtualTable') else 'RipRelativeStorage' if name.endswith('.Instance') else 'Code'}
        if name == 'ActorRemoveVtable':
            for hit in hits:
                table = hit + 11 + struct.unpack_from('<i', image, hit + 7)[0]
                pointers = [struct.unpack_from('<Q', image, table + 8 * i)[0] - base for i in range(4)]
                report['Vtables'][hex(table)] = [hex(x) for x in pointers]
                add_function(pointers[0], 'ActorRemove')
    # Constructor LEA references to read-only data reveal vtables without relying on guessed offsets.
    for start, (end, name) in list(functions.items()):
        if not name.endswith('CharaView.Ctor'):
            continue
        for ins in md.disasm(image[start:end], base + start):
            if ins.mnemonic == 'lea' and len(ins.operands) == 2 and ins.operands[1].type == capstone.CS_OP_MEM:
                mem = ins.operands[1].mem
                if mem.base != capstone.x86.X86_REG_RIP:
                    continue
                table = ins.address + ins.size + mem.disp - base
                section = pe.get_section_by_rva(table)
                if section is None or section.Name.rstrip(b'\0') != b'.rdata':
                    continue
                pointers = [struct.unpack_from('<Q', image, table + 8 * i)[0] - base for i in range(12)]
                if not all(text_rva <= x < text_rva + len(text_data) for x in pointers):
                    continue
                report['Vtables'][hex(table)] = [hex(x) for x in pointers]
                for i, target in enumerate(pointers):
                    add_function(target, f'CharaView.vfunc{i}')
    for rva in args.function:
        if ':' in rva:
            start, end = (int(x, 16) for x in rva.split(':'))
            if not text_rva <= start < end <= text_rva + len(text_data):
                raise ValueError('Requested range outside .text')
            functions[start] = (end, 'requested explicit range')
            fallback_starts.discard(start)
        else:
            add_function(int(rva, 16), 'requested')
    for spec in args.vtable:
        parts = spec.split(':')
        if len(parts) != 2:
            raise ValueError('Vtable must be RVA:COUNT')
        table, count = int(parts[0], 16), int(parts[1], 10)
        section = pe.get_section_by_rva(table)
        if section is None or section.Name.rstrip(b'\0') != b'.rdata' or not 1 <= count <= 128:
            raise ValueError('Candidate vtable must start in .rdata and contain 1-128 entries')
        end = table + count * 8
        section_end = section.VirtualAddress + max(section.Misc_VirtualSize, section.SizeOfRawData)
        if end > len(image) or end > section_end:
            raise ValueError('Candidate vtable extends beyond .rdata')
        # Values are reported as RVAs only; this is not proof of a complete vtable or runtime type.
        pointers = [struct.unpack_from('<Q', image, table + 8 * i)[0] - base for i in range(count)]
        report['Vtables'][hex(table)] = [hex(x) for x in pointers]
    lines = []
    for start, (end, name) in sorted(functions.items()):
        lines.append(f'\n## {name} RVA {start:#x}..{end:#x}\n')
        calls = []
        for ins in md.disasm(image[start:end], base + start):
            lines.append(f'{ins.address - base:08x}  {ins.mnemonic:8} {ins.op_str}')
            if ins.mnemonic == 'call' and ins.operands[0].type == capstone.CS_OP_IMM:
                calls.append(hex(ins.operands[0].imm - base))
        range_kind = 'explicit' if name == 'requested explicit range' else 'fallback-terminated' if start in fallback_starts else 'unwind'
        report['Functions'][hex(start)] = {'Name': name, 'End': hex(end), 'RangeKind': range_kind, 'DirectCalls': calls}
    (output / 'functions.txt').write_text('\n'.join(lines), encoding='utf-8')
    (output / 'report.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    print(json.dumps({'ExecutableSHA256': report['ExecutableSHA256'], 'Signatures': report['Signatures'], 'Vtables': report['Vtables']}, indent=2))


if __name__ == '__main__':
    main()
