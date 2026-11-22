"""Local release-tool regression tests (stdlib only)."""
import contextlib
import importlib.util
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest import mock
import zipfile

TOOLS = Path(__file__).resolve().parent
REPO = TOOLS.parents[1]


def module(name):
    spec = importlib.util.spec_from_file_location(name, TOOLS / (name + ".py"))
    value = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(value)
    return value


package = module("package")
smoke = module("smoke")


def run_package(root):
    publish, ui, output = root / "publish", root / "ui", root / "output"
    (publish / "wwwroot/assets").mkdir(parents=True)
    (publish / "wwwroot/assets/index-stale.js").write_text("stale")
    (publish / "SData.exe").write_bytes(b"apphost")
    (publish / "appsettings.json").write_text('{"Serilog": {}}', encoding="utf-8")
    (ui / "rum").mkdir(parents=True)
    (ui / "index.html").write_text("<html></html>")
    (ui / "rum/probe.js").write_text("probe")
    output.mkdir()
    argv = ["package.py", "--rid", "win-x64", "--version", "0.0.0-test",
            "--publish", str(publish), "--ui", str(ui), "--output", str(output)]
    with mock.patch.object(sys, "argv", argv), contextlib.redirect_stdout(io.StringIO()):
        package.main()
    return output / "sdata-0.0.0-test-win-x64"


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
            for name in ["configs/rsa_private.key", "configs/system.json", "data/sdata.db", "server.log",
                         ".env.production", "appsettings.Development.json"]:
                path = folder / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text("private")
                with self.assertRaises(ValueError, msg=name):
                    package.validate_tree(folder)
                path.unlink()

    def test_stale_publish_web_root_is_replaced_by_release_ui(self):
        with tempfile.TemporaryDirectory() as directory:
            stage = run_package(Path(directory).resolve())
            self.assertFalse((stage / "wwwroot/assets/index-stale.js").exists())
            self.assertTrue((stage / "wwwroot/index.html").is_file())
            self.assertTrue((stage / "wwwroot/rum/probe.js").is_file())

    def test_package_ships_no_configs_or_launchers(self):
        with tempfile.TemporaryDirectory() as directory:
            stage = run_package(Path(directory).resolve())
            self.assertFalse((stage / "configs").exists())
            self.assertFalse((stage / "start.ps1").exists())
            self.assertFalse((stage / "start.sh").exists())
            self.assertEqual((stage / "appsettings.json").read_text(encoding="utf-8"), '{"Serilog": {}}')
            self.assertTrue((stage / "README.txt").is_file())
            self.assertTrue((stage / "SData.exe").is_file())

    def test_shipped_appsettings_has_no_ui_managed_settings(self):
        # 有页面入口的设置只存数据库；配置文件只含控制台日志与无页面入口的参数（默认值）
        config = json.loads((REPO / "src/SData/appsettings.json").read_text(encoding="utf-8"))
        self.assertEqual(set(config), {"Serilog", "SData"})
        self.assertEqual([sink["Name"] for sink in config["Serilog"]["WriteTo"]], ["Console"])
        ui_managed = {"S3", "Retention", "Compression", "OTLP", "Mq", "Kafka", "RabbitMq", "Pulsar", "EsBulk", "Mqtt"}
        self.assertFalse(ui_managed & set(config["SData"]))
        self.assertEqual(config["SData"]["Port"], 5300)
        self.assertEqual(config["SData"]["Ingest"]["InitialApiKey"], "")
        # 初始管理员密码与令牌有效期只在配置文件中提供，代码里没有默认值
        self.assertTrue(config["SData"]["Auth"]["InitialAdminPassword"])
        self.assertGreater(config["SData"]["Auth"]["TokenExpireMinutes"], 0)

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


if __name__ == "__main__":
    unittest.main()
