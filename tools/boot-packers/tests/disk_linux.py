#!/usr/bin/env python3
import re
import subprocess
import sys
from pathlib import Path

from payload_from import build_base_image, build_shim


def main() -> int:
    if len(sys.argv) != 4:
        print("usage: disk_linux.py <packer> <unpacker> <work>", file=sys.stderr)
        return 2
    packer = Path(sys.argv[1]).resolve()
    unpacker = Path(sys.argv[2]).resolve()
    work = Path(sys.argv[3]).resolve()
    work.mkdir(parents=True, exist_ok=True)

    build_base_image(work / "BaseImage")
    build_shim(work / "shim.bin")
    (work / "normal.bin").write_bytes(bytes(range(64)))
    (work / "diag.bin").write_bytes(bytes(((i * 7) & 0xFF) for i in range(64)))

    config = """[Pack]
Shim=shim.bin
Output=PackedKernel
Default=Image-01
[Image-00]
Name=Android
Type=Linux
Path=BaseImage
BaseImage=true
Align=21
[Image-01]
Name=Normal
Type=DiskLinux
Path=normal.bin
Align=3
[Image-02]
Name=Diagnostic
Type=DiskLinux
Path=diag.bin
Align=3
[Manifest]
Type=Manifest
"""
    (work / "config.ini").write_text(config, encoding="utf-8")

    subprocess.run([str(packer), "config.ini"], cwd=str(work), check=True)
    subprocess.run([str(unpacker), "PackedKernel", "unpacked"], cwd=str(work), check=True)

    unpacked = work / "unpacked"
    unpacked_cfg = unpacked / "config.cfg"
    assert unpacked_cfg.is_file(), "UnpackedConfig missing"
    cfg_text = unpacked_cfg.read_text(encoding="utf-8")
    disk_linux = re.findall(r"^\s*Type\s*=\s*DiskLinux\s*$", cfg_text, re.M)
    assert len(disk_linux) == 2, f"expected 2 DiskLinux, got {len(disk_linux)}"

    base_orig = (work / "BaseImage").read_bytes()
    base_out = (unpacked / "BaseImage").read_bytes()
    assert base_orig == base_out, "BaseImage mismatch"

    subprocess.run([str(packer), "config.cfg"], cwd=str(unpacked), check=True)
    repacked = unpacked / "RepackedKernel"
    packed = work / "PackedKernel"
    assert repacked.is_file(), "RepackedKernel missing"
    assert repacked.read_bytes() == packed.read_bytes(), "RepackedKernel != PackedKernel"
    return 0


if __name__ == "__main__":
    sys.exit(main())
