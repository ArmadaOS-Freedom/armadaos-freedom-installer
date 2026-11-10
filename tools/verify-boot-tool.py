#!/usr/bin/env python3
"""Verify packaged Linux ARM64 Armada BootTool layout and launch contract."""
import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import stat
import struct
import subprocess
import sys

RUNTIMES = ("libcoreclr.so", "libhostfxr.so", "libhostpolicy.so")
LICENSES = (
    "licenses/FreedomInstaller-GPL3.txt",
    "licenses/GblRuntime-NOTICE.txt",
    "licenses/GblRuntime-LICENSE.txt",
    "licenses/SharpCompress-LICENSE.txt",
    "licenses/DotNet-LICENSE.txt",
    "licenses/DotNet-ThirdPartyNotices.txt",
)


def sha256(path):
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(65536), b""):
            digest.update(chunk)
    return digest.hexdigest()


def check_elf_arm64(path):
    with path.open("rb") as stream:
        data = stream.read(20)
    if len(data) < 20 or data[:4] != b"\x7fELF" or data[4] != 2 or data[5] != 1:
        raise ValueError(f"not a 64-bit little-endian ELF: {path}")
    if struct.unpack_from("<H", data, 18)[0] != 183:
        raise ValueError(f"not an ARM64 (e_machine 183) ELF: {path}")


def scan_files(root):
    found = {}
    for cur, dirs, files in os.walk(root):
        base = Path(cur)
        for name in dirs + files:
            p = base / name
            if p.is_symlink():
                raise ValueError(f"symlink prohibited: {p}")
        for name in files:
            p = base / name
            if not stat.S_ISREG(p.stat().st_mode):
                raise ValueError(f"non-regular file: {p}")
            rel = p.relative_to(root).as_posix()
            if rel != "manifest.json":
                found[rel] = p
    return found


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", help="Published boot-tool directory")
    args = parser.parse_args()
    try:
        root = Path(os.path.abspath(args.directory))
        if not root.is_dir() or root.is_symlink():
            raise ValueError("target must be an existing non-symlink directory")
        manifest_path = root / "manifest.json"
        if not manifest_path.is_file() or manifest_path.is_symlink():
            raise ValueError("missing or invalid manifest.json")
        doc = json.loads(manifest_path.read_text(encoding="utf-8"))
        if not isinstance(doc, dict) or doc.get("schemaVersion") != 1 or doc.get("component") != "armada-boot-tool":
            raise ValueError("invalid manifest schemaVersion or component")
        if not isinstance(doc.get("version"), str) or not doc["version"].strip():
            raise ValueError("invalid manifest version")
        if doc.get("runtimeIdentifier") != "linux-arm64":
            raise ValueError("manifest runtimeIdentifier must be linux-arm64")
        records = doc.get("files")
        if not isinstance(records, list) or not records:
            raise ValueError("manifest files must be a non-empty list")

        actual = scan_files(root)
        seen = set()
        for rec in records:
            if not isinstance(rec, dict):
                raise ValueError("manifest file entry must be an object")
            rel = rec.get("path")
            size = rec.get("bytes")
            digest = rec.get("sha256")
            if not isinstance(rel, str) or not rel or "\\" in rel or rel.startswith("/"):
                raise ValueError(f"invalid path in manifest: {rel!r}")
            pure = PurePosixPath(rel)
            if pure.as_posix() != rel or "." in pure.parts or ".." in pure.parts or rel == "manifest.json":
                raise ValueError(f"non-canonical or forbidden manifest path: {rel}")
            if rel in seen:
                raise ValueError(f"duplicate manifest path: {rel}")
            seen.add(rel)
            if type(size) is not int or size < 0:
                raise ValueError(f"invalid byte length for {rel}")
            if not isinstance(digest, str) or len(digest) != 64 or any(c not in "0123456789abcdef" for c in digest):
                raise ValueError(f"invalid sha256 for {rel}")
            if rel not in actual:
                raise ValueError(f"manifest file missing from disk: {rel}")
            p = actual[rel]
            if p.stat().st_size != size or sha256(p) != digest:
                raise ValueError(f"file size or sha256 mismatch: {rel}")

        if seen != set(actual.keys()):
            extra = sorted(set(actual.keys()) - seen)
            raise ValueError(f"unlisted files in directory: {extra}")

        for req in ("armada-boot-tool", "armada-boot-tool.dll", "armada-boot-tool.deps.json",
                    "armada-boot-tool.runtimeconfig.json") + RUNTIMES + LICENSES:
            if req not in seen:
                raise ValueError(f"required file missing: {req}")
        for elf in ("armada-boot-tool",) + RUNTIMES:
            check_elf_arm64(root / elf)

        rc = json.loads((root / "armada-boot-tool.runtimeconfig.json").read_text(encoding="utf-8"))
        opts = rc.get("runtimeOptions", {})
        inc = opts.get("includedFrameworks", [])
        if "framework" in opts or "frameworks" in opts or not isinstance(inc, list) or not any(
                isinstance(f, dict) and f.get("name") == "Microsoft.NETCore.App" for f in inc):
            raise ValueError("runtimeconfig is not self-contained Microsoft.NETCore.App")

        app = root / "armada-boot-tool"
        if not os.access(app, os.X_OK):
            raise ValueError("armada-boot-tool is not executable")
        res = subprocess.run([str(app)], cwd=str(root), env=os.environ.copy(),
                             capture_output=True, text=True, timeout=30)
        if res.returncode != 2 or res.stdout != "":
            raise ValueError(f"expected exit 2 and empty stdout, got exit {res.returncode}")
        err = json.loads(res.stderr)
        if not isinstance(err, dict) or "error" not in err or "usage" not in str(err.get("error", "")).lower():
            raise ValueError(f"unexpected stderr JSON payload: {res.stderr.strip()}")
        print(json.dumps({"verified": str(root), "files": len(records), "version": doc["version"]}, separators=(",", ":")))
        return 0
    except (OSError, ValueError, json.JSONDecodeError, subprocess.SubprocessError) as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
