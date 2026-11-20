"""Stage only publish output and public assets, then archive without runtime data."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import shutil
import tarfile
import zipfile

RIDS = {"win-x64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64"}
FORBIDDEN = {".key", ".pem", ".pfx", ".p12", ".db", ".sqlite", ".sqlite3", ".duckdb", ".wal", ".log"}
ROOT = Path(__file__).resolve().parents[2]


def validate_tree(folder, rid=None):
    # 校验发布入口：单文件形态有 apphost（SData/SData.exe），回退形态有完整托管三件套（dll/deps/runtimeconfig）。
    # 二者至少满足其一，避免绑定具体发布形态（便于某 RID 单文件失败时按 apphost 回退）。
    apphost = "SData.exe" if (rid and rid.startswith("win-")) else "SData"
    has_apphost = (folder / apphost).is_file() or (folder / "SData.exe").is_file() or (folder / "SData").is_file()
    managed = ["SData.dll", "SData.deps.json", "SData.runtimeconfig.json"]
    present = [(folder / name).is_file() for name in managed]
    has_managed = all(present)
    if not has_apphost and not has_managed:
        raise ValueError("Missing publish entry (apphost or complete managed assembly set)")
    if any(present) and not has_managed:
        raise ValueError("Incomplete managed assembly set (dll/deps/runtimeconfig must coexist)")
    for name in ["appsettings.json", "wwwroot/index.html", "wwwroot/rum/probe.js"]:
        if not (folder / name).is_file():
            raise ValueError(f"Missing package file: {name}")
    for file in folder.rglob("*"):
        relative = file.relative_to(folder)
        if file.is_symlink():
            raise ValueError(f"Unexpected symlink: {relative}")
        if not file.is_file():
            continue
        if (file.suffix.lower() in FORBIDDEN or file.name.startswith(".env")
                or file.name.lower().endswith(("-wal", "-shm"))
                or any(p.lower() in {"data", "logs", "node_modules", ".git"} for p in relative.parts)
                or file.name.lower() == "appsettings.development.json"
                or relative.as_posix() in {"configs/system.json", "configs/system.yaml"}):
            raise ValueError(f"Runtime/private file in package: {relative}")
        if file.suffix.lower() in {".json", ".xml", ".txt", ".config", ".yaml", ".yml"}:
            text = file.read_text(encoding="utf-8", errors="replace")
            if re.search(r"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----|<RSAKeyValue>", text):
                raise ValueError(f"Private key in package: {relative}")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--rid", required=True, choices=sorted(RIDS))
    parser.add_argument("--version", required=True)
    parser.add_argument("--publish", required=True, type=Path)
    parser.add_argument("--ui", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    if not re.fullmatch(r"[0-9A-Za-z][0-9A-Za-z.+-]{0,127}", args.version):
        raise ValueError("Invalid archive version")
    publish, ui, output = args.publish.resolve(), args.ui.resolve(), args.output.resolve()
    if not publish.is_dir() or not ui.is_dir():
        raise ValueError("Publish and UI inputs must exist")
    name = f"sdata-{args.version}-{args.rid}"
    stage = output / name
    # Never reuse an old stage, where runtime files could have been generated.
    stage.mkdir(parents=True, exist_ok=False)
    shutil.copytree(publish, stage, dirs_exist_ok=True)
    shutil.copytree(ui, stage / "wwwroot", dirs_exist_ok=True)
    config_dir = stage / "configs"
    config_dir.mkdir(exist_ok=True)
    template = json.loads((ROOT / "configs/system.example.json").read_text(encoding="utf-8"))
    template["SData"]["Auth"]["InitialAdminPassword"] = ""
    template["SData"]["RabbitMq"]["Uri"] = ""
    template["SData"]["Ingest"]["InitialApiKey"] = ""
    (config_dir / "system.example.json").write_text(
        json.dumps(template, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    script = "start.ps1" if args.rid.startswith("win-") else "start.sh"
    shutil.copyfile(ROOT / "tool/Release" / script, stage / script)
    shutil.copyfile(ROOT / "tool/Release/README.txt", stage / "README.txt")
    validate_tree(stage, args.rid)
    executable = stage / ("SData.exe" if args.rid.startswith("win-") else "SData")
    if not executable.is_file():
        raise ValueError("Missing self-contained apphost")
    if not args.rid.startswith("win-"):
        executable.chmod(0o755)
        (stage / script).chmod(0o755)
    if args.rid.startswith("win-"):
        archive = output / f"{name}.zip"
        with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as bundle:
            for file in sorted(stage.rglob("*")):
                if file.is_file():
                    bundle.write(file, file.relative_to(output))
    else:
        archive = output / f"{name}.tar.gz"
        with tarfile.open(archive, "w:gz") as bundle:
            bundle.add(stage, arcname=name)
    with archive.open("rb") as stream:
        digest = hashlib.file_digest(stream, "sha256").hexdigest()
    (output / f"{archive.name}.sha256").write_text(f"{digest}  {archive.name}\n", encoding="ascii")
    print(archive.name)


if __name__ == "__main__":
    main()
