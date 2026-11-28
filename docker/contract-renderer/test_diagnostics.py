"""Run against test_diagnostics compiled from the production sandbox.c."""
import subprocess
import unittest


class LandlockDiagnosticsTests(unittest.TestCase):
    def test_low_abi_reports_actual_version_without_inventing_errno(self):
        for abi in (1, 2):
            with self.subTest(abi=abi):
                result = subprocess.run(["/tmp/test-diagnostics", str(abi), "95"], capture_output=True, text=True)
                self.assertEqual(125, result.returncode)
                self.assertIn(f"detected={abi}", result.stderr)
                self.assertIn("required>=3", result.stderr)
                self.assertNotIn("errno=", result.stderr)

    def test_failed_probe_reports_actual_errno(self):
        for number, description in ((38, "Function not implemented"), (95, "Operation not supported"), (1, "Operation not permitted")):
            with self.subTest(errno=number):
                result = subprocess.run(["/tmp/test-diagnostics", "-1", str(number)], capture_output=True, text=True)
                self.assertEqual(125, result.returncode)
                self.assertIn("probe failed", result.stderr)
                self.assertIn(f"errno={number}", result.stderr)
                self.assertIn(description, result.stderr)
                self.assertNotIn("detected=", result.stderr)

    def test_supported_abi_continues_without_failure_message(self):
        result = subprocess.run(["/tmp/test-diagnostics", "3", "95"], capture_output=True, text=True)
        self.assertEqual(0, result.returncode)
        self.assertEqual("", result.stderr)


if __name__ == "__main__":
    unittest.main()
