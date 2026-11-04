#!/usr/bin/env python3
"""Download and verify public build inputs for build 0.7.6.

Reads a manifest, validates it, downloads each referenced archive over HTTPS,
verifies size and SHA-256, and emits an MSBuild props file with absolute paths
for the nine expected Bundled* properties.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import sys
import tempfile
import urllib.request
import xml.etree.ElementTree as ET

EXPECTED_PROPERTIES = (
    "BundledPlatformToolsArchive",
    "BundledEdlArchive",
    "BundledEdlG5Archive",
    "BundledQualcommDriverArchive",
    "BundledEdlRawPacker",
    "BundledEdlRawUnpacker",
    "BundledAvbSigningKey",
    "BundledGblPrcEfi",
    "BundledGblRowEfi",
)

SCHEMA_VERSION = 1
RELEASE = "build-inputs-v0.7.6"
SHA256_RE = re.compile(r"^[0-9a-f]{64}$")
NAME_RE = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]*$")
TIMEOUT = 60
CHUNK = 65536


class FetchError(Exception):
    pass


def _die(msg: str) -> "NoReturn":
    raise FetchError(msg)


def load_manifest(path: str) -> list[dict]:
    try:
        with open(path, "r", encoding="utf-8") as fh:
            data = json.load(fh)
    except (OSError, json.JSONDecodeError) as exc:
        _die(f"cannot read manifest {path!r}: {exc}")
    if not isinstance(data, dict):
        _die("manifest root must be a JSON object")
    if data.get("schemaVersion") != SCHEMA_VERSION:
        _die(f"unsupported schemaVersion: {data.get('schemaVersion')!r}")
    if data.get("release") != RELEASE:
        _die(f"unexpected release: {data.get('release')!r}")
    files = data.get("files")
    if not isinstance(files, list):
        _die("manifest 'files' must be a list")
    seen_props: set[str] = set()
    seen_names: set[str] = set()
    result: list[dict] = []
    for idx, entry in enumerate(files):
        if not isinstance(entry, dict):
            _die(f"files[{idx}] must be an object")
        name = entry.get("name")
        prop = entry.get("property")
        size = entry.get("bytes")
        digest = entry.get("sha256")
        url = entry.get("url")
        if not isinstance(name, str) or not NAME_RE.match(name) or name in (".", ".."):
            _die(f"files[{idx}] has invalid name: {name!r}")
        if name in seen_names:
            _die(f"duplicate file name: {name!r}")
        seen_names.add(name)
        if prop not in EXPECTED_PROPERTIES:
            _die(f"files[{idx}] unexpected property: {prop!r}")
        if prop in seen_props:
            _die(f"duplicate property: {prop!r}")
        seen_props.add(prop)
        if not isinstance(size, int) or isinstance(size, bool) or size <= 0:
            _die(f"files[{idx}] invalid bytes: {size!r}")
        if not isinstance(digest, str) or not SHA256_RE.match(digest):
            _die(f"files[{idx}] invalid sha256: {digest!r}")
        if not isinstance(url, str) or not url.startswith("https://"):
            _die(f"files[{idx}] url must be HTTPS: {url!r}")
        expected_suffix = "/" + name
        if not url.endswith(expected_suffix):
            _die(f"files[{idx}] url must end with {expected_suffix!r}")
        result.append({"name": name, "property": prop, "bytes": size, "sha256": digest, "url": url})
    missing = set(EXPECTED_PROPERTIES) - seen_props
    if missing:
        _die(f"manifest missing properties: {sorted(missing)}")
    return result


def _hash_file(path: str) -> tuple[int, str]:
    h = hashlib.sha256()
    total = 0
    with open(path, "rb") as fh:
        while True:
            chunk = fh.read(CHUNK)
            if not chunk:
                break
            total += len(chunk)
            h.update(chunk)
    return total, h.hexdigest()


def verify_cached(path: str, entry: dict) -> bool:
    try:
        size, digest = _hash_file(path)
    except OSError:
        return False
    return size == entry["bytes"] and digest == entry["sha256"]


def download(entry: dict, dest: str) -> None:
    req = urllib.request.Request(entry["url"], headers={"User-Agent": "fetch-build-inputs/1"})
    expected = entry["bytes"]
    h = hashlib.sha256()
    total = 0
    tmp_path = None
    try:
        with urllib.request.urlopen(req, timeout=TIMEOUT) as resp:
            tmp = tempfile.NamedTemporaryFile(
                mode="wb", dir=os.path.dirname(dest), prefix="." + entry["name"] + ".", suffix=".part", delete=False
            )
            tmp_path = tmp.name
            with tmp:
                while True:
                    chunk = resp.read(CHUNK)
                    if not chunk:
                        break
                    total += len(chunk)
                    if total > expected:
                        raise FetchError(f"size overflow for {entry['name']}: downloaded more than {expected} bytes")
                    h.update(chunk)
                    tmp.write(chunk)
            if total != expected:
                raise FetchError(f"size mismatch for {entry['name']}: expected {expected}, got {total}")
            actual = h.hexdigest()
            if actual != entry["sha256"]:
                raise FetchError(f"sha256 mismatch for {entry['name']}: expected {entry['sha256']}, got {actual}")
            os.replace(tmp_path, dest)
            tmp_path = None
    finally:
        if tmp_path is not None and os.path.exists(tmp_path):
            try:
                os.unlink(tmp_path)
            except OSError:
                pass


def write_props(output_dir: str, entries: list[dict]) -> str:
    ns = "http://schemas.microsoft.com/developer/msbuild/2003"
    ET.register_namespace("", ns)
    project = ET.Element(f"{{{ns}}}Project")
    group = ET.SubElement(project, f"{{{ns}}}PropertyGroup")
    for entry in entries:
        value = os.path.abspath(os.path.join(output_dir, entry["name"]))
        el = ET.SubElement(group, f"{{{ns}}}{entry['property']}")
        el.text = value
    tree = ET.ElementTree(project)
    target = os.path.join(output_dir, "build-inputs.props")
    tmp = tempfile.NamedTemporaryFile(
        mode="wb", dir=output_dir, prefix=".build-inputs.", suffix=".part", delete=False
    )
    try:
        with tmp:
            tree.write(tmp, encoding="utf-8", xml_declaration=True)
            tmp.write(b"\n")
        os.replace(tmp.name, target)
    except Exception:
        try:
            os.unlink(tmp.name)
        except OSError:
            pass
        raise
    return target


def run(args: argparse.Namespace) -> int:
    output_dir = args.output_dir
    entries = load_manifest(args.manifest)
    if args.verify_only:
        if not os.path.isdir(output_dir):
            _die(f"output directory does not exist: {output_dir!r}")
        for entry in entries:
            target = os.path.join(output_dir, entry["name"])
            if not os.path.isfile(target):
                _die(f"missing cached file (verify-only): {entry['name']}")
            if not verify_cached(target, entry):
                _die(f"cached file failed verification (verify-only): {entry['name']}")
        print(f"verified {len(entries)} build inputs")
        return 0
    if not os.path.isdir(output_dir):
        os.makedirs(output_dir, exist_ok=True)
    for entry in entries:
        target = os.path.join(output_dir, entry["name"])
        if os.path.isfile(target) and verify_cached(target, entry):
            print(f"cached: {entry['name']}")
            continue
        print(f"download: {entry['name']}")
        download(entry, target)
    props_path = write_props(output_dir, entries)
    print(f"wrote {props_path}")
    return 0


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output-dir", required=True)
    parser.add_argument("--manifest", default=os.path.join(os.path.dirname(os.path.abspath(__file__)), "build-inputs.json"))
    parser.add_argument("--verify-only", action="store_true")
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv if argv is not None else sys.argv[1:])
    try:
        return run(args)
    except FetchError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1
    except (urllib.error.URLError, OSError) as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
