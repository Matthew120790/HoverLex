"""将官方 LanguageTool 和 Java 运行时打包为本机英文纠错资源。"""
from pathlib import Path
from zipfile import ZipFile, ZIP_DEFLATED
import hashlib
import json
import subprocess
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
DOWNLOAD = ROOT / "artifacts" / "proofreader-download"
OUTPUT = ROOT / "assets" / "EnglishProofreader.zip"


def prepare():
    java_source = json.loads((DOWNLOAD / "java-source.json").read_text(encoding="utf-8-sig"))
    java = DOWNLOAD / "java.zip"
    if hashlib.sha256(java.read_bytes()).hexdigest() != java_source["checksum"]:
        raise RuntimeError("Java checksum mismatch")
    compiler = DOWNLOAD / "ecj-3.40.0.jar"
    compiler_url = "https://repo.maven.apache.org/maven2/org/eclipse/jdt/ecj/3.40.0/ecj-3.40.0.jar"
    if not compiler.exists():
        urllib.request.urlretrieve(compiler_url, compiler)
    expected = urllib.request.urlopen(compiler_url + ".sha1", timeout=30).read().decode().strip()
    if hashlib.sha1(compiler.read_bytes()).hexdigest() != expected:
        raise RuntimeError("Java compiler checksum mismatch")
    runtime = next((DOWNLOAD / "java-runtime").glob("*/bin/java.exe"))
    classes = DOWNLOAD / "worker-classes"
    classes.mkdir(exist_ok=True)
    classpath = ";".join(str(path) for path in sorted((DOWNLOAD / "libs").glob("*.jar")))
    subprocess.run([str(runtime), "-jar", str(compiler), "-17", "-encoding", "UTF-8", "-cp", classpath,
                    "-d", str(classes), str(ROOT / "scripts" / "HoverLexProofreader.java")], check=True, timeout=90)
    with ZipFile(OUTPUT, "w", ZIP_DEFLATED, compresslevel=9) as target:
        with ZipFile(java) as source:
            for item in source.infolist():
                relative = item.filename.partition("/")[2]
                if relative and not item.is_dir():
                    target.writestr("java/" + relative, source.read(item))
        libraries = sorted((DOWNLOAD / "libs").glob("*.jar"))
        if not all(any(path.name == name for path in libraries) for name in ("language-en-6.6.jar", "languagetool-commandline-6.6.jar")):
            raise RuntimeError("LanguageTool English dependencies missing")
        for path in libraries:
            target.write(path, "tool/libs/" + path.name)
        target.write(classes / "HoverLexProofreader.class", "tool/worker/HoverLexProofreader.class")
        target.write(ROOT / "scripts" / "HoverLexProofreader.java", "tool/worker/HoverLexProofreader.java")
        target.write(ROOT / "scripts" / "proofreader-pom.xml", "tool/proofreader-pom.xml")
        target.write(DOWNLOAD / "LanguageTool-COPYING.txt", "tool/COPYING.txt")
        provenance = {
            "languageTool": "6.6",
            "languageToolSource": "https://repo.maven.apache.org/maven2/org/languagetool/",
            "libraries": {path.name: hashlib.sha256(path.read_bytes()).hexdigest() for path in libraries},
            "java": java_source,
        }
        target.writestr("source.json", json.dumps(provenance, indent=2))
    print(f"Prepared {OUTPUT.name}: {OUTPUT.stat().st_size / 1024 / 1024:.1f} MiB", flush=True)


if __name__ == "__main__":
    prepare()
