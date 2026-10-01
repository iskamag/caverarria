"""Pinned package verification. No mod or live client execution."""
import importlib.util
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("managed_setup", ROOT / "scripts/setup-managed-runtime.py")
setup = importlib.util.module_from_spec(spec)
spec.loader.exec_module(setup)


class PackagingTests(unittest.TestCase):
    def test_checked_package_extracts_only_the_net8_runtime_and_license(self):
        data = (ROOT / "runtime/managed-wasm-probe/packages/webassembly.2.1.0.nupkg").read_bytes()
        files = setup.verify_package(data)
        self.assertEqual(set(files), {"WebAssembly.dll", "WebAssembly.LICENSE"})
        self.assertEqual(files["WebAssembly.dll"][:2], b"MZ")
        self.assertIn(b"Apache License", files["WebAssembly.LICENSE"])

    def test_corrupt_package_is_rejected_before_extraction(self):
        with self.assertRaisesRegex(ValueError, "checksum mismatch"):
            setup.verify_package(b"wrong package")


if __name__ == "__main__":
    unittest.main()
