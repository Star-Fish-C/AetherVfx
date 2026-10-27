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


if __name__ == '__main__':
    unittest.main()
