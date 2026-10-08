"""从 LanguageTool 官方 ZIP 读取命令行组件，不下载其他语言词库。"""
import io
import urllib.request
from pathlib import Path
from zipfile import ZipFile

ROOT = Path(__file__).resolve().parents[1]
DEST = ROOT / "artifacts" / "proofreader-download"
URL = "https://languagetool.org/download/LanguageTool-6.6.zip"


class RemoteArchive(io.RawIOBase):
    def __init__(self):
        self.position = 0
        request = urllib.request.Request(URL, method="HEAD", headers={"User-Agent": "HoverLex build"})
        with urllib.request.urlopen(request, timeout=30) as response:
            self.size = int(response.headers["Content-Length"])

    def seek(self, offset, whence=0):
        self.position = offset if whence == 0 else self.position + offset if whence == 1 else self.size + offset
        return self.position

    def tell(self):
        return self.position

    def read(self, size=-1):
        size = min(self.size - self.position, size if size >= 0 else self.size)
        if size <= 0:
            return b""
        request = urllib.request.Request(URL, headers={"User-Agent": "HoverLex build", "Range": f"bytes={self.position}-{self.position + size - 1}"})
        with urllib.request.urlopen(request, timeout=60) as response:
            data = response.read()
            if response.status != 206 or len(data) != size:
                raise RuntimeError("Invalid official ZIP range")
        self.position += len(data)
        return data


def main():
    output = DEST / "libs" / "languagetool-commandline-6.6.jar"
    with ZipFile(RemoteArchive()) as archive:
        # ZipFile 校验 CRC；原始 jar 不做修改。
        output.write_bytes(archive.read("LanguageTool-6.6/languagetool-commandline.jar"))
        (DEST / "LanguageTool-COPYING.txt").write_bytes(archive.read("LanguageTool-6.6/COPYING.txt"))
    print("Official command-line component verified", flush=True)


if __name__ == "__main__":
    main()
