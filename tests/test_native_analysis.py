"""Offline regression tests; requires capstone and pefile in artifacts/tools/python."""
import importlib.util
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('native_preview_analysis', ROOT / 'scripts/Inspect-NativePreview.py')
analysis = importlib.util.module_from_spec(spec)
spec.loader.exec_module(analysis)


class FallbackBoundsTests(unittest.TestCase):
    def setUp(self):
        self.decoder = analysis.capstone.Cs(analysis.capstone.CS_ARCH_X86, analysis.capstone.CS_MODE_64)
        self.base, self.start = 0x140000000, 0x1000

    def end(self, code):
        return analysis.conservative_fallback_end(self.decoder, bytes.fromhex(code), self.start, self.base)

    def test_ret_does_not_absorb_next_function_after_padding(self):
        self.assertEqual(self.end('c3 cc cc e8 01 00 00 00 c3'), self.start + 1)

    def test_ret_with_stack_operand_preserves_full_instruction(self):
        self.assertEqual(self.end('c2 00 00 cc e8 01 00 00 00'), self.start + 3)

    def test_unconditional_tail_jump_ends_stub(self):
        self.assertEqual(self.end('e9 01 00 00 00 90 c3'), self.start + 5)

    def test_conditional_branch_is_not_unconditional_terminator(self):
        self.assertEqual(self.end('75 01 90 c3'), self.start + 4)

    def test_incomplete_instruction_does_not_invent_range(self):
        self.assertEqual(self.end('90 e8'), self.start + 1)

    def test_unmatched_reference_targets_remain_explicit(self):
        self.assertEqual(analysis.empty_reference_results({0x120, 0x128}),
                         {'0x120': [], '0x128': []})

    def test_rip_displacement_candidates_account_for_trailing_immediate(self):
        # mov rax,[rip+0x10]; mov dword [rip+0x10],1
        code = bytes.fromhex('48 8b 05 10 00 00 00 c7 05 10 00 00 00 01 00 00 00')
        candidates = list(analysis.rip_displacement_candidates(code, 0x1000, {0x1017, 0x1021}))
        self.assertIn((3, 0x1017), candidates)
        self.assertIn((9, 0x1021), candidates)

    def test_rip_validation_uses_instruction_end_and_actual_displacement_position(self):
        self.decoder.detail = True
        code = bytes.fromhex('c7 05 10 00 00 00 01 00 00 00')
        ins = next(self.decoder.disasm(code, self.base + self.start))
        self.assertTrue(analysis.matches_rip_reference(ins, self.start + 2, self.start + 26, self.base))
        self.assertFalse(analysis.matches_rip_reference(ins, self.start + 3, self.start + 26, self.base))
        self.assertFalse(analysis.matches_rip_reference(ins, self.start + 2, self.start + 22, self.base))


if __name__ == '__main__':
    unittest.main()
