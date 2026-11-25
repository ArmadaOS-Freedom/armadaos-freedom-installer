#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-3.0-only
"""Build a complete installer release with shared version and offline inputs."""

import argparse
import hashlib
import json
from pathlib import Path
import platform
import plistlib
import re
import shutil
import struct
import subprocess
import sys
import xml.etree.ElementTree as ET

import build_installer as windows
import build_installer_macos as macos


def release_version(value):
    if (not re.fullmatch(r"(?:0|[1-9][0-9]{0,4})(?:\.(?:0|[1-9][0-9]{0,4})){2}", value)
            or any(int(part) > 65535 for part in value.split("."))):
        raise argparse.ArgumentTypeError("expected a three-part numeric installer version")
    return value


def version_for_run(initial_version, run_number):
    if run_number < 1:
        raise ValueError("workflow run number must be positive")
    major, minor, patch = map(int, release_version(initial_version).split("."))
    return release_version(f"{major}.{minor}.{patch + run_number - 1}")


def source_revision(value):
    if not re.fullmatch(r"[0-9a-f]{40}", value):
        raise argparse.ArgumentTypeError("expected a full Git commit SHA")
    return value


def write_release_props(props_path, version, revision, offline_properties):
    props = ET.parse(props_path)
    group = ET.SubElement(props.getroot(), "PropertyGroup")
    for name, value in {
        "Version": version,
        "SourceRevisionId": revision,
        **offline_properties,
    }.items():
        ET.SubElement(group, name).text = value
    props.write(props_path, encoding="utf-8", xml_declaration=True)


def verify_payload(binary, version, revision, script, bundle, *, windows_binary):
    data = binary.read_bytes()
    informational_version = f"{version}+{revision}"
    if windows_binary:
        pe_offset = struct.unpack_from("<I", data, 0x3C)[0]
        if data[:2] != b"MZ" or data[pe_offset:pe_offset + 6] != b"PE\0\0\x64\x86":
            raise SystemExit("Windows release is not an x64 executable")
        marker = informational_version.encode("utf-16le")
    else:
        marker = informational_version.encode("utf-8")
    if marker not in data:
        raise SystemExit(f"Release must report {informational_version}")
    for resource in (script, bundle):
        if resource.read_bytes() not in data:
            raise SystemExit(f"Release does not embed the validated {resource.name}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--platform", required=True, choices=("win-x64", "macos-arm64"))
    parser.add_argument("--version", required=True, type=release_version)
    parser.add_argument("--source-revision", required=True, type=source_revision)
    parser.add_argument("--offline-dir", required=True, type=Path)
    parser.add_argument("--work-dir", required=True, type=Path)
    parser.add_argument("--output-dir", required=True, type=Path)
    args = parser.parse_args()

    on_mac = args.platform == "macos-arm64"
    if on_mac and (sys.platform != "darwin" or platform.machine() != "arm64"):
        parser.error("the macOS release uses Xcode and must run on Apple silicon")

    source = Path(__file__).resolve().parents[1]
    work_dir = args.work_dir.resolve()
    work_dir.mkdir(parents=True, exist_ok=True)
    dist = args.output_dir.resolve()
    script = (args.offline_dir / "armada-offline-bootstrap").resolve(strict=True)
    bundle = (args.offline_dir / "armada-offline-updater.tar.gz").resolve(strict=True)
    name = f"ArmadaOS-Freedom-Installer-{args.version}"
    env = windows._make_controlled_env(work_dir)

    if on_mac:
        # The ordinary Mac build can connect to a tablet's existing updater.
        # The release embeds the same validated pair as the Windows build.
        offline, updater_version, _ = windows._validate_offline_inputs(script, bundle, work_dir)
        record = macos.prepare(source, work_dir)
        offline_properties = {key: value["path"] for key, value in offline.items()}
        offline_properties["BundledOfflineUpdaterVersion"] = updater_version
    else:
        record = windows.prepare(source, work_dir, offline_bootstrap_script=script,
                                 offline_updater_bundle=bundle)
        updater_version = record["offlineUpdaterVersion"]
        offline_properties = {}

    write_release_props(record["propsPath"], args.version, args.source_revision, offline_properties)
    dist.mkdir(parents=True, exist_ok=True)
    if on_mac:
        app = macos.publish(source, record, work_dir / "published-app", env)
        contents = app / "Contents"
        with (contents / "Info.plist").open("rb") as stream:
            info = plistlib.load(stream)
        if (info["CFBundleShortVersionString"] != args.version
                or info["CFBundleVersion"] != args.version):
            raise SystemExit("macOS bundle version differs from the release version")
        launcher = contents / "MacOS" / info["CFBundleExecutable"]
        if subprocess.check_output(["lipo", "-archs", launcher], text=True).strip() != "arm64":
            raise SystemExit("Mac installer is not Apple silicon arm64")
        verify_payload(launcher.with_suffix(".dll"), args.version, args.source_revision,
                       script, bundle, windows_binary=False)
        artifact = dist / f"{name}-macOS-arm64.zip"
        subprocess.run(["ditto", "-c", "-k", "--sequesterRsrc", "--keepParent", app, artifact],
                       check=True)
    else:
        publish_dir = work_dir / "published-exe"
        windows._default_publish(record, publish_dir, env)
        executable = publish_dir / "FreedomInstaller.App.exe"
        verify_payload(executable, args.version, args.source_revision, script, bundle,
                       windows_binary=True)
        artifact = dist / f"{name}.exe"
        shutil.copyfile(executable, artifact)

    proof = {
        "installerVersion": args.version,
        "installerCommit": args.source_revision,
        "offlineUpdaterVersion": updater_version,
        "platform": args.platform,
    }
    with artifact.open("rb") as stream:
        artifact_hash = hashlib.file_digest(stream, "sha256").hexdigest()
    (dist / f"{artifact.name}.sha256").write_text(f"{artifact_hash}  {artifact.name}\n", encoding="utf-8")
    (dist / "build-proof.json").write_text(json.dumps(proof, indent=2) + "\n", encoding="utf-8")
    print(f"Verified {artifact.name} with updater {updater_version}")


if __name__ == "__main__":
    main()
