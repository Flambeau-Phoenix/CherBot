import hashlib
import importlib.util
import os
from pathlib import Path
import shutil
import subprocess
import tarfile
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("package_linux", ROOT / "packaging/package_linux.py")
package = importlib.util.module_from_spec(spec)
spec.loader.exec_module(package)
BASH = os.environ.get("CHERBOT_TEST_BASH") or (shutil.which("bash") if os.name == "posix" else None)


class LinuxPackageTests(unittest.TestCase):
    def setUp(self):
        (ROOT / "artifacts").mkdir(exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(prefix="package-test-", dir=ROOT / "artifacts")
        self.addCleanup(self.temp.cleanup)
        self.work = Path(self.temp.name)
        self.binary = self.work / "binary"
        self.binary.mkdir()
        header = bytearray(20)
        header[:6] = b"\x7fELF\x02\x01"
        header[18:20] = (62).to_bytes(2, "little")
        (self.binary / "cherbot").write_bytes(header)
        (self.binary / "appsettings.json").write_text("{}")
        self.stem = "cherbot-0.3.0-linux-x64"
        self.staged = self.work / self.stem

    def build(self):
        package.stage_release(self.binary, self.staged, "0.3.0", "linux-x64")
        archive = self.work / f"{self.stem}.tar.gz"
        installer = self.work / f"{self.stem}.run"
        package.create_archive(self.staged, archive)
        package.create_installer(archive, installer, "0.3.0", "linux-x64", self.stem)
        return archive, installer

    def extract(self, installer, destination):
        # Relative POSIX paths also work under Git Bash on Windows.
        return subprocess.run([BASH, installer.relative_to(ROOT).as_posix(), "--extract", destination.relative_to(ROOT).as_posix()],
                              cwd=ROOT, capture_output=True, text=True)

    def test_archive_has_unix_modes_and_no_private_input_files(self):
        (self.binary / "api-token").write_text("must never be packaged")
        archive, _ = self.build()
        with tarfile.open(archive) as tar:
            self.assertEqual(0o755, tar.getmember(f"{self.stem}/cherbot").mode)
            self.assertEqual(0o755, tar.getmember(f"{self.stem}/install.sh").mode)
            self.assertEqual(0o644, tar.getmember(f"{self.stem}/cherbot.example.json").mode)
            self.assertNotIn(f"{self.stem}/api-token", tar.getnames())
            self.assertIn(f"{self.stem}/docs/cherbot-ai-reference.txt", tar.getnames())

    def test_wrong_architecture_is_rejected(self):
        with self.assertRaisesRegex(ValueError, "linux-arm64"):
            package.validate_binary(self.binary / "cherbot", "linux-arm64")

    def test_installer_payload_matches_archive_exactly(self):
        archive, installer = self.build()
        header, payload = installer.read_bytes().split(b"__CHERBOT_ARCHIVE_BELOW__\n", 1)
        self.assertEqual(archive.read_bytes(), payload)
        self.assertIn(hashlib.sha256(payload).hexdigest().encode(), header)
        self.assertNotIn(b"@PAYLOAD_", header)
        self.assertNotIn("@STEM@", (self.staged / "README.md").read_text())

    @unittest.skipUnless(BASH, "Bash is required for extraction tests")
    def test_installer_extracts_and_refuses_an_existing_destination(self):
        archive, installer = self.build()
        destination = self.work / "extracted files"
        result = self.extract(installer, destination)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual((self.binary / "cherbot").read_bytes(), (destination / self.stem / "cherbot").read_bytes())
        again = self.extract(installer, destination)
        self.assertNotEqual(0, again.returncode)
        self.assertIn("already exists", again.stderr)

    @unittest.skipUnless(BASH, "Bash is required for extraction tests")
    def test_corrupt_installer_is_rejected_before_extraction(self):
        _, installer = self.build()
        content = installer.read_bytes()
        installer.write_bytes(content[:-1] + bytes([content[-1] ^ 1]))
        destination = self.work / "corrupt-output"
        result = self.extract(installer, destination)
        self.assertNotEqual(0, result.returncode)
        self.assertIn("checksum failed", result.stderr)
        self.assertFalse(destination.exists())


if __name__ == "__main__":
    unittest.main()
