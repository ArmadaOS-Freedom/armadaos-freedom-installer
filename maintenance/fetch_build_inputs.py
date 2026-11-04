#!/usr/bin/env python3
"""Fetch official upstream build inputs for EDL packaging.

Reads a schemaVersion=2 manifest, validates the entire document before
creating any output directory or performing any network I/O, then
downloads each `downloads` entry over HTTPS, verifies size and SHA-256, and
returns a mapping {id: absolute_path} for downstream orchestration.

No MSBuild props are emitted, no archives are extracted, no subprocesses,
credentials, or retries. Stdlib only.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import sys
import tempfile
import urllib.error
import urllib.request

SCHEMA_VERSION = 2

REQUIRED_IDS = (
    "platformTools",
    "python",
    "libusbPackage",
    "libusbLicense",
    "gblPrc",
    "gblRow",
)

SHA256_RE = re.compile(r"^[0-9a-f]{64}$")
NAME_RE = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]*$")
ID_RE = re.compile(r"^[A-Za-z][A-Za-z0-9]*$")
TIMEOUT = 60
CHUNK = 65536


class FetchError(Exception):
    pass


def _die(msg: str) -> "NoReturn":
    raise FetchError(msg)


def _is_positive_int(value: object) -> bool:
    return isinstance(value, int) and not isinstance(value, bool) and value > 0


def _validate_download(idx: int, entry: object) -> dict:
    if not isinstance(entry, dict):
        _die(f"downloads[{idx}] must be a JSON object")
    download_id = entry.get("id")
    name = entry.get("name")
    url = entry.get("url")
    size = entry.get("bytes")
    digest = entry.get("sha256")
    if not isinstance(download_id, str) or not ID_RE.match(download_id):
        _die(f"downloads[{idx}] invalid id: {download_id!r}")
    if not isinstance(name, str) or not NAME_RE.match(name) or name in (".", "..") or "/" in name or "\\" in name:
        _die(f"downloads[{idx}] invalid name: {name!r}")
    if not isinstance(url, str) or not url.startswith("https://"):
        _die(f"downloads[{idx}] url must be HTTPS: {url!r}")
    if not _is_positive_int(size):
        _die(f"downloads[{idx}] invalid bytes: {size!r}")
    if not isinstance(digest, str) or not SHA256_RE.match(digest):
        _die(f"downloads[{idx}] invalid sha256: {digest!r}")
    # Preserve additional fields unchanged; only normalised view for internal use.
    view = dict(entry)
    view["id"] = download_id
    view["name"] = name
    view["url"] = url
    view["bytes"] = size
    view["sha256"] = digest
    return view


def load_manifest(path: str) -> dict:
    try:
        with open(path, "r", encoding="utf-8") as handle:
            data = json.load(handle)
    except (OSError, json.JSONDecodeError) as exc:
        _die(f"cannot read manifest {path!r}: {exc}")
    if not isinstance(data, dict):
        _die("manifest root must be a JSON object")
    if data.get("schemaVersion") != SCHEMA_VERSION:
        _die(f"unsupported schemaVersion: {data.get('schemaVersion')!r}")
    downloads = data.get("downloads")
    if not isinstance(downloads, list):
        _die("manifest 'downloads' must be a list")

    seen_ids: set[str] = set()
    seen_names: set[str] = set()
    normalised: list[dict] = []
    for idx, entry in enumerate(downloads):
        view = _validate_download(idx, entry)
        if view["id"] in seen_ids:
            _die(f"duplicate download id: {view['id']!r}")
        if view["name"] in seen_names:
            _die(f"duplicate download name: {view['name']!r}")
        seen_ids.add(view["id"])
        seen_names.add(view["name"])
        normalised.append(view)

    missing = [rid for rid in REQUIRED_IDS if rid not in seen_ids]
    if missing:
        _die(f"manifest missing required download ids: {missing}")
    extra = sorted(seen_ids - set(REQUIRED_IDS))
    if extra:
        _die(f"manifest has unexpected download ids: {extra}")

    # Retain opaque extra data unchanged.
    result = dict(data)
    result["downloads"] = normalised
    if "sources" in data and not isinstance(data["sources"], list):
        _die("manifest 'sources' must be a list when present")
    if "vendorFiles" in data and not isinstance(data["vendorFiles"], list):
        _die("manifest 'vendorFiles' must be a list when present")
    return result


def hash_file(path: str) -> tuple[int, str]:
    digest = hashlib.sha256()
    total = 0
    with open(path, "rb") as handle:
        while True:
            chunk = handle.read(CHUNK)
            if not chunk:
                break
            total += len(chunk)
            digest.update(chunk)
    return total, digest.hexdigest()


def _verify_cached(path: str, entry: dict) -> bool:
    try:
        size, digest = hash_file(path)
    except OSError:
        return False
    return size == entry["bytes"] and digest == entry["sha256"]


def _download(entry: dict, dest: str) -> None:
    request = urllib.request.Request(
        entry["url"], headers={"User-Agent": "fetch-build-inputs/2"}
    )
    expected = entry["bytes"]
    digest = hashlib.sha256()
    total = 0
    part_path = None
    try:
        fd, part_path = tempfile.mkstemp(
            prefix="." + entry["name"] + ".", suffix=".part", dir=os.path.dirname(dest)
        )
    except OSError as exc:
        _die(f"cannot create part file for {entry['id']}: {exc}")
    try:
        with os.fdopen(fd, "wb") as handle:
            with urllib.request.urlopen(request, timeout=TIMEOUT) as response:
                while True:
                    chunk = response.read(CHUNK)
                    if not chunk:
                        break
                    total += len(chunk)
                    if total > expected:
                        _die(
                            f"size overflow for {entry['id']}: expected {expected} bytes, "
                            f"received more"
                        )
                    digest.update(chunk)
                    handle.write(chunk)
            handle.flush()
            os.fsync(handle.fileno())
        if total != expected:
            _die(
                f"size mismatch for {entry['id']}: expected {expected}, got {total}"
            )
        actual = digest.hexdigest()
        if actual != entry["sha256"]:
            _die(
                f"sha256 mismatch for {entry['id']}: expected {entry['sha256']}, "
                f"got {actual}"
            )
        os.replace(part_path, dest)
    finally:
        try:
            if part_path is not None and os.path.exists(part_path):
                os.unlink(part_path)
        except OSError:
            pass


def fetch_all(manifest: dict, output_dir: str, verify_only: bool = False) -> dict[str, str]:
    """Fetch or verify all downloads; returns {id: absolute file path}.

    `verify_only=True` never creates `output_dir`, never writes, and requires
    every cached file to match. Non-verify mode creates `output_dir` only after
    the caller has run `load_manifest` on a validated manifest (validation is
    performed again here for safety, without touching FS/network).
    """
    if not isinstance(manifest, dict):
        _die("manifest must be a dict from load_manifest")
    downloads = manifest.get("downloads")
    if not isinstance(downloads, list):
        _die("manifest 'downloads' missing or not a list")
    # Revalidate the already-normalised list cheaply (defensive).
    seen_ids: set[str] = set()
    seen_names: set[str] = set()
    for idx, entry in enumerate(downloads):
        if not isinstance(entry, dict):
            _die(f"downloads[{idx}] must be a JSON object")
        view = _validate_download(idx, entry)
        if view["id"] in seen_ids:
            _die(f"duplicate download id: {view['id']!r}")
        if view["name"] in seen_names:
            _die(f"duplicate download name: {view['name']!r}")
        seen_ids.add(view["id"])
        seen_names.add(view["name"])
    missing = [rid for rid in REQUIRED_IDS if rid not in seen_ids]
    if missing:
        _die(f"manifest missing required download ids: {missing}")
    extra = sorted(seen_ids - set(REQUIRED_IDS))
    if extra:
        _die(f"manifest has unexpected download ids: {extra}")

    output_abs = os.path.abspath(output_dir)
    results: dict[str, str] = {}

    if verify_only:
        if not os.path.isdir(output_abs):
            _die(f"output directory does not exist (verify-only): {output_abs!r}")
        for entry in downloads:
            target = os.path.join(output_abs, entry["name"])
            if not os.path.isfile(target):
                _die(f"missing cached file (verify-only): {entry['id']}")
            if not _verify_cached(target, entry):
                _die(f"cached file failed verification (verify-only): {entry['id']}")
            results[entry["id"]] = target
        return results

    if not os.path.isdir(output_abs):
        try:
            os.makedirs(output_abs, exist_ok=False)
        except FileExistsError:
            # Race: another process created it between the check and makedirs.
            pass
        except OSError as exc:
            _die(f"cannot create output directory {output_abs!r}: {exc}")

    for entry in downloads:
        target = os.path.join(output_abs, entry["name"])
        if os.path.isfile(target) and _verify_cached(target, entry):
            print(f"cached: {entry['id']}")
        else:
            print(f"download: {entry['id']}")
            try:
                _download(entry, target)
            except FetchError:
                raise
            except (urllib.error.URLError, OSError) as exc:
                _die(f"download failed for {entry['id']}: {exc}")
        # Existing target retained on failure: _download only replaces on success.
        results[entry["id"]] = target
    return results


def _parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output-dir", required=True)
    parser.add_argument(
        "--manifest",
        default=os.path.join(
            os.path.dirname(os.path.abspath(__file__)), "build-inputs.json"
        ),
    )
    parser.add_argument("--verify-only", action="store_true")
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = _parse_args(argv if argv is not None else sys.argv[1:])
    try:
        manifest = load_manifest(args.manifest)
        results = fetch_all(manifest, args.output_dir, verify_only=args.verify_only)
    except FetchError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1
    except (urllib.error.URLError, OSError) as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1
    action = "verified" if args.verify_only else "ready"
    print(f"{action}: {len(results)} build inputs in {os.path.abspath(args.output_dir)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
