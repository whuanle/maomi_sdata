"""Smoke-test a locally generated package; no arbitrary URLs or commands accepted."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import socket
import subprocess
import tarfile
import tempfile
import time
import urllib.error
import urllib.request
import zipfile


def free_port():
    with socket.socket() as listener:
        listener.bind(("127.0.0.1", 0))
        return listener.getsockname()[1]


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        raise ValueError("Redirects are forbidden")


def extract_archive(archive, target):
    def destination(name):
        path = (target / name).resolve()
        if not path.is_relative_to(target) or path == target:
            raise ValueError("Unsafe archive path")
        return path

    if archive.suffix == ".zip":
        with zipfile.ZipFile(archive) as bundle:
            for member in bundle.infolist():
                path = destination(member.filename)
                if member.is_dir():
                    path.mkdir(parents=True, exist_ok=True)
                else:
                    if (member.external_attr >> 16) & 0o170000 == 0o120000:
                        raise ValueError("Symlinks are forbidden")
                    path.parent.mkdir(parents=True, exist_ok=True)
                    with bundle.open(member) as source, path.open("xb") as output:
                        shutil.copyfileobj(source, output)
    else:
        with tarfile.open(archive) as bundle:
            for member in bundle:
                path = destination(member.name)
                if member.isdir():
                    path.mkdir(parents=True, exist_ok=True)
                elif member.isfile():
                    path.parent.mkdir(parents=True, exist_ok=True)
                    with bundle.extractfile(member) as source, path.open("xb") as output:
                        shutil.copyfileobj(source, output)
                    path.chmod(member.mode & 0o777)
                else:
                    raise ValueError("Only regular files/directories are accepted")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--archive", required=True, type=Path)
    args = parser.parse_args()
    archive = args.archive.resolve(strict=True)
    checksum = archive.with_name(archive.name + ".sha256").read_text().split()[0]
    with archive.open("rb") as stream:
        assert hashlib.file_digest(stream, "sha256").hexdigest() == checksum
    with tempfile.TemporaryDirectory(prefix="sdata smoke ") as directory:
        target = Path(directory).resolve()
        extract_archive(archive, target)
        folders = list(target.iterdir())
        assert len(folders) == 1 and folders[0].is_dir()
        home = folders[0]
        # 包内没有 configs 目录与启动脚本；appsettings.json 只含控制台日志与无页面入口的参数
        assert not (home / "configs").exists()
        assert not list(home.glob("start.*"))
        settings_path = home / "appsettings.json"
        settings = json.loads(settings_path.read_text(encoding="utf-8"))
        assert set(settings) == {"Serilog", "SData"} and "Port" in settings["SData"], settings
        # 初始管理员密码只来自 appsettings.json：程序中没有内置默认值
        password = settings["SData"]["Auth"]["InitialAdminPassword"]
        assert password, "appsettings.json must provide the initial admin password"
        port, mqtt = free_port(), free_port()
        while mqtt == port:
            mqtt = free_port()
        # 测试机上 5300 可能已被占用：改写解压副本中的 SData:Port，同时验证配置文件中的端口生效
        settings["SData"]["Port"] = port
        settings_path.write_text(json.dumps(settings, indent=2), encoding="utf-8")
        env = dict(os.environ)
        env["ASPNETCORE_ENVIRONMENT"] = "Production"
        env["DOTNET_MULTILEVEL_LOOKUP"] = "0"
        env["DOTNET_ROOT"] = str(target / "no-global-runtime")
        env["ASPNETCORE_CONTENTROOT"] = str(home)
        # 内存上限与 MQTT 端口只为测试环境覆盖，其余全部来自配置文件或内置默认值
        env["SData__Mqtt__Port"] = str(mqtt)
        env["SData__DuckDb__MemoryLimit"] = "128MB"
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
        # Local test only: address is fixed to the service spawned below, never user input.
        base = f"http://127.0.0.1:{port}"

        def request(path, payload=None, token=None):
            if path not in {"/health", "/", "/rum/probe.js", "/api/auth/login", "/api/analytics/query"}:
                raise ValueError("Unsupported endpoint")
            headers = {"Content-Type": "application/json"}
            if token:
                headers["Authorization"] = "Bearer " + token
            data = None if payload is None else json.dumps(payload).encode()
            req = urllib.request.Request(base + path, data=data, headers=headers)
            with opener.open(req, timeout=5) as response:
                return response.read()

        with (target / "smoke.log").open("wb") as output:
            # Fixed executable name from our own package; no shell command strings.
            executable = home / ("SData.exe" if os.name == "nt" else "SData")
            process = subprocess.Popen(
                [str(executable)], shell=False, cwd=home, env=env,
                stdout=output, stderr=subprocess.STDOUT)
            try:
                deadline = time.monotonic() + 90
                while True:
                    if process.poll() is not None:
                        raise RuntimeError("Service exited before health check")
                    try:
                        request("/health")
                        break
                    except (OSError, urllib.error.URLError):
                        if time.monotonic() > deadline:
                            raise TimeoutError("Service did not become healthy")
                        time.sleep(1)
                assert b"<html" in request("/").lower()
                assert len(request("/rum/probe.js")) > 1000
                auth = json.loads(request("/api/auth/login", {"username": "admin", "password": password}))
                result = json.loads(request("/api/analytics/query",
                    {"sql": "SELECT 42 AS answer", "rowLimit": 1}, auth["token"]))
                assert result["rows"][0][0] == 42, result
                assert (home / "configs/rsa_private.key").is_file()
                # 首次启动把程序目录的 appsettings.json 复制为 configs/appsettings.json（逐字节一致，供覆盖）
                assert (home / "configs/appsettings.json").read_bytes() == settings_path.read_bytes()
                assert (home / "data/sdata.db").is_file()
                assert (home / "data/sdata.duckdb").is_file()
                # 日志只输出到控制台，运行目录内不应产生任何日志文件
                assert not list(home.rglob("*.log"))
                print("PASS: checksum, single config file, admin login with configured password, SQLite, DuckDB, UI and probe")
            finally:
                if os.name == "nt":
                    subprocess.run(["taskkill.exe", "/PID", str(process.pid), "/T", "/F"],
                        shell=False, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
                else:
                    process.terminate()
                try:
                    process.wait(timeout=15)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait()
        # Never repackage this directory; it now contains credentials/runtime data.


if __name__ == "__main__":
    main()
