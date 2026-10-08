"""打包免安装程序和独立源码；不包含个人生词、测试残留或调研文件。"""
from pathlib import Path
from zipfile import ZipFile, ZIP_DEFLATED
import hashlib
import json
import shutil
import time

ROOT = Path(__file__).resolve().parents[1]
APP = ROOT / "dist" / "HoverLex"
OUT = ROOT / "release"
OUT.mkdir(exist_ok=True)
VERSION = (ROOT / "VERSION").read_text(encoding="utf-8").strip()

portable_files = ["HoverLex.exe", "HoverLex.exe.config", "HoverLexLauncher.exe", "HoverLexLauncher.exe.config", "EnglishProofreader.zip", "update-channel.json", "README.md", "THIRD-PARTY-NOTICES.md", "LICENSE"]
package = OUT / f"HoverLex-Windows-x64-v{VERSION}.zip"
with ZipFile(package, "w", ZIP_DEFLATED, compresslevel=9) as archive:
    for name in portable_files:
        archive.write(APP / name, "HoverLex/" + name)
    for path in sorted((APP / "Dictionary").iterdir()):
        archive.write(path, "HoverLex/Dictionary/" + path.name)

source_files = ["README.md", "THIRD-PARTY-NOTICES.md", "LICENSE", ".gitignore", "AGENTS.md", "build.ps1", "app.manifest", "HoverLex.exe.config", "VALIDATION.md", "SPIKES-INPUT-TRANSLATION.md", "VERSION"]
with ZipFile(OUT / f"HoverLex-Source-v{VERSION}.zip", "w", ZIP_DEFLATED, compresslevel=9) as archive:
    for name in source_files:
        archive.write(ROOT / name, "HoverLex/" + name)
    for folder in ("src", "scripts", "desktop", "assets"):
        for path in sorted((ROOT / folder).glob("*")):
            if path.is_file():
                archive.write(path, "HoverLex/" + path.relative_to(ROOT).as_posix())

revision = int(time.time() * 1000)
channel = ROOT / "updates"
(channel / "packages").mkdir(parents=True, exist_ok=True)
channel_name = f"packages/HoverLex-{VERSION}-{revision}.zip"
shutil.copyfile(package, channel / channel_name)
payload = {"Version": VERSION, "Revision": revision, "Package": channel_name, "Size": package.stat().st_size, "Sha256": hashlib.sha256(package.read_bytes()).hexdigest(), "AppSha256": hashlib.sha256((APP / "HoverLex.exe").read_bytes()).hexdigest()}
(channel / "release-payload.json").write_text(json.dumps(payload, separators=(",", ":")), encoding="utf-8")
for path in (package, OUT / f"HoverLex-Source-v{VERSION}.zip"):
    print(f"{path.name}: {path.stat().st_size / 1024 / 1024:.1f} MiB")
