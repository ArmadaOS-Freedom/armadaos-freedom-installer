#!/usr/bin/env python3
"""Publish a verified, self-contained Linux Armada BootTool."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import stat
import struct
import subprocess
import sys
import xml.etree.ElementTree as ET

RIDS = {"linux-arm64": 183, "linux-x64": 62}
KEY_HASH = "6a224754880a57ab9cbd308267cd157d94cf05a1c8cb851aec4090e045d24121"
RUNTIMES = ("libcoreclr.so", "libhostfxr.so", "libhostpolicy.so")


def sha256(path):
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(65536), b""):
            digest.update(chunk)
    return digest.hexdigest()


def check_elf(path, machine):
    with path.open("rb") as stream:
        data = stream.read(20)
    if len(data) < 20 or data[:4] != b"\x7fELF" or data[4] != 2 or data[5] != 1:
        raise ValueError(f"not a 64-bit little-endian ELF: {path}")
    if struct.unpack_from("<H", data, 18)[0] != machine:
        raise ValueError(f"wrong ELF architecture: {path}")


def output_files(root):
    entries = []
    for directory, dirs, names in os.walk(root):
        base = Path(directory)
        for name in dirs + names:
            path = base / name
            if path.is_symlink():
                raise ValueError(f"symlink in published output: {path}")
        for name in names:
            path = base / name
            if not stat.S_ISREG(path.stat().st_mode):
                raise ValueError(f"non-regular published output: {path}")
            entries.append(path)
    return sorted(entries, key=lambda p: p.relative_to(root).as_posix())


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", required=True)
    parser.add_argument("--inputs", required=True)
    parser.add_argument("--rid", choices=tuple(RIDS), default="linux-arm64")
    args = parser.parse_args()
    try:
        repo = Path(__file__).resolve().parent.parent
        sys.path.insert(0, str(repo / "maintenance"))
        import fetch_build_inputs as fetch
        output, inputs = Path(os.path.abspath(args.output)), Path(os.path.abspath(args.inputs))
        if output.is_symlink():
            raise ValueError("--output must not be a symlink")
        if output.exists() and (not output.is_dir() or any(output.iterdir())):
            raise ValueError("--output must not exist or must be an empty directory")
        manifest = fetch.load_manifest(str(repo / "maintenance/build-inputs.json"))
        gbl = fetch.fetch_all(manifest, str(inputs), ids=["gblPrc", "gblRow"])
        key_rel = "vendor/aosp/testkey_rsa4096.pem"
        records = [v for v in manifest.get("vendorFiles", []) if isinstance(v, dict) and v.get("path") == key_rel]
        if len(records) != 1:
            raise ValueError("manifest must contain exactly one signing-key vendorFiles entry")
        key = repo / key_rel
        record, size = records[0], key.stat().st_size
        digest = sha256(key)
        if size != record.get("bytes") or digest != record.get("sha256") or digest != KEY_HASH:
            raise ValueError("AOSP test key failed manifest or known-public-hash verification")
        version = ET.parse(repo / "Directory.Build.props").getroot().findtext("./PropertyGroup/Version")
        if not version or not version.strip():
            raise ValueError("Version missing from Directory.Build.props")
        version = version.strip()
        output.mkdir(parents=True, exist_ok=True)
        project = repo / "src/FreedomInstaller.BootTool/FreedomInstaller.BootTool.csproj"
        subprocess.run([
            "dotnet", "publish", str(project), "-c", "Release", "-r", args.rid,
            "--self-contained", "true", "--output", str(output),
            "-p:PublishTrimmed=false", "-p:PublishAot=false", "-p:PublishSingleFile=false",
            "-p:UseAppHost=true", "-p:InvariantGlobalization=true", "-p:DebugType=None",
            "-p:DebugSymbols=false", f"-p:BundledAvbSigningKey={key}",
            f"-p:BundledGblPrcEfi={gbl['gblPrc']}", f"-p:BundledGblRowEfi={gbl['gblRow']}",
        ], check=True)
        for name in ("armada-boot-tool",) + RUNTIMES:
            check_elf(output / name, RIDS[args.rid])
        with (output / "armada-boot-tool.runtimeconfig.json").open(encoding="utf-8") as stream:
            options = json.load(stream).get("runtimeOptions", {})
        included = options.get("includedFrameworks", [])
        if "framework" in options or "frameworks" in options or not isinstance(included, list) or not any(
                isinstance(f, dict) and f.get("name") == "Microsoft.NETCore.App" for f in included):
            raise ValueError("runtimeconfig does not prove a self-contained Microsoft.NETCore.App runtime")
        for suffix in (".dll", ".deps.json"):
            if not (output / ("armada-boot-tool" + suffix)).is_file():
                raise ValueError(f"missing published armada-boot-tool{suffix}")
        licenses = output / "licenses"
        licenses.mkdir()
        for source, name in (("LICENSE", "FreedomInstaller-GPL3.txt"),
                ("docs/GblRuntime-NOTICE.txt", "GblRuntime-NOTICE.txt"),
                ("docs/GblRuntime-LICENSE.txt", "GblRuntime-LICENSE.txt"),
                ("docs/SharpCompress-LICENSE.txt", "SharpCompress-LICENSE.txt")):
            shutil.copyfile(repo / source, licenses / name)
        dotnet = shutil.which("dotnet")
        if not dotnet:
            raise ValueError("dotnet not found after publish")
        sdk = Path(dotnet).resolve().parent
        notices = next((n for n in ("ThirdPartyNotices.txt", "THIRD-PARTY-NOTICES.TXT") if (sdk / n).is_file()), None)
        if not (sdk / "LICENSE.txt").is_file() or notices is None:
            raise ValueError(".NET SDK license or third-party notices missing")
        shutil.copyfile(sdk / "LICENSE.txt", licenses / "DotNet-LICENSE.txt")
        shutil.copyfile(sdk / notices, licenses / "DotNet-ThirdPartyNotices.txt")
        files = [{"path": p.relative_to(output).as_posix(), "bytes": p.stat().st_size,
                  "sha256": sha256(p)} for p in output_files(output)]
        document = {"schemaVersion": 1, "component": "armada-boot-tool", "version": version,
                    "runtimeIdentifier": args.rid, "files": files}
        (output / "manifest.json").write_text(json.dumps(document, indent=2) + "\n", encoding="utf-8")
        print(json.dumps({"output": str(output.resolve()), "rid": args.rid,
                          "version": version, "fileCount": len(files)}, separators=(",", ":")))
        return 0
    except (fetch.FetchError, OSError, ValueError, ET.ParseError, json.JSONDecodeError,
            subprocess.CalledProcessError) as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
