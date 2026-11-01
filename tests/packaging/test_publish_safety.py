"""A reused publish directory must never ship a running engine's private state."""
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
BASH = os.environ.get("CHERBOT_TEST_BASH") or (shutil.which("bash") if os.name == "posix" else None)
PWSH = shutil.which("pwsh")


class PublishSafetyTests(unittest.TestCase):
    def setUp(self):
        (ROOT / "artifacts").mkdir(exist_ok=True)
        temporary = tempfile.TemporaryDirectory(prefix="publish-safety-", dir=ROOT / "artifacts")
        self.addCleanup(temporary.cleanup)
        self.work = Path(temporary.name)

    @unittest.skipUnless(BASH, "Bash is required")
    def test_shell_publish_refuses_private_data_before_running_dotnet(self):
        for name in ("api-token", "cherbot.json", "cherbot.db", ".env", "keys/encryption.key"):
            with self.subTest(name=name):
                output = self.work / str(len(list(self.work.iterdir())))
                private = output / name
                private.parent.mkdir(parents=True)
                private.write_text("disposable-test-value")
                result = subprocess.run([BASH, "packaging/publish.sh"], cwd=ROOT,
                                        env={**os.environ, "OUT": output.as_posix()}, capture_output=True, text=True)
                self.assertNotEqual(0, result.returncode)
                self.assertIn("Refusing to package runtime data", result.stderr)

    @unittest.skipUnless(PWSH, "PowerShell 7 is required")
    def test_powershell_publish_refuses_private_data_before_running_dotnet(self):
        for name in ("api-token", "cherbot.json", "cherbot.db", ".env", "keys/encryption.key"):
            with self.subTest(name=name):
                output = self.work / str(len(list(self.work.iterdir())))
                private = output / "cherbot-win-x64" / name
                private.parent.mkdir(parents=True)
                private.write_text("disposable-test-value")
                result = subprocess.run([PWSH, "-NoProfile", "-File", "packaging/publish.ps1", "-Runtimes",
                                         "win-x64", "-NoDesktop", "-Zip", "-Output", str(output)],
                                        cwd=ROOT, capture_output=True, text=True)
                self.assertNotEqual(0, result.returncode)
                self.assertIn("Refusing to package an output directory", result.stderr)
