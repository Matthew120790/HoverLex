"""下载并构建 ECDICT 离线索引。只使用 Python 标准库。"""
import csv
import hashlib
import json
import pathlib
import struct
import urllib.request

ROOT = pathlib.Path(__file__).resolve().parents[1]
SOURCE = ROOT / "research" / "ecdict.csv"
OUTPUT = ROOT / "dist" / "HoverLex" / "Dictionary"
URL = "https://raw.githubusercontent.com/skywind3000/ECDICT/master/ecdict.csv"


def write_string(target, value):
    encoded = value.encode("utf-8")
    length = len(encoded)
    while length >= 128:
        target.write(bytes([(length & 127) | 128]))
        length >>= 7
    target.write(bytes([length]))
    target.write(encoded)


def main():
    SOURCE.parent.mkdir(parents=True, exist_ok=True)
    OUTPUT.mkdir(parents=True, exist_ok=True)
    if not SOURCE.exists():
        print("Downloading ECDICT...", flush=True)
        with urllib.request.urlopen(URL, timeout=60) as response:
            with SOURCE.with_suffix(".download").open("wb") as target:
                while chunk := response.read(1024 * 1024):
                    target.write(chunk)
        SOURCE.with_suffix(".download").rename(SOURCE)
    offsets = {}
    rows = 0
    with SOURCE.open(encoding="utf-8-sig", newline="") as source, (OUTPUT / "dictionary.dat").open("wb") as data:
        for row in csv.DictReader(source):
            word = row["word"].strip()
            key = word.lower()
            if not key or key in offsets:
                continue
            offsets[key] = data.tell()
            for field in ("word", "phonetic", "translation", "definition", "tag", "exchange"):
                write_string(data, row.get(field, "").replace("\\n", "\n"))
            rows += 1
    with (OUTPUT / "dictionary.idx").open("wb") as index:
        index.write(b"HLX1" + struct.pack("<i", len(offsets)))
        for word in sorted(offsets):
            encoded = word.encode("utf-8")
            index.write(struct.pack("<H", len(encoded)))
            index.write(encoded)
            index.write(struct.pack("<q", offsets[word]))
    license_bytes = urllib.request.urlopen("https://raw.githubusercontent.com/skywind3000/ECDICT/master/LICENSE", timeout=20).read()
    (OUTPUT / "ECDICT-LICENSE.txt").write_bytes(license_bytes)
    metadata = {"source": URL, "entries": rows, "source_sha256": hashlib.sha256(SOURCE.read_bytes()).hexdigest(), "license": "MIT", "format": "HLX1"}
    (OUTPUT / "source.json").write_text(json.dumps(metadata, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"Built {rows:,} entries in {OUTPUT}", flush=True)


if __name__ == "__main__":
    main()
