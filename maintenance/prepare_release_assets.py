#!/usr/bin/env python3
"""Validate and assemble prebuilt release assets from a tracked manifest.

Reads a publish.json manifest describing assets and assembled assets,
validates all referenced files exist with expected size and SHA-256,
assembles multi-part assets by streaming bytes into a temp file inside
the release-assets directory and atomically renaming into place, then
writes a validated upload list and emits fixed output keys for the
calling workflow. Pure offline logic: no network, subprocess, or
GitHub API calls.
"""

import argparse
import hashlib
import json
import os
import re
import sys

SAFE_NAME_RE = re.compile(r"^[A-Za-z0-9._-]+$")
SHA256_RE = re.compile(r"^[0-9a-f]{64}$")
SOURCE_COMMIT_RE = re.compile(r"^[0-9a-f]{40}$")
TAG_PREFIX = "build-inputs-v"
CHUNK = 1024 * 1024


class ValidationError(Exception):
    pass


def _safe_basename(name):
    if not isinstance(name, str) or not name:
        raise ValidationError(f"invalid name: {name!r}")
    if "\n" in name or "\r" in name or "\x00" in name:
        raise ValidationError(f"newline or NUL in name: {name!r}")
    if not SAFE_NAME_RE.match(name):
        raise ValidationError(f"unsafe basename: {name!r}")
    if name in (".", ".."):
        raise ValidationError(f"reserved name: {name!r}")
    return name


def _resolve(base_dir, name):
    _safe_basename(name)
    path = os.path.join(base_dir, name)
    real_base = os.path.realpath(base_dir)
    real_path = os.path.realpath(path)
    if not real_path.startswith(real_base + os.sep):
        raise ValidationError(f"path escapes base directory: {name!r}")
    return path


def _file_sha256(path):
    h = hashlib.sha256()
    size = 0
    with open(path, "rb") as f:
        while True:
            chunk = f.read(CHUNK)
            if not chunk:
                break
            size += len(chunk)
            h.update(chunk)
    return size, h.hexdigest()


def _verify_file(path, expected_bytes, expected_sha, label):
    if not os.path.isfile(path):
        raise ValidationError(f"{label}: missing file {os.path.basename(path)!r}")
    size, sha = _file_sha256(path)
    if size != expected_bytes:
        raise ValidationError(
            f"{label}: size mismatch for {os.path.basename(path)!r}: "
            f"expected {expected_bytes}, got {size}"
        )
    if sha != expected_sha:
        raise ValidationError(
            f"{label}: sha256 mismatch for {os.path.basename(path)!r}"
        )


def _atomic_assemble(base_dir, out_name, parts, expected_bytes, expected_sha):
    _safe_basename(out_name)
    if out_name in parts:
        raise ValidationError(f"assembled name collides with a part: {out_name!r}")
    out_path = os.path.join(base_dir, out_name)
    real_base = os.path.realpath(base_dir)
    real_out = os.path.realpath(out_path)
    if not real_out.startswith(real_base + os.sep):
        raise ValidationError(f"assembled path escapes base: {out_name!r}")
    part_paths = []
    seen = set()
    for p in parts:
        _safe_basename(p)
        if p in seen:
            raise ValidationError(f"duplicate part in assembly: {p!r}")
        seen.add(p)
        pp = os.path.join(base_dir, p)
        if not os.path.isfile(pp):
            raise ValidationError(f"assembled {out_name!r}: missing part {p!r}")
        part_paths.append(pp)
    tmp_path = os.path.join(base_dir, ".tmp-assemble-" + str(os.getpid()) + "-" + out_name)
    if os.path.exists(tmp_path):
        raise ValidationError(f"temp path already exists: {tmp_path!r}")
    h = hashlib.sha256()
    total = 0
    try:
        with open(tmp_path, "wb") as out:
            for pp in part_paths:
                with open(pp, "rb") as src:
                    while True:
                        chunk = src.read(CHUNK)
                        if not chunk:
                            break
                        total += len(chunk)
                        h.update(chunk)
                        out.write(chunk)
        if total != expected_bytes:
            raise ValidationError(
                f"assembled {out_name!r}: size mismatch: expected {expected_bytes}, got {total}"
            )
        if h.hexdigest() != expected_sha:
            raise ValidationError(f"assembled {out_name!r}: sha256 mismatch")
        os.replace(tmp_path, out_path)
    finally:
        if os.path.exists(tmp_path):
            try:
                os.remove(tmp_path)
            except OSError:
                pass


def load_and_validate(manifest_path):
    if not os.path.isfile(manifest_path):
        raise ValidationError(f"manifest not found: {manifest_path!r}")
    base_dir = os.path.dirname(os.path.abspath(manifest_path))
    with open(manifest_path, "r", encoding="utf-8") as f:
        data = json.load(f)
    if not isinstance(data, dict):
        raise ValidationError("manifest root must be an object")
    tag = data.get("tag")
    if not isinstance(tag, str) or not tag.startswith(TAG_PREFIX):
        raise ValidationError(f"tag must start with {TAG_PREFIX!r}: {tag!r}")
    suffix = tag[len(TAG_PREFIX):]
    if not suffix:
        raise ValidationError("tag has empty suffix")
    _safe_basename(suffix)
    source = data.get("sourceCommit")
    if not isinstance(source, str) or not SOURCE_COMMIT_RE.match(source):
        raise ValidationError(f"sourceCommit must be 40 lowercase hex: {source!r}")
    title = data.get("title")
    if not isinstance(title, str) or not title:
        raise ValidationError("title must be a non-empty string")
    if "\n" in title or "\r" in title:
        raise ValidationError("title must not contain newlines")
    notes_file = data.get("notesFile")
    _safe_basename(notes_file)
    notes_path = _resolve(base_dir, notes_file)
    if not os.path.isfile(notes_path):
        raise ValidationError(f"notes file not found: {notes_file!r}")

    assets = data.get("assets")
    if not isinstance(assets, list):
        raise ValidationError("assets must be a list")
    assembled = data.get("assembledAssets") or []
    if not isinstance(assembled, list):
        raise ValidationError("assembledAssets must be a list")

    reserved = {os.path.basename(manifest_path), "upload-list.txt"}
    used_names = set()
    publish_paths = []

    for idx, a in enumerate(assets):
        if not isinstance(a, dict):
            raise ValidationError(f"assets[{idx}] must be an object")
        name = _safe_basename(a.get("name"))
        if name in reserved:
            raise ValidationError(f"asset name reserved: {name!r}")
        if name in used_names:
            raise ValidationError(f"duplicate asset name: {name!r}")
        used_names.add(name)
        bytes_ = a.get("bytes")
        sha = a.get("sha256")
        if not isinstance(bytes_, int) or bytes_ < 0:
            raise ValidationError(f"asset {name!r}: invalid bytes")
        if not isinstance(sha, str) or not SHA256_RE.match(sha):
            raise ValidationError(f"asset {name!r}: invalid sha256")
        _verify_file(_resolve(base_dir, name), bytes_, sha, "asset")
        publish_paths.append(os.path.join(base_dir, name))

    for idx, a in enumerate(assembled):
        if not isinstance(a, dict):
            raise ValidationError(f"assembledAssets[{idx}] must be an object")
        name = _safe_basename(a.get("name"))
        if name in reserved:
            raise ValidationError(f"assembled name reserved: {name!r}")
        if name in used_names:
            raise ValidationError(f"duplicate asset name: {name!r}")
        used_names.add(name)
        bytes_ = a.get("bytes")
        sha = a.get("sha256")
        parts = a.get("parts")
        if not isinstance(bytes_, int) or bytes_ < 0:
            raise ValidationError(f"assembled {name!r}: invalid bytes")
        if not isinstance(sha, str) or not SHA256_RE.match(sha):
            raise ValidationError(f"assembled {name!r}: invalid sha256")
        if not isinstance(parts, list) or not parts:
            raise ValidationError(f"assembled {name!r}: parts must be non-empty list")
        for p in parts:
            _safe_basename(p)
            if p in used_names or p == name or p in reserved:
                raise ValidationError(f"assembled {name!r}: invalid part name {p!r}")
        _atomic_assemble(base_dir, name, parts, bytes_, sha)
        publish_paths.append(os.path.join(base_dir, name))

    upload_list_path = os.path.join(base_dir, "upload-list.txt")
    with open(upload_list_path, "w", encoding="utf-8", newline="\n") as f:
        for p in publish_paths:
            f.write(p + "\n")

    return {
        "tag": tag,
        "source": source,
        "title": title,
        "notes": notes_path,
        "upload_list": upload_list_path,
    }


def write_github_output(outputs, path):
    with open(path, "a", encoding="utf-8", newline="\n") as f:
        for key in ("tag", "source", "title", "notes", "upload_list"):
            val = outputs[key]
            if "\n" in val or "\r" in val:
                raise ValidationError(f"output {key!r} contains newline")
            f.write(f"{key}={val}\n")


def main(argv=None):
    parser = argparse.ArgumentParser(description="Assemble and validate release assets.")
    parser.add_argument("--manifest", required=True, help="Path to publish.json manifest")
    parser.add_argument("--github-output", required=False, help="GITHUB_OUTPUT file path")
    args = parser.parse_args(argv)
    try:
        outputs = load_and_validate(args.manifest)
    except ValidationError as e:
        print(f"ERROR: {e}", file=sys.stderr)
        return 1
    if args.github_output:
        try:
            write_github_output(outputs, args.github_output)
        except ValidationError as e:
            print(f"ERROR: {e}", file=sys.stderr)
            return 1
    print(f"tag={outputs['tag']}")
    print(f"source={outputs['source']}")
    print(f"notes={outputs['notes']}")
    print(f"upload_list={outputs['upload_list']}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
