#!/usr/bin/env python3
"""Prepare the public inputs used by the complete Linux Core test run.

The script performs two independent steps, both inside the CI work directory:

1. ``build_installer.prepare(repo_root, work_dir)`` with its normal
   prepare-only semantics: no legacy optional offline assets and no publish.
   That downloads the pinned public archives, builds the Windows EDL client
   and raw-boot packers, packages the G4/G5/TB710FU toolchains and the two
   driver ZIPs, and emits ``build-inputs.props`` plus ``build-record.json``.
2. A native Linux build of the same ``tools/boot-packers`` sources with
   ``BUILD_SHIMS=OFF`` in its own build directory.  It deliberately does not
   set ``CMAKE_SYSTEM_NAME``, so CMake produces real ELF executables that the
   tests may run on Linux.  The pinned Windows PE files are only named in the
   environment; they are never executed on Linux.

The only output of interest is an environment file (``KEY=value`` lines) that
maps the generated artefacts to the variable names the Core tests require.
Every mapped file is required to exist; a missing input aborts the job instead
of silently skipping whole suites.

Stdlib only.  No credentials, no network access of its own, no device work.
"""

import argparse
import json
import shlex
import shutil
import subprocess
import sys
from pathlib import Path

_HERE = Path(__file__).resolve().parent
if str(_HERE) not in sys.path:
    sys.path.insert(0, str(_HERE))

import build_installer  # noqa: E402

# ``record`` names are the exact keys of build_installer's build record.
_RECORD_INPUTS = (
    ("EDL_RAW_PACKER_PATH", "BundledEdlRawPacker"),
    ("EDL_RAW_UNPACKER_PATH", "BundledEdlRawUnpacker"),
    ("EDL_TOOLCHAIN_TEST_BUNDLE_PATH", "BundledEdlArchive"),
    ("EDL_TOOLCHAIN_G5_TEST_BUNDLE_PATH", "BundledEdlG5Archive"),
    ("EDL_TOOLCHAIN_TB710FU_TEST_BUNDLE_PATH", "BundledEdlTb710fuArchive"),
    ("ANDROID_DRIVER_TEST_ARCHIVE", "BundledAndroidDriverArchive"),
    ("QUALCOMM_DRIVER_TEST_ARCHIVE", "BundledQualcommDriverArchive"),
)

_WINDOWS_PE_VARIABLES = {
    "EDL_RAW_PACKER_PATH",
    "EDL_RAW_UNPACKER_PATH",
    "EDL_CLIENT_TEST_BINARY",
}

_LINUX_ELF_VARIABLES = {
    "EDL_RAW_LINUX_PACKER_PATH",
    "EDL_RAW_LINUX_UNPACKER_PATH",
}

NATIVE_ORACLE_TARGETS = (
    ("packer", "MultiBootKernelPatcher"),
    ("unpacker", "UnpackTool"),
)


def _require_file(path, label):
    if path is None:
        raise SystemExit(f"test input error: {label} is not present in the build record")
    full = Path(path).resolve()
    if not full.is_file():
        raise SystemExit(f"test input error: {label} is not a file: {full}")
    return str(full)


def _magic(path):
    with open(path, "rb") as handle:
        return handle.read(4)


def _record_input(record, key):
    inputs = record.get("inputs")
    if not isinstance(inputs, dict):
        raise SystemExit("test input error: build record has no inputs object")
    return inputs.get(key)


def compose_environment(record, native, work_root):
    """Map the generated artefacts to the mandatory test variables.

    This is a pure mapping apart from creating the two writable roots.  Every
    value is an absolute path to a real file (or an existing directory for the
    roots), and the two binary kinds are checked so a Windows PE can never be
    handed to a Linux test as its executable oracle.  A PE only has to start
    with ``MZ`` (real Windows images carry more after the signature); an ELF
    still has to match its four-byte magic exactly.
    """
    work_root = Path(work_root).resolve()
    artifacts = work_root / "artifacts"
    edl_client_work = work_root / "edl-client"
    artifacts.mkdir(parents=True, exist_ok=True)
    edl_client_work.mkdir(parents=True, exist_ok=True)

    environment = {
        "TEST_ARTIFACT_ROOT": str(artifacts),
        "EDL_CLIENT_TEST_WORK_ROOT": str(edl_client_work),
    }

    for variable, key in _RECORD_INPUTS:
        environment[variable] = _require_file(_record_input(record, key), variable)

    # These two record fields are top-level rather than ``inputs`` entries, and
    # they gate the compiled-tool-pins test, so the names are written literally
    # instead of being derived from a shared table.
    environment["BUILD_TOOL_PINS_JSON"] = _require_file(
        record.get("toolPinsJson"), "BUILD_TOOL_PINS_JSON")
    tool_paths = record.get("toolPaths")
    if not isinstance(tool_paths, dict):
        raise SystemExit("test input error: build record has no toolPaths object")
    environment["EDL_CLIENT_TEST_BINARY"] = _require_file(
        tool_paths.get("client"), "EDL_CLIENT_TEST_BINARY")

    for variable in sorted(_WINDOWS_PE_VARIABLES):
        if not _magic(environment[variable]).startswith(b"MZ"):
            raise SystemExit(
                f"test input error: {variable} is not the pinned Windows PE: "
                f"{environment[variable]}"
            )

    for variable in sorted(_LINUX_ELF_VARIABLES):
        value = native.get(variable)
        environment[variable] = _require_file(value, variable)
        if _magic(environment[variable]) != b"\x7fELF":
            raise SystemExit(
                f"test input error: {variable} is not a native Linux ELF: "
                f"{environment[variable]}"
            )

    environment["CORE_TEST_PROPS_PATH"] = _require_file(
        record.get("propsPath"), "CORE_TEST_PROPS_PATH")
    return environment


def write_environment_file(path, environment):
    """Write one sourceable ``KEY=value`` line per variable.

    Values are shell-quoted because the file is sourced by ``set -a; . file``
    from a path that may contain spaces or shell metacharacters.  The quoting
    is the shell's own portable form, so no caller has to repair it.
    """
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    text = "".join(
        f"{name}={shlex.quote(value)}\n" for name, value in environment.items())
    path.write_text(text, encoding="utf-8")
    return path


def build_native_oracle(repo_root, work_dir):
    """Build real Linux ELF packers from the vendored boot-packers sources."""
    repo_root = Path(repo_root).resolve()
    source = repo_root / "tools" / "boot-packers"
    if not (source / "CMakeLists.txt").is_file():
        raise SystemExit(f"native oracle error: missing source tree {source}")
    if not (source / "lib" / "inih" / "ini.c").is_file():
        raise SystemExit(
            "native oracle error: tools/boot-packers/lib/inih/ini.c is missing; "
            "the vendored inih source is required (no submodule fetch is performed)")

    root = Path(work_dir).resolve() / "native-oracle"
    build = root / "build"
    output = root / "bin"
    build.mkdir(parents=True, exist_ok=True)
    output.mkdir(parents=True, exist_ok=True)

    # No CMAKE_SYSTEM_NAME and no cross compiler: this is a native Linux
    # oracle, separate from the Windows PE build produced by prepare().
    _run([
        "cmake",
        "-S", str(source),
        "-B", str(build),
        "-DBUILD_SHIMS=OFF",
        "-DCMAKE_BUILD_TYPE=Release",
    ], repo_root)
    _run(["cmake", "--build", str(build), "--parallel", "2"], repo_root)

    native = {}
    for key, executable in NATIVE_ORACLE_TARGETS:
        produced = build / executable
        if not produced.is_file():
            raise SystemExit(f"native oracle error: {produced} was not produced")
        if _magic(produced) != b"\x7fELF":
            raise SystemExit(f"native oracle error: {produced} is not a Linux ELF")
        destination = output / executable
        shutil.copy2(produced, destination)
        destination.chmod(0o755)
        native[key] = str(destination.resolve())

    native["EDL_RAW_LINUX_PACKER_PATH"] = native.pop("packer")
    native["EDL_RAW_LINUX_UNPACKER_PATH"] = native.pop("unpacker")
    return native


def _run(argv, cwd):
    try:
        subprocess.run([str(part) for part in argv], cwd=str(cwd), check=True)
    except FileNotFoundError as error:
        raise SystemExit(f"command not found: {error}") from error
    except subprocess.CalledProcessError as error:
        raise SystemExit(
            f"command failed with exit code {error.returncode}: "
            + " ".join(str(part) for part in argv)) from error


def main(argv=None):
    parser = argparse.ArgumentParser(
        description="Prepare public inputs for the complete Linux Core test run.")
    parser.add_argument("--repo-root", default=str(_HERE.parent))
    parser.add_argument("--work-dir", required=True)
    parser.add_argument("--env-file", required=True)
    parser.add_argument("--summary-json", default=None)
    args = parser.parse_args(argv)

    repo_root = Path(args.repo_root).resolve()
    work_dir = Path(args.work_dir).resolve()
    work_dir.mkdir(parents=True, exist_ok=True)

    record = build_installer.prepare(repo_root, work_dir)
    native = build_native_oracle(repo_root, work_dir)
    environment = compose_environment(record, native, work_dir)
    env_file = write_environment_file(args.env_file, environment)

    summary = {
        "propsPath": environment["CORE_TEST_PROPS_PATH"],
        "envFile": str(env_file.resolve()),
        "variables": sorted(environment),
        "nativeOracle": {
            "packer": environment["EDL_RAW_LINUX_PACKER_PATH"],
            "unpacker": environment["EDL_RAW_LINUX_UNPACKER_PATH"],
        },
    }
    if args.summary_json:
        summary_path = Path(args.summary_json)
        summary_path.parent.mkdir(parents=True, exist_ok=True)
        summary_path.write_text(
            json.dumps(summary, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(json.dumps(summary, indent=2, sort_keys=True))
    return 0


if __name__ == "__main__":
    sys.exit(main())
