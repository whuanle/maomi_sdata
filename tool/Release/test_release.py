"""Local release-tool regression tests (stdlib only)."""
import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
import zipfile

TOOLS = Path(__file__).resolve().parent


def module(name):
    spec = importlib.util.spec_from_file_location(name, TOOLS / (name + ".py"))
    value = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(value)
    return value


package = module("package")
smoke = module("smoke")


class ReleaseTests(unittest.TestCase):
    def test_private_files_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            folder = Path(directory)
            for name in ["SData.dll", "SData.deps.json", "SData.runtimeconfig.json",
                         "appsettings.json", "wwwroot/index.html", "wwwroot/rum/probe.js"]:
                path = folder / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text("{}")
            package.validate_tree(folder)
            for name in ["configs/rsa_private.key", "data/sdata.db", "server.log",
                         ".env.production", "configs/system.json", "appsettings.Development.json"]:
                path = folder / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text("private")
                with self.assertRaises(ValueError, msg=name):
                    package.validate_tree(folder)
                path.unlink()

    def test_archive_traversal_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory).resolve()
            archive = root / "malformed.zip"
            with zipfile.ZipFile(archive, "w") as bundle:
                bundle.writestr("../escape.txt", "not extracted")
            target = root / "output"
            target.mkdir()
            with self.assertRaises(ValueError):
                smoke.extract_archive(archive, target)
            self.assertFalse((root / "escape.txt").exists())

    def test_zip_extracts_regular_files(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory).resolve()
            archive = root / "valid.zip"
            with zipfile.ZipFile(archive, "w") as bundle:
                bundle.writestr("sdata/test.txt", "ok")
            target = root / "output"
            target.mkdir()
            smoke.extract_archive(archive, target)
            self.assertEqual((target / "sdata/test.txt").read_text(), "ok")

    def test_shell_launcher_preserves_config_and_escapes_password(self):
        with tempfile.TemporaryDirectory() as directory:
            home = Path(directory)
            (home / "configs").mkdir()
            (home / "start.sh").write_text((TOOLS / "start.sh").read_text(), newline="\n")
            template = {"SData": {"Auth": {"InitialAdminPassword": ""}}}
            (home / "configs/system.example.json").write_text(json.dumps(template, indent=2))
            # Fake apphost: test startup configuration without running a server.
            (home / "SData").write_text("#!/usr/bin/env bash\nexit 0\n", newline="\n")
            (home / "SData").chmod(0o755)
            import os
            import secrets
            password = secrets.token_urlsafe(24) + '"\\\t'
            env = dict(os.environ, SDATA_INITIAL_ADMIN_PASSWORD=password)
            env.pop("SDATA_FILE", None)
            result = subprocess.run(["bash", "./start.sh"], cwd=home, env=env,
                                    shell=False, capture_output=True, text=True)
            self.assertEqual(result.returncode, 0, result.stderr)
            config_path = home / "configs/system.json"
            before = config_path.read_bytes()
            config = json.loads(before)
            self.assertEqual(config["SData"]["Auth"]["InitialAdminPassword"], password)
            result = subprocess.run(["bash", "./start.sh"], cwd=home, env=env,
                                    shell=False, capture_output=True, text=True)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(config_path.read_bytes(), before)


if __name__ == "__main__":
    unittest.main()
