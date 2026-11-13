#!/usr/bin/env python3
"""Unified source build entrypoint for the Freedom Installer (win-x64).

Phases
------
0. optional paired offline bootstrap inputs staged into the isolated run
   directory with bounded streaming and validated there (cheap, before any
   build)
1. manifest load + strict vendor file validation (before any download/compile)
2. official input fetch into a real cache, per-run isolated directories
3. compile all three tools from checked-in source
4. extract libusb + combined LICENSE, hash fresh tool outputs, generate pins
5. build Qualcomm driver ZIP
6. package the G4, G5 and TB710FU EDL toolchains via package_edl
7. emit MSBuild props + build record (atomic last-success pointers)

After prepare(), the CLI optionally runs `dotnet publish` of the WPF app against
the freshly generated props file.  A publish requires the paired
--offline-bootstrap-script and --offline-updater-bundle inputs; they are local
artefacts of the system offline updater bundle build, never downloaded here.
The pair is copied into the per-run directory before it is validated, and the
props, the record digests and the publish all read only that snapshot, so an
edit to the originals cannot change what is embedded.

Stdlib only. No GitHub API, no auth, no devices, no broad cleanup.
"""

import argparse
import hashlib
import json
import os
import re
import subprocess
import sys
import tarfile
import tempfile
import uuid
import zipfile
from pathlib import Path

_HERE = Path(__file__).resolve().parent
if str(_HERE) not in sys.path:
    sys.path.insert(0, str(_HERE))

import fetch_build_inputs  # noqa: E402
import package_edl  # noqa: E402


SCHEMA_VERSION = 1

_PROPERTY_NAMES = (
    "BundledPlatformToolsArchive",
    "BundledEdlArchive",
    "BundledEdlG5Archive",
    "BundledEdlTb710fuArchive",
    "BundledQualcommDriverArchive",
    "BundledAndroidDriverArchive",
    "BundledEdlRawPacker",
    "BundledEdlRawUnpacker",
    "BundledAvbSigningKey",
    "BundledGblPrcEfi",
    "BundledGblRowEfi",
)

# Optional paired inputs: embedded only when supplied, and required for a
# single-file publish.  They are built by the system repository's
# build_files/build-offline-updater-bundle.py from a full ARM64 image root and
# copied into the build environment as ordinary local files; this build never
# downloads or regenerates them.
_OFFLINE_PROPERTY_NAMES = (
    "BundledOfflineBootstrapScript",
    "BundledOfflineUpdaterBundle",
)

# Value property (not a path) taken from the validated bundle manifest.  The
# installer embeds it as assembly metadata and requires exactly this version in
# the install receipt and in the post-install probe.
_OFFLINE_VERSION_PROPERTY = "BundledOfflineUpdaterVersion"

_OFFLINE_ASSET_SOURCE = "system build_files/build-offline-updater-bundle.py"

# The bundle contract is explicit.  A bundle manifest names the capabilities
# the runtime it carries provides; the installer refuses any bundle that does
# not declare the one it requires, so an older bundle that merely unpacks is
# not accepted and cannot fail later during a resumed upgrade.  The manifest
# also pins the entry script it installs to /usr/libexec/armada, and the
# archive member named by that entry must be present exactly once, regular,
# executable and matching the declared size and SHA-256.
_OFFLINE_MANIFEST_NAME = "manifest.json"
_OFFLINE_BUNDLE_NAME = "armada-offline-updater"
_OFFLINE_ENTRY = "usr/libexec/armada/armada-offline-update"
_OFFLINE_REQUIRED_CAPABILITY = "preflight-session-v1"
# The managed SSH session refuses an upload larger than 256 MiB
# (OfflineUpdateSshManagedSession.MaxUploadBytes).  The builder must never
# accept a bundle the transport can never deliver, so the build-side limit
# matches that transport instead of being larger.
_OFFLINE_MAX_BUNDLE_BYTES = 256 * 1024 * 1024
_OFFLINE_MAX_MEMBER_BYTES = 128 * 1024 * 1024
_OFFLINE_MAX_MANIFEST_BYTES = 4 * 1024 * 1024
_OFFLINE_MAX_MEMBERS = 4096
_OFFLINE_MAX_CAPABILITIES = 16
_OFFLINE_MAX_CAPABILITY_LENGTH = 64
_OFFLINE_CAPABILITY = re.compile(r"[a-z0-9]+(?:-[a-z0-9]+)*\Z", re.ASCII)
_OFFLINE_VERSION = re.compile(r"\d+(?:\.\d+)*\Z", re.ASCII)
_OFFLINE_MODE = re.compile(r"[0-7]{3,4}\Z", re.ASCII)

# Streaming chunk for snapshotting build inputs: bounded memory regardless of
# input size, and the only way a file is ever read on the way into a run.
_COPY_CHUNK = 1024 * 1024

_TOOL_NAMES = ("client", "packer", "unpacker")

_EXCLUDE_SOURCE_DIRS = {".git", "bin", "obj", "__pycache__"}


# ---------------------------------------------------------------------------
# helpers
# ---------------------------------------------------------------------------


def _log(phase, msg):
    print(f"[{phase}] {msg}", flush=True)


def _hash_file(path):
    h = hashlib.sha256()
    n = 0
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(1024 * 1024), b""):
            h.update(chunk)
            n += len(chunk)
    return n, h.hexdigest()


def _atomic_write_bytes(target, data):
    target = Path(target)
    target.parent.mkdir(parents=True, exist_ok=True)
    fd, tmp = tempfile.mkstemp(prefix=".tmp-", dir=str(target.parent))
    try:
        with os.fdopen(fd, "wb") as fh:
            fh.write(data)
            fh.flush()
            os.fsync(fh.fileno())
        os.chmod(tmp, 0o644)
        os.replace(tmp, target)
    except BaseException:
        try:
            os.unlink(tmp)
        except OSError:
            pass
        raise


def _atomic_write_text(target, text):
    _atomic_write_bytes(target, text.encode("utf-8"))


def _atomic_write_json(target, obj):
    _atomic_write_text(target, json.dumps(obj, indent=2, sort_keys=True) + "\n")


def _xml_escape(text):
    s = str(text)
    s = s.replace("&", "&amp;")
    s = s.replace("<", "&lt;")
    s = s.replace(">", "&gt;")
    s = s.replace('"', "&quot;")
    s = s.replace("'", "&apos;")
    return s


def _validate_vendor_files(repo_root, manifest):
    """Validate every vendor file (exact bytes + SHA) BEFORE any download or
    compilation.  Also ensure the relative path stays inside the repo."""
    repo_root = Path(repo_root).resolve()
    entries = manifest.get("vendorFiles")
    if not isinstance(entries, list) or not entries:
        raise SystemExit("manifest error: vendorFiles is empty or missing")
    results = {}
    for entry in entries:
        rel = entry.get("path")
        if not isinstance(rel, str) or not rel:
            raise SystemExit("manifest error: vendorFiles entry missing path")
        # Containment check: no traversal, no absolute.
        if os.path.isabs(rel):
            raise SystemExit(f"vendor error: absolute path in vendorFiles: {rel!r}")
        parts = Path(rel).parts
        if any(p == ".." for p in parts):
            raise SystemExit(f"vendor error: path traversal in vendorFiles: {rel!r}")
        resolved = (repo_root / rel).resolve()
        try:
            resolved.relative_to(repo_root)
        except ValueError:
            raise SystemExit(f"vendor error: path escapes repo: {rel!r}")
        if not resolved.is_file():
            raise SystemExit(f"vendor error: missing required file: {rel} ({resolved})")
        want_bytes = entry.get("bytes")
        want_sha = entry.get("sha256")
        if not isinstance(want_bytes, int) or want_bytes < 0:
            raise SystemExit(f"manifest error: bad bytes for {rel!r}")
        if not isinstance(want_sha, str) or len(want_sha) != 64:
            raise SystemExit(f"manifest error: bad sha256 for {rel!r}")
        got_bytes, got_sha = _hash_file(resolved)
        if got_bytes != want_bytes:
            raise SystemExit(
                f"vendor mismatch: {rel} size {got_bytes} != expected {want_bytes}"
            )
        if got_sha.lower() != want_sha.lower():
            raise SystemExit(
                f"vendor mismatch: {rel} sha256 {got_sha} != expected {want_sha}"
            )
        results[rel] = resolved
    return results


def _offline_capability_message():
    return (
        f"bundle does not declare the required capability "
        f"{_OFFLINE_REQUIRED_CAPABILITY!r}; rebuild it with system "
        f"build_files/build-offline-updater-bundle.py from a runtime that "
        f"supports a preflight sessionId"
    )


def _offline_manifest_document(bundle):
    """Read the one manifest.json from a bundle tar.gz without extracting it.

    Only the manifest member is read into memory, and every bound is enforced
    before its bytes are consumed.  Nothing is written or executed.
    """
    prefix = f"offline bundle error: {bundle}: "
    try:
        size = Path(bundle).stat().st_size
    except OSError as error:
        raise SystemExit(prefix + f"cannot read bundle: {error}")
    if not 0 < size <= _OFFLINE_MAX_BUNDLE_BYTES:
        raise SystemExit(prefix + "bundle size is out of bounds")
    raw = None
    count = 0
    try:
        with tarfile.open(str(bundle), mode="r:gz") as archive:
            for member in archive:
                count += 1
                if count > _OFFLINE_MAX_MEMBERS:
                    raise SystemExit(prefix + "bundle has too many members")
                if member.size > _OFFLINE_MAX_MEMBER_BYTES:
                    raise SystemExit(prefix + "bundle member size is out of bounds")
                if member.name != _OFFLINE_MANIFEST_NAME:
                    continue
                if raw is not None:
                    raise SystemExit(
                        prefix + f"bundle has more than one {_OFFLINE_MANIFEST_NAME}"
                    )
                if not member.isreg():
                    raise SystemExit(
                        prefix + f"{_OFFLINE_MANIFEST_NAME} is not a regular file"
                    )
                if not 0 < member.size <= _OFFLINE_MAX_MANIFEST_BYTES:
                    raise SystemExit(
                        prefix + f"{_OFFLINE_MANIFEST_NAME} size is out of bounds"
                    )
                raw = archive.extractfile(member).read(_OFFLINE_MAX_MANIFEST_BYTES + 1)
                if len(raw) > _OFFLINE_MAX_MANIFEST_BYTES:
                    raise SystemExit(
                        prefix + f"{_OFFLINE_MANIFEST_NAME} exceeds its size limit"
                    )
    except (OSError, tarfile.TarError) as error:
        raise SystemExit(prefix + f"bundle is not a readable tar.gz: {error}")
    if raw is None:
        raise SystemExit(prefix + f"bundle has no {_OFFLINE_MANIFEST_NAME}")
    try:
        return json.loads(raw.decode("utf-8"))
    except (UnicodeDecodeError, ValueError) as error:
        raise SystemExit(prefix + f"{_OFFLINE_MANIFEST_NAME} is not valid JSON: {error}")


def _validate_offline_manifest(bundle, document):
    """Validate bundle identity, version, entry declaration and the required
    capability, and return the declared record for the entry member.

    Every ``files`` record is checked for shape here so the archive pass that
    follows only has to compare bytes against a well-formed declaration.
    """
    prefix = f"offline bundle error: {bundle}: "
    if not isinstance(document, dict):
        raise SystemExit(prefix + "manifest must be a JSON object")
    if document.get("schemaVersion") != 1:
        raise SystemExit(prefix + "manifest schemaVersion must be 1")
    if document.get("bundle") != _OFFLINE_BUNDLE_NAME:
        raise SystemExit(prefix + f"manifest bundle must be {_OFFLINE_BUNDLE_NAME!r}")
    version = document.get("version")
    if not isinstance(version, str) or not _OFFLINE_VERSION.fullmatch(version):
        raise SystemExit(prefix + "manifest version is not a version string")
    if document.get("entry") != _OFFLINE_ENTRY:
        raise SystemExit(prefix + f"manifest entry must be {_OFFLINE_ENTRY!r}")
    files = document.get("files")
    if not isinstance(files, list) or not files:
        raise SystemExit(prefix + "manifest files must be a non-empty list")
    if len(files) > _OFFLINE_MAX_MEMBERS:
        raise SystemExit(prefix + "manifest declares too many files")
    declared = {}
    for item in files:
        if not isinstance(item, dict):
            raise SystemExit(prefix + "manifest files entries must be JSON objects")
        rel = item.get("path")
        if not isinstance(rel, str) or not rel or rel.startswith("/"):
            raise SystemExit(prefix + "manifest files entry path is not relative")
        parts = rel.split("/")
        if any(part in ("", ".", "..") for part in parts):
            raise SystemExit(
                prefix + f"manifest files entry path is not normalized: {rel!r}"
            )
        if rel in declared:
            raise SystemExit(prefix + f"manifest declares {rel!r} more than once")
        want_bytes = item.get("bytes")
        if (
            not isinstance(want_bytes, int)
            or isinstance(want_bytes, bool)
            or not 0 <= want_bytes <= _OFFLINE_MAX_MEMBER_BYTES
        ):
            raise SystemExit(prefix + f"manifest files entry size is invalid for {rel!r}")
        want_sha = item.get("sha256")
        if not isinstance(want_sha, str) or len(want_sha) != 64:
            raise SystemExit(prefix + f"manifest files entry sha256 is invalid for {rel!r}")
        try:
            int(want_sha, 16)
        except ValueError:
            raise SystemExit(prefix + f"manifest files entry sha256 is invalid for {rel!r}")
        mode = item.get("mode")
        if not isinstance(mode, str) or not _OFFLINE_MODE.fullmatch(mode):
            raise SystemExit(prefix + f"manifest files entry mode is invalid for {rel!r}")
        declared[rel] = item
    entry_spec = declared.get(_OFFLINE_ENTRY)
    if entry_spec is None:
        raise SystemExit(
            prefix + f"manifest files must declare the entry {_OFFLINE_ENTRY!r}"
        )
    if not (int(entry_spec["mode"], 8) & 0o111):
        raise SystemExit(prefix + f"manifest entry mode for {_OFFLINE_ENTRY} is not executable")
    capabilities = document.get("capabilities")
    if not isinstance(capabilities, list):
        raise SystemExit(prefix + _offline_capability_message())
    if len(capabilities) > _OFFLINE_MAX_CAPABILITIES:
        raise SystemExit(prefix + "manifest declares too many capabilities")
    seen = set()
    for capability in capabilities:
        if (
            not isinstance(capability, str)
            or not capability
            or len(capability) > _OFFLINE_MAX_CAPABILITY_LENGTH
            or not _OFFLINE_CAPABILITY.fullmatch(capability)
        ):
            raise SystemExit(prefix + "manifest capabilities are malformed")
        if capability in seen:
            raise SystemExit(prefix + "manifest capabilities must be unique")
        seen.add(capability)
    if _OFFLINE_REQUIRED_CAPABILITY not in seen:
        raise SystemExit(prefix + _offline_capability_message())
    return entry_spec


def _offline_entry_member(bundle, spec):
    """Check the declared entry member in the bundle archive.

    The archive is streamed again - no extraction, nothing written - and only
    the bytes of the member named by the manifest entry are hashed, bounded by
    ``_OFFLINE_MAX_MEMBER_BYTES``.  The member must appear exactly once, must be
    a regular file (not a symlink, hard link or directory), must carry an
    executable mode, and must match the declared size and SHA-256.
    """
    prefix = f"offline bundle error: {bundle}: "
    entry = _OFFLINE_ENTRY
    want_bytes = spec["bytes"]
    want_sha = spec["sha256"].lower()
    count = 0
    got_bytes = 0
    got_sha = None
    try:
        with tarfile.open(str(bundle), mode="r:gz") as archive:
            for member in archive:
                if member.name != entry:
                    continue
                count += 1
                if count > 1:
                    raise SystemExit(prefix + f"bundle has more than one {entry}")
                if not member.isreg():
                    raise SystemExit(prefix + f"{entry} is not a regular file")
                if not (member.mode & 0o111):
                    raise SystemExit(prefix + f"{entry} is not executable")
                if member.size != want_bytes:
                    raise SystemExit(
                        prefix + f"{entry} size {member.size} != expected {want_bytes}"
                    )
                digest = hashlib.sha256()
                stream = archive.extractfile(member)
                while True:
                    chunk = stream.read(1024 * 1024)
                    if not chunk:
                        break
                    got_bytes += len(chunk)
                    if got_bytes > _OFFLINE_MAX_MEMBER_BYTES:
                        raise SystemExit(prefix + f"{entry} exceeds its size limit")
                    digest.update(chunk)
                got_sha = digest.hexdigest()
    except (OSError, tarfile.TarError) as error:
        raise SystemExit(prefix + f"bundle is not a readable tar.gz: {error}")
    if not count:
        raise SystemExit(prefix + f"bundle has no {entry}")
    if got_sha != want_sha:
        raise SystemExit(prefix + f"{entry} sha256 {got_sha} != expected {want_sha}")


def _stage_offline_asset(prop, source, dest_dir, limit=None):
    """Snapshot one offline input into the run directory with bounded streaming.

    The source is read one chunk at a time, never more than ``limit`` bytes in
    total, and the exact bytes written are hashed as they pass through, so the
    returned digest describes the snapshot rather than a later re-read of the
    original.  A source whose size or modification time changes while it is
    being copied is rejected instead of being captured half-way.
    """
    source = Path(source)
    prefix = f"offline assets error: {prop}: "
    try:
        before = os.stat(str(source))
    except OSError as error:
        raise SystemExit(prefix + f"cannot read: {error}")
    if limit is not None and not 0 < before.st_size <= limit:
        raise SystemExit(
            f"offline bundle error: {source}: bundle size is out of bounds"
        )
    dest_dir = Path(dest_dir)
    # Fixed destination name per property: independent of the source basename,
    # so two inputs can never collide and no caller-supplied name is reused.
    dest = dest_dir / prop
    try:
        reader = open(str(source), "rb")
    except OSError as error:
        raise SystemExit(prefix + f"cannot read: {error}")
    fd, tmp = tempfile.mkstemp(prefix=".tmp-", dir=str(dest_dir))
    digest = hashlib.sha256()
    total = 0
    try:
        with reader, os.fdopen(fd, "wb") as writer:
            while True:
                chunk = reader.read(_COPY_CHUNK)
                if not chunk:
                    break
                total += len(chunk)
                if limit is not None and total > limit:
                    raise SystemExit(
                        f"offline bundle error: {source}: bundle size is out of bounds"
                    )
                digest.update(chunk)
                writer.write(chunk)
            writer.flush()
            os.fsync(writer.fileno())
        try:
            after = os.stat(str(source))
        except OSError as error:
            raise SystemExit(prefix + f"cannot re-read after staging: {error}")
        if (
            total != before.st_size
            or after.st_size != before.st_size
            or after.st_mtime_ns != before.st_mtime_ns
        ):
            raise SystemExit(prefix + "input changed while it was being staged")
        os.chmod(tmp, 0o644)
        os.replace(tmp, dest)
    except BaseException:
        try:
            os.unlink(tmp)
        except OSError:
            pass
        raise
    return {
        "path": str(dest.resolve()),
        "bytes": total,
        "sha256": digest.hexdigest(),
    }


def _validate_offline_inputs(script, bundle, work_dir):
    """Stage then validate the optional paired offline-bootstrap inputs.

    Both paths must be supplied together, and each must be an existing,
    readable regular file.  The pair is copied into a fresh, isolated run
    directory with bounded streaming, and only those snapshots are read from
    then on: the manifest, the entry bytes, the props, the record digests and
    the publish all refer to the snapshot, never to the mutable originals.

    The bundle's manifest is read from the staged snapshot - bounded, without
    extraction or execution - and must declare the capability this installer
    requires, after which the staged entry script member is streamed and
    checked against the manifest's size and SHA-256.  This runs before the
    expensive build phases so a typo or a stale bundle never costs a full
    tool compilation.

    Returns ``(staged, version, run_dir)``: ``staged`` maps each props name to
    ``{path, bytes, sha256}`` for the snapshot, ``version`` is the bundle
    manifest version the installer requires in the install receipt, and
    ``run_dir`` is the isolated run directory that holds the snapshots.  All
    three are ``({}, None, None)`` when both inputs are omitted.
    """
    if script is None and bundle is None:
        return {}, None, None
    if script is None or bundle is None:
        missing = (
            "--offline-bootstrap-script" if script is None
            else "--offline-updater-bundle"
        )
        raise SystemExit(
            f"offline assets error: {missing} is missing; the bootstrap script "
            "and updater bundle must be supplied together"
        )
    sources = {}
    for prop, raw in (
        ("BundledOfflineBootstrapScript", script),
        ("BundledOfflineUpdaterBundle", bundle),
    ):
        path = Path(raw).expanduser()
        if not path.exists():
            raise SystemExit(f"offline assets error: {prop} not found: {path}")
        if not path.is_file():
            raise SystemExit(
                f"offline assets error: {prop} is not a regular file: {path}"
            )
        if not os.access(str(path), os.R_OK):
            raise SystemExit(f"offline assets error: {prop} is not readable: {path}")
        sources[prop] = path.resolve()

    run_dir = _new_run_dir(work_dir)
    staged_dir = run_dir / "offline"
    staged_dir.mkdir()
    staged = {
        "BundledOfflineBootstrapScript": _stage_offline_asset(
            "BundledOfflineBootstrapScript",
            sources["BundledOfflineBootstrapScript"],
            staged_dir,
        ),
        "BundledOfflineUpdaterBundle": _stage_offline_asset(
            "BundledOfflineUpdaterBundle",
            sources["BundledOfflineUpdaterBundle"],
            staged_dir,
            limit=_OFFLINE_MAX_BUNDLE_BYTES,
        ),
    }
    bundle_path = staged["BundledOfflineUpdaterBundle"]["path"]
    document = _offline_manifest_document(bundle_path)
    _offline_entry_member(
        bundle_path, _validate_offline_manifest(bundle_path, document)
    )
    return staged, document["version"], run_dir


def _make_controlled_env(work_dir):
    env = dict(os.environ)
    work_dir = Path(work_dir)
    tmp = work_dir / "tmp"
    dotnet_home = work_dir / "dotnet"
    nuget = work_dir / "nuget"
    tmp.mkdir(parents=True, exist_ok=True)
    dotnet_home.mkdir(parents=True, exist_ok=True)
    nuget.mkdir(parents=True, exist_ok=True)
    env["TMPDIR"] = str(tmp)
    env["TEMP"] = str(tmp)
    env["TMP"] = str(tmp)
    env.setdefault("DOTNET_CLI_HOME", str(dotnet_home))
    env.setdefault("NUGET_PACKAGES", str(nuget))
    env["DOTNET_NOLOGO"] = "1"
    env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
    return env


def _run(argv, cwd, env, phase):
    _log(phase, "run: " + " ".join(str(a) for a in argv))
    try:
        subprocess.run([str(a) for a in argv], cwd=str(cwd), env=env, check=True)
    except FileNotFoundError as exc:
        raise SystemExit(f"[{phase}] executable not found: {exc}") from exc
    except subprocess.CalledProcessError as exc:
        raise SystemExit(
            f"[{phase}] command failed with exit code {exc.returncode}: "
            + " ".join(str(a) for a in argv)
        ) from exc


def _new_run_dir(work_dir):
    """Create a fresh, uniquely named isolated run directory under ``runs``."""
    runs_root = Path(work_dir) / "runs"
    runs_root.mkdir(parents=True, exist_ok=True)
    run_dir = runs_root / uuid.uuid4().hex
    run_dir.mkdir(parents=False, exist_ok=False)
    return run_dir


def _extract_libusb(nupkg_path, extract_spec):
    """Read ONLY the one declared native DLL out of the checked .nupkg."""
    inner = extract_spec.get("path")
    want_bytes = extract_spec.get("bytes")
    want_sha = extract_spec.get("sha256")
    if not inner or not isinstance(want_bytes, int) or not isinstance(want_sha, str):
        raise SystemExit("manifest error: libusbPackage.extract incomplete")
    with zipfile.ZipFile(nupkg_path, "r") as zf:
        try:
            data = zf.read(inner)
        except KeyError:
            raise SystemExit(
                f"libusb package missing declared member {inner!r}"
            )
    if len(data) != want_bytes:
        raise SystemExit(
            f"libusb DLL size {len(data)} != expected {want_bytes}"
        )
    got_sha = hashlib.sha256(data).hexdigest()
    if got_sha.lower() != want_sha.lower():
        raise SystemExit(
            f"libusb DLL sha256 {got_sha} != expected {want_sha}"
        )
    return data


def _combine_license(repo_root, libusb_license_path):
    edl_license = Path(repo_root) / "tools" / "edl-ng" / "LICENSE"
    if not edl_license.is_file():
        raise SystemExit(f"missing required license file: {edl_license}")
    parts = []
    parts.append("==== LICENSE (tools/edl-ng) ====\n")
    parts.append(edl_license.read_text(encoding="utf-8", errors="replace"))
    parts.append("\n\n==== LICENSE (libusb) ====\n")
    parts.append(Path(libusb_license_path).read_text(encoding="utf-8", errors="replace"))
    return "".join(parts)


def _generate_tool_pins_cs(tool_pins):
    tools = tool_pins["tools"]
    lines = [
        "// <auto-generated> by maintenance/build_installer.py — do not edit.",
        "namespace FreedomInstaller.Core",
        "{",
        "    internal static class CompiledToolPins",
        "    {",
    ]
    for name, label in (
        ("client", "Client"),
        ("packer", "Packer"),
        ("unpacker", "Unpacker"),
    ):
        info = tools[name]
        lines.append(f"        internal const long {label}Bytes = {int(info['bytes'])};")
    for name, label in (
        ("client", "Client"),
        ("packer", "Packer"),
        ("unpacker", "Unpacker"),
    ):
        info = tools[name]
        lines.append(
            f'        internal const string {label}Sha256 = "{info["sha256"]}";'
        )
    lines.append("    }")
    lines.append("}")
    lines.append("")
    return "\n".join(lines)


def _build_driver_zip(repo_root, out_path):
    driver_root = Path(repo_root) / "vendor" / "qualcomm" / "driver"
    members = [
        (driver_root / "qcser.inf", "qcser.inf"),
        (driver_root / "qcser.cat", "qcser.cat"),
        (driver_root / "serial" / "amd64" / "qcusbser.sys", "serial/amd64/qcusbser.sys"),
    ]
    out_path = Path(out_path)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(out_path, "w", zipfile.ZIP_DEFLATED) as zf:
        for src, arc in members:
            if not src.is_file():
                raise SystemExit(f"driver zip: missing source file {src}")
            zf.write(src, arc)
    return out_path


def _build_android_driver_zip(repo_root, out_path):
    driver_root = Path(repo_root) / "vendor" / "android"
    members = []
    for package in ("google", "oplus"):
        for name in ("android_winusb.inf", "androidwinusba64.cat",
                     "amd64/WdfCoInstaller01009.dll",
                     "amd64/winusbcoinstaller2.dll" if package == "google" else "amd64/WinUSBCoInstaller2.dll"):
            rel = f"{package}/{name}"
            source = driver_root / rel
            if not source.is_file():
                raise SystemExit(f"Android driver zip: missing source file {source}")
            members.append((source, rel))
    out_path = Path(out_path)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(out_path, "w", zipfile.ZIP_DEFLATED) as archive:
        for source, rel in members:
            archive.write(source, rel)
    return out_path


def _source_digest(repo_root, manifest):
    """Sorted list of {path,bytes,sha256} for every checked-in source path and
    build script declared in the manifest."""
    repo_root = Path(repo_root).resolve()
    seen = set()
    entries = []

    def _add_hash(rel):
        if rel in seen:
            return
        seen.add(rel)
        p = repo_root / rel
        n, sha = _hash_file(p)
        entries.append({"path": rel, "bytes": n, "sha256": sha})

    def _walk(rel):
        p = repo_root / rel
        if p.is_file():
            _add_hash(rel)
        elif p.is_dir():
            for root, dirs, files in os.walk(p):
                dirs[:] = sorted(d for d in dirs if d not in _EXCLUDE_SOURCE_DIRS)
                for fn in sorted(files):
                    full = Path(root) / fn
                    relpath = full.relative_to(repo_root).as_posix()
                    _add_hash(relpath)
        else:
            raise SystemExit(f"source error: missing manifest source {rel!r}")

    for src in manifest.get("sources", []):
        if "path" in src:
            _walk(src["path"])
        if "paths" in src:
            for rel in src["paths"]:
                _walk(rel)
        build_script = src.get("buildScript")
        if isinstance(build_script, str) and build_script:
            _walk(build_script)
    entries.sort(key=lambda e: e["path"])
    return entries


def _write_props(path, inputs, offline_updater_version=None):
    lines = ["<Project>", "  <PropertyGroup>"]
    for prop in _PROPERTY_NAMES:
        if prop not in inputs:
            raise SystemExit(f"props error: missing input property {prop}")
        value = str(Path(inputs[prop]).resolve())
        lines.append(f"    <{prop}>{_xml_escape(value)}</{prop}>")
    # The offline bootstrap pair is optional: a prepare-only run may omit it,
    # while a single-file publish requires both.  Its version is a value, not
    # a path, and comes from the already validated manifest.
    for prop in _OFFLINE_PROPERTY_NAMES:
        if prop not in inputs:
            continue
        value = str(Path(inputs[prop]).resolve())
        lines.append(f"    <{prop}>{_xml_escape(value)}</{prop}>")
    if offline_updater_version is not None:
        lines.append(
            f"    <{_OFFLINE_VERSION_PROPERTY}>"
            f"{_xml_escape(offline_updater_version)}</{_OFFLINE_VERSION_PROPERTY}>"
        )
    lines.append("  </PropertyGroup>")
    lines.append("</Project>")
    lines.append("")
    return "\n".join(lines)


# ---------------------------------------------------------------------------
# public: prepare
# ---------------------------------------------------------------------------


def prepare(repo_root, work_dir, manifest_path=None,
            offline_bootstrap_script=None, offline_updater_bundle=None):
    """Run the source build input preparation.

    ``offline_bootstrap_script`` and ``offline_updater_bundle`` are optional
    but paired: when supplied they are copied into the per-run directory with
    bounded streaming, validated there as regular files before any expensive
    phase, and recorded with the size and SHA-256 of that snapshot.  The props
    point at the snapshot, so the single-file publish embeds exactly the bytes
    that were validated together with the bundle manifest version the
    installer requires in the receipt.

    Returns a record dict describing every generated artefact.
    """
    repo_root = Path(repo_root).resolve()
    work_dir = Path(work_dir).resolve()
    if manifest_path is None:
        manifest_path = repo_root / "maintenance" / "build-inputs.json"
    manifest_path = Path(manifest_path)
    work_dir.mkdir(parents=True, exist_ok=True)

    # --- Phase 0: snapshot + validate the offline bootstrap pair ----------
    # The pair is copied into the isolated run directory before anything is
    # validated or downloaded.  Everything downstream - manifest, digests,
    # props, publication - reads that snapshot, not the mutable originals.
    offline_staged, offline_updater_version, run_dir = _validate_offline_inputs(
        offline_bootstrap_script, offline_updater_bundle, work_dir
    )
    offline_inputs = {
        prop: item["path"] for prop, item in offline_staged.items()
    }
    if offline_inputs:
        _log(
            "offline",
            f"paired bootstrap script and updater bundle staged and OK "
            f"(version {offline_updater_version})",
        )

    # --- Phase 1a: manifest load -----------------------------------------
    _log("manifest", f"loading {manifest_path}")
    manifest = fetch_build_inputs.load_manifest(str(manifest_path))

    # --- Phase 1b: strict vendor validation BEFORE any download/compile ---
    _log("vendor", "validating checked-in vendor files")
    vendor_map = _validate_vendor_files(repo_root, manifest)
    _log("vendor", f"{len(vendor_map)} vendor files OK")

    # --- Phase 1c: work directories, per-run isolation --------------------
    downloads_root = work_dir / "downloads"
    downloads_root.mkdir(parents=True, exist_ok=True)
    if run_dir is None:
        run_dir = _new_run_dir(work_dir)
    materials_dir = run_dir / "materials"
    materials_dir.mkdir()
    run_build_dir = run_dir / "build"
    run_build_dir.mkdir()

    env = _make_controlled_env(work_dir)

    # --- Phase 2: fetch official inputs -----------------------------------
    _log("fetch", f"fetching official inputs -> {downloads_root}")
    fetched = fetch_build_inputs.fetch_all(
        manifest, str(downloads_root), ids=fetch_build_inputs.BUILDER_IDS["windows"])
    for dl_id in fetch_build_inputs.BUILDER_IDS["windows"]:
        if dl_id not in fetched:
            raise SystemExit(f"fetch error: missing download id {dl_id!r}")
    _log("fetch", "official inputs ready")

    # --- Phase 3: compile the three tools --------------------------------
    _log("compile", "building edl-ng")
    edl_out = run_build_dir / "edl-ng"
    edl_out.mkdir()
    _run(
        ["sh", str(repo_root / "tools" / "build-edl-ng.sh"), str(edl_out)],
        cwd=repo_root,
        env=env,
        phase="compile",
    )

    _log("compile", "building boot packers")
    packers_build = run_build_dir / "boot-packers"
    packers_build.mkdir()
    packers_out = run_build_dir / "boot-packers-out"
    packers_out.mkdir()
    _run(
        [
            "sh",
            str(repo_root / "tools" / "build-boot-packers.sh"),
            str(packers_build),
            str(packers_out),
        ],
        cwd=repo_root,
        env=env,
        phase="compile",
    )

    client_exe = edl_out / "edl-ng.exe"
    packer_exe = packers_out / "MultiBootKernelPatcher.exe"
    unpacker_exe = packers_out / "UnpackTool.exe"
    for label, p in (
        ("client", client_exe),
        ("packer", packer_exe),
        ("unpacker", unpacker_exe),
    ):
        if not p.is_file():
            raise SystemExit(f"compile error: {label} executable missing at {p}")
    _log("compile", "three tools built")

    # --- Phase 4: libusb + license + pins --------------------------------
    _log("libusb", "extracting declared native DLL from .nupkg")
    libusb_entry = None
    for dl in manifest["downloads"]:
        if dl.get("id") == "libusbPackage":
            libusb_entry = dl
            break
    if libusb_entry is None:
        raise SystemExit("manifest error: libusbPackage download missing")
    extract_spec = libusb_entry.get("extract") or {}
    libusb_data = _extract_libusb(fetched["libusbPackage"], extract_spec)
    libusb_dll_out = materials_dir / "libusb-1.0.dll"
    _atomic_write_bytes(libusb_dll_out, libusb_data)

    _log("license", "combining edl-ng + libusb licenses")
    license_text = _combine_license(repo_root, fetched["libusbLicense"])
    license_out = materials_dir / "LICENSE.txt"
    _atomic_write_text(license_out, license_text)

    _log("pins", "hashing freshly built tools")
    tools = {}
    tool_paths = {}
    for name, path in (
        ("client", client_exe),
        ("packer", packer_exe),
        ("unpacker", unpacker_exe),
    ):
        n, sha = _hash_file(path)
        tools[name] = {"bytes": n, "sha256": sha}
        tool_paths[name] = str(path.resolve())
    tool_pins = {"schemaVersion": SCHEMA_VERSION, "tools": tools}
    tool_pins_json_path = run_dir / "tool-pins.json"
    _atomic_write_json(tool_pins_json_path, tool_pins)

    pins_cs = _generate_tool_pins_cs(tool_pins)
    pins_cs_path = run_dir / "CompiledToolPins.g.cs"
    _atomic_write_text(pins_cs_path, pins_cs)

    # --- Phase 5: driver ZIP ---------------------------------------------
    _log("driver", "building Qualcomm driver ZIP")
    driver_zip = run_dir / "qualcomm-driver.zip"
    _build_driver_zip(repo_root, driver_zip)
    android_driver_zip = run_dir / "android-driver.zip"
    _build_android_driver_zip(repo_root, android_driver_zip)

    # --- Phase 6: package G4 / G5 / TB710FU -------------------------------
    python_zip = fetched["python"]
    avb = repo_root / "maintenance" / "avb_stock.py"
    avb_cli = repo_root / "maintenance" / "avb_stock_cli.py"
    gpt = repo_root / "maintenance" / "portable_gpt.py"
    gpt_cli = repo_root / "maintenance" / "edl_gpt_cli.py"
    for label, p in (
        ("avb_stock.py", avb),
        ("avb_stock_cli.py", avb_cli),
        ("portable_gpt.py", gpt),
        ("edl_gpt_cli.py", gpt_cli),
    ):
        if not p.is_file():
            raise SystemExit(f"package error: missing repository script {p}")

    firehose_melf = repo_root / "vendor" / "qualcomm" / "g4" / "firehose.melf"
    g5_root = repo_root / "vendor" / "qualcomm" / "g5"
    tb710fu_firehose = repo_root / "vendor" / "qualcomm" / "tb710fu" / "firehose.melf"
    if not firehose_melf.is_file():
        raise SystemExit(f"package error: missing G4 firehose {firehose_melf}")
    if not g5_root.is_dir():
        raise SystemExit(f"package error: missing G5 loader root {g5_root}")
    if not tb710fu_firehose.is_file():
        raise SystemExit(f"package error: missing TB710FU firehose {tb710fu_firehose}")

    packages_dir = run_dir / "packages"
    packages_dir.mkdir()

    def _package(profile, output):
        ns = {
            "profile": profile,
            "client": str(client_exe.resolve()),
            "usbLibrary": str(libusb_dll_out.resolve()),
            "license": str(license_out.resolve()),
            "python": str(Path(python_zip).resolve()),
            "avb": str(avb.resolve()),
            "avbCli": str(avb_cli.resolve()),
            "gpt": str(gpt.resolve()),
            "gptCli": str(gpt_cli.resolve()),
            "firehose": (
                str(firehose_melf.resolve()) if profile == "tb322fc"
                else str(tb710fu_firehose.resolve()) if profile == "tb710fu"
                else None
            ),
            "loader_root": str(g5_root.resolve()) if profile == "tb323fu" else None,
            "output": str(Path(output).resolve()),
            "build_pins_json": str(tool_pins_json_path.resolve()),
        }
        _log("package", f"{profile} -> {output}")
        return package_edl.package(argparse.Namespace(**ns))

    g4_out = packages_dir / "freedom-tb322fc.zip"
    g5_out = packages_dir / "freedom-tb323fu.zip"
    g4_report = _package("tb322fc", g4_out)
    g5_report = _package("tb323fu", g5_out)
    tb710fu_out = packages_dir / "freedom-tb710fu.zip"
    tb710fu_report = _package("tb710fu", tb710fu_out)

    # --- Phase 7: props + record ----------------------------------------
    inputs = {
        "BundledPlatformToolsArchive": str(Path(fetched["platformTools"]).resolve()),
        "BundledEdlArchive": str(g4_out.resolve()),
        "BundledEdlG5Archive": str(g5_out.resolve()),
        "BundledEdlTb710fuArchive": str(tb710fu_out.resolve()),
        "BundledQualcommDriverArchive": str(driver_zip.resolve()),
        "BundledAndroidDriverArchive": str(android_driver_zip.resolve()),
        "BundledEdlRawPacker": str(packer_exe.resolve()),
        "BundledEdlRawUnpacker": str(unpacker_exe.resolve()),
        "BundledAvbSigningKey": str(
            (repo_root / "vendor" / "aosp" / "testkey_rsa4096.pem").resolve()
        ),
        "BundledGblPrcEfi": str(Path(fetched["gblPrc"]).resolve()),
        "BundledGblRowEfi": str(Path(fetched["gblRow"]).resolve()),
    }
    inputs.update(offline_inputs)
    for prop, path_str in inputs.items():
        if not Path(path_str).is_file():
            raise SystemExit(f"props error: missing input for {prop}: {path_str}")

    offline_assets = {}
    for prop, item in offline_staged.items():
        # digests were computed from the bytes written into the snapshot, so
        # they cannot drift from what the props point at.
        offline_assets[prop] = {
            "path": item["path"],
            "bytes": item["bytes"],
            "sha256": item["sha256"],
            "source": _OFFLINE_ASSET_SOURCE,
        }
    if offline_assets:
        _log(
            "offline",
            "recorded snapshot digests for the paired bootstrap script and "
            "updater bundle",
        )

    props_xml = _write_props(None, inputs, offline_updater_version)
    marker = "  <PropertyGroup>\n"
    extra = f"    <BundledToolPinsSource>{_xml_escape(str(pins_cs_path.resolve()))}</BundledToolPinsSource>\n"
    if marker not in props_xml:
        raise SystemExit("props error: could not locate PropertyGroup marker")
    props_xml = props_xml.replace(marker, marker + extra, 1)

    props_path = work_dir / "build-inputs.props"
    record_path = work_dir / "build-record.json"

    # --- source digest ---------------------------------------------------
    _log("record", "computing source digest")
    source_digest = _source_digest(repo_root, manifest)
    sources_meta = []
    for src in manifest.get("sources", []):
        item = {k: v for k, v in src.items() if k not in ("path", "paths")}
        if "path" in src:
            item["path"] = src["path"]
        if "paths" in src:
            item["paths"] = list(src["paths"])
        sources_meta.append(item)

    record = {
        "schemaVersion": SCHEMA_VERSION,
        "runDirectory": str(run_dir.resolve()),
        "tools": {name: dict(tools[name]) for name in _TOOL_NAMES},
        "toolPaths": tool_paths,
        "inputs": {k: str(Path(v).resolve()) for k, v in inputs.items()},
        "offlineBootstrap": offline_assets,
        "offlineUpdaterVersion": offline_updater_version,
        "toolPinsJson": str(tool_pins_json_path.resolve()),
        "toolPinsSource": str(pins_cs_path.resolve()),
        "propsPath": str(props_path.resolve()),
        "packages": {
            "tb322fc": str(g4_out.resolve()),
            "tb323fu": str(g5_out.resolve()),
            "tb710fu": str(tb710fu_out.resolve()),
        },
        "sources": sources_meta,
        "sourceFiles": source_digest,
    }

    # Write props before record so a reader sees a record only after props exists.
    _atomic_write_text(props_path, props_xml)
    _atomic_write_json(record_path, record)
    _log("done", f"props -> {props_path}")
    _log("done", f"record -> {record_path}")
    return record


# ---------------------------------------------------------------------------
# CLI: prepare + dotnet publish
# ---------------------------------------------------------------------------


def _default_publish(record, output_dir, env):
    props = record["propsPath"]
    repo_root = Path(_HERE).resolve().parent
    output_dir = Path(output_dir).resolve()
    output_dir.mkdir(parents=True, exist_ok=True)
    argv = [
        "dotnet",
        "publish",
        str(repo_root / "src" / "FreedomInstaller.App" / "FreedomInstaller.App.csproj"),
        "-c",
        "Release",
        "-r",
        "win-x64",
        "--self-contained",
        "true",
        "-p:UseSharedCompilation=false",
        f"-p:BundledBuildInputsProps={props}",
        "-o",
        str(output_dir),
    ]
    _run(argv, cwd=repo_root, env=env, phase="publish")


def main(argv=None):
    parser = argparse.ArgumentParser(
        description="Unified source build entrypoint for the Freedom Installer."
    )
    parser.add_argument("--work-dir", required=True)
    parser.add_argument("--output-dir", default=None)
    parser.add_argument("--manifest", default=None)
    parser.add_argument("--prepare-only", action="store_true")
    parser.add_argument(
        "--offline-bootstrap-script",
        default=None,
        help=(
            "Existing armada-offline-bootstrap script built by the system "
            "build_files/build-offline-updater-bundle.py; required with "
            "--offline-updater-bundle for a single-file publish."
        ),
    )
    parser.add_argument(
        "--offline-updater-bundle",
        default=None,
        help=(
            "Existing armada-offline-updater.tar.gz bundle built by the system "
            "build_files/build-offline-updater-bundle.py; required with "
            "--offline-bootstrap-script for a single-file publish."
        ),
    )
    args = parser.parse_args(argv)

    if not args.prepare_only:
        if not args.output_dir:
            parser.error("--output-dir is required unless --prepare-only is set")
        # A single-file publish without the offline bootstrap pair would
        # silently ship an installer that cannot deploy the updater.  Fail
        # here, before prepare() starts compiling anything.
        if not args.offline_bootstrap_script or not args.offline_updater_bundle:
            parser.error(
                "single-file publish requires both --offline-bootstrap-script "
                "and --offline-updater-bundle (the paired local artefacts from "
                "the system offline updater bundle build); use --prepare-only "
                "only when you do not intend to publish the single EXE"
            )

    repo_root = Path(_HERE).resolve().parent
    work_dir = Path(args.work_dir).resolve()
    manifest_path = Path(args.manifest).resolve() if args.manifest else None

    record = prepare(
        repo_root,
        work_dir,
        manifest_path=manifest_path,
        offline_bootstrap_script=args.offline_bootstrap_script,
        offline_updater_bundle=args.offline_updater_bundle,
    )
    print(f"prepare complete: {record['propsPath']}", flush=True)

    if args.prepare_only:
        print(json.dumps({"propsPath": record["propsPath"], "record": record}, indent=2))
        return 0

    env = _make_controlled_env(work_dir)
    _default_publish(record, args.output_dir, env)
    print(f"publish complete: {Path(args.output_dir).resolve()}", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
