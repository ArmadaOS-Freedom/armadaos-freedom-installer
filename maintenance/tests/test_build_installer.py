"""Behavioural tests for maintenance/build_installer.py.

These tests exercise real vendor validation, real hashing, real ZIP/props/pins
construction, and CLI wiring.  Compiler calls and official input fetching are
patched at phase boundaries for realistic hashes on small fake outputs.
"""

import contextlib
import hashlib
import io
import json
import os
import sys
import tarfile
import tempfile
import unittest
import zipfile
from pathlib import Path
from unittest import mock

_HERE = Path(__file__).resolve().parent
_MAINT = _HERE.parent
if str(_MAINT) not in sys.path:
    sys.path.insert(0, str(_MAINT))

import build_installer  # noqa: E402
import fetch_build_inputs  # noqa: E402
import package_edl  # noqa: E402


VENDOR_SMALL = b"vendor-payload"
AOSP_PEM = b"-----BEGIN PUBLIC KEY-----\ntest\n-----END PUBLIC KEY-----\n"
DRIVER_INF = b"[version]\n"
DRIVER_CAT = b"cat\n"
DRIVER_SYS = b"sys\n"
G4_MELF = b"g4-melf\n"
G5_MBN = b"g5-mbn\n"
TB710FU_MELF = b"tb710fu-melf\n"

# The bundle contract: the manifest entry names this archive member, which is
# a small regular executable file carried inside the tar.gz.
OFFLINE_ENTRY = "usr/libexec/armada/armada-offline-update"
OFFLINE_ENTRY_BYTES = b"#!/usr/bin/python3 -I\n# armada offline updater\n"
OFFLINE_ENTRY_MODE = 0o755


def _sha(b):
    return hashlib.sha256(b).hexdigest()


class BuildInstallerTestHelpers:
    @staticmethod
    def write_valid_zip(path, profile):
        with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as zf:
            zf.writestr("README.txt", f"fake {profile} installer\n")
            zf.writestr("payload.bin", profile.encode("ascii"))


def _write(p, data):
    p = Path(p)
    p.parent.mkdir(parents=True, exist_ok=True)
    if isinstance(data, str):
        data = data.encode("utf-8")
    p.write_bytes(data)
    return p


def _offline_manifest(**overrides):
    document = {
        "schemaVersion": 1,
        "bundle": "armada-offline-updater",
        "version": "0.8.0",
        "format": "tar.gz",
        "entry": OFFLINE_ENTRY,
        "files": [
            {
                "path": OFFLINE_ENTRY,
                "bytes": len(OFFLINE_ENTRY_BYTES),
                "sha256": _sha(OFFLINE_ENTRY_BYTES),
                "mode": "0755",
            }
        ],
    }
    document.update(overrides)
    return document


def _tar_gz_bytes(members):
    """Build a real tar.gz from (name, bytes[, mode]) tuples."""
    buffer = io.BytesIO()
    with tarfile.open(fileobj=buffer, mode="w:gz") as archive:
        for member in members:
            info = tarfile.TarInfo(member[0])
            info.size = len(member[1])
            info.mode = member[2] if len(member) > 2 else 0o644
            archive.addfile(info, io.BytesIO(member[1]))
    return buffer.getvalue()


def _offline_bundle_bytes(capabilities=("preflight-session-v1",),
                          entry_bytes=OFFLINE_ENTRY_BYTES,
                          entry_mode=OFFLINE_ENTRY_MODE,
                          **overrides):
    document = _offline_manifest(**overrides)
    if capabilities is not None:
        # Keep the caller's exact JSON type.  Coercing to list() would turn a
        # malformed string such as "preflight-session-v1" into a list of
        # characters and the negative test would stop exercising the
        # "capabilities must be a list" branch.
        document["capabilities"] = capabilities
    members = [("manifest.json", json.dumps(document).encode("utf-8"))]
    if entry_bytes is not None:
        members.append((OFFLINE_ENTRY, entry_bytes, entry_mode))
    return _tar_gz_bytes(members)


def _build_fixture_repo(root):
    """Create a minimal complete repo tree with Chinese/space/& in path."""
    root = Path(root)
    # vendor files (must match manifest hashes exactly)
    vendor = {
        "vendor/aosp/testkey_rsa4096.pem": AOSP_PEM,
        "vendor/qualcomm/driver/qcser.inf": DRIVER_INF,
        "vendor/qualcomm/driver/qcser.cat": DRIVER_CAT,
        "vendor/qualcomm/driver/serial/amd64/qcusbser.sys": DRIVER_SYS,
        "vendor/qualcomm/g4/firehose.melf": G4_MELF,
        "vendor/qualcomm/tb710fu/firehose.melf": TB710FU_MELF,
    }
    for package in ("google", "oplus"):
        for name in ("android_winusb.inf", "androidwinusba64.cat",
                     "amd64/WdfCoInstaller01009.dll",
                     "amd64/winusbcoinstaller2.dll" if package == "google" else "amd64/WinUSBCoInstaller2.dll"):
            vendor[f"vendor/android/{package}/{name}"] = DRIVER_INF
    # fabricate G5 loader files (only existence validation needed by prepare)
    for name in (
        "multi_image_qti.mbn",
        "multi_image.mbn",
        "prog_firehose_ddr.elf",
        "qsahara_device_programmer.xml",
        "sequencer_ram.elf",
        "signed_firmware_soc_view.elf",
        "tme_config.elf",
        "xbl_config_devprg.elf",
        "xbl_sc.elf",
    ):
        vendor[f"vendor/qualcomm/g5/{name}"] = G5_MBN
    for rel, data in vendor.items():
        _write(root / rel, data)

    # repository sources
    _write(root / "maintenance" / "avb_stock.py", "# avb\n")
    _write(root / "maintenance" / "avb_stock_cli.py", "# avb_cli\n")
    _write(root / "maintenance" / "portable_gpt.py", "# gpt\n")
    _write(root / "maintenance" / "edl_gpt_cli.py", "# gpt_cli\n")
    _write(root / "tools" / "edl-ng" / "LICENSE", "EDL-ng license text\n")
    _write(root / "tools" / "edl-ng" / "Program.cs", "// edl src\n")
    _write(root / "tools" / "boot-packers" / "main.cpp", "// packers src\n")
    _write(root / "tools" / "build-edl-ng.sh", "#!/bin/sh\n")
    _write(root / "tools" / "build-boot-packers.sh", "#!/bin/sh\n")
    _write(root / "src" / "FreedomInstaller.App" / "FreedomInstaller.App.csproj", "<Project/>\n")

    # manifest: keep hashes in sync with the fixture bodies.
    manifest = {
        "schemaVersion": 2,
        "target": "win-x64",
        "downloads": [
            {
                "id": "platformTools",
                "name": "platform-tools.zip",
                "url": "https://example.invalid/platform-tools.zip",
                "source": "https://developer.android.com/tools/releases/platform-tools",
                "bytes": 10,
                "sha256": "0" * 64,
                "version": "x",
            },
            {
                "id": "platformToolsMacos",
                "name": "platform-tools-macos.zip",
                "url": "https://example.invalid/platform-tools-darwin.zip",
                "source": "https://developer.android.com/tools/releases/platform-tools",
                "bytes": 11,
                "sha256": "9" * 64,
                "version": "x",
            },
            {
                "id": "python",
                "name": "python-embed.zip",
                "url": "https://example.invalid/python.zip",
                "source": "https://example.invalid/python",
                "bytes": 20,
                "sha256": "1" * 64,
                "version": "x",
            },
            {
                "id": "libusbPackage",
                "name": "libusb.nupkg",
                "url": "https://example.invalid/libusb.nupkg",
                "source": "https://example.invalid/libusb",
                "bytes": 30,
                "sha256": "2" * 64,
                "version": "x",
                "extract": {
                    "path": "runtimes/win-x64/native/libusb-1.0.dll",
                    "bytes": len(b"libusb-dll"),
                    "sha256": _sha(b"libusb-dll"),
                },
            },
            {
                "id": "libusbLicense",
                "name": "libusb-COPYING.txt",
                "url": "https://example.invalid/libusb-COPYING.txt",
                "source": "https://example.invalid/libusb",
                "bytes": 40,
                "sha256": "3" * 64,
                "version": "x",
            },
            {
                "id": "gblPrc",
                "name": "prc.efi",
                "url": "https://example.invalid/prc.efi",
                "source": "https://example.invalid/gbl",
                "bytes": 50,
                "sha256": "4" * 64,
                "version": "x",
            },
            {
                "id": "gblRow",
                "name": "row.efi",
                "url": "https://example.invalid/row.efi",
                "source": "https://example.invalid/gbl",
                "bytes": 60,
                "sha256": "5" * 64,
                "version": "x",
            },
        ],
        "vendorFiles": [],
        "sources": [
            {
                "id": "edl-ng",
                "upstream": "https://github.com/strongtz/edl-ng",
                "baseCommit": "0" * 40,
                "path": "tools/edl-ng",
                "buildScript": "tools/build-edl-ng.sh",
            },
            {
                "id": "boot-packers",
                "upstream": "https://github.com/Project-Aloha/DualBootKernelPatcher",
                "baseCommit": "1" * 40,
                "path": "tools/boot-packers",
                "buildScript": "tools/build-boot-packers.sh",
            },
            {
                "id": "avb-gpt-scripts",
                "paths": [
                    "maintenance/avb_stock.py",
                    "maintenance/avb_stock_cli.py",
                    "maintenance/portable_gpt.py",
                    "maintenance/edl_gpt_cli.py",
                ],
            },
        ],
    }
    for rel, data in vendor.items():
        manifest["vendorFiles"].append(
            {"path": rel, "bytes": len(data), "sha256": _sha(data)}
        )

    _write(root / "maintenance" / "build-inputs.json", json.dumps(manifest, indent=2))
    return root, manifest


def _fake_fetch_side_effect(manifest, output_dir, ids=None):
    """Real hashes on fake download inputs cached into output_dir."""
    out = Path(output_dir)
    out.mkdir(parents=True, exist_ok=True)
    files = {}
    for dl in manifest["downloads"]:
        rid = dl["id"]
        if ids is not None and rid not in ids:
            continue
        if rid == "libusbPackage":
            data = _make_nupkg()
        elif rid == "libusbLicense":
            data = b"libusb license text\n"
        else:
            data = f"{rid}-payload\n".encode("utf-8")
        p = out / dl["name"]
        _write(p, data)
        # Recompute size/sha in the manifest so the fake fetch is consistent.
        dl["bytes"] = len(data)
        dl["sha256"] = _sha(data)
        files[rid] = str(p.resolve())
    return files


def _make_nupkg():
    """Build a real .nupkg containing the declared native DLL."""
    import io

    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w", zipfile.ZIP_STORED) as zf:
        zf.writestr("runtimes/win-x64/native/libusb-1.0.dll", b"libusb-dll")
        zf.writestr("readme.txt", b"unused")
    return buf.getvalue()


def _fake_compile(repo_root, env):
    """Patch both build scripts to spawn tiny fake fresh outputs."""

    def edl(argv, cwd, env, phase):
        out = Path(argv[2])
        out.mkdir(parents=True, exist_ok=True)
        _write(out / "edl-ng.exe", b"fresh-client-0123456789")

    def packers(argv, cwd, env, phase):
        out = Path(argv[3])
        out.mkdir(parents=True, exist_ok=True)
        _write(out / "MultiBootKernelPatcher.exe", b"fresh-packer-abcdefghij")
        _write(out / "UnpackTool.exe", b"fresh-unpacker-ABCDEFGHIJ")

    def dispatch(argv, cwd, env, phase):
        if "build-edl-ng.sh" in " ".join(argv):
            return edl(argv, cwd, env, phase)
        if "build-boot-packers.sh" in " ".join(argv):
            return packers(argv, cwd, env, phase)
        # any other _run (e.g. dotnet publish) is a no-op for this test

    return dispatch


class PrepareTests(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory(prefix="bi \u2013 ")  # em dash to force encoding
        self.work_parent = Path(self._tmp.name) / "rep o & data"
        self.work_parent.mkdir(parents=True)
        self.repo_root, self.manifest = _build_fixture_repo(self.work_parent / "r epo & src")
        self.work_dir = self.work_parent / "work dir"

    def tearDown(self):
        self._tmp.cleanup()

    def _run_prepare(self, **prepare_kwargs):
        def fake_package(ns):
            out = Path(ns.output)
            out.parent.mkdir(parents=True, exist_ok=True)
            BuildInstallerTestHelpers.write_valid_zip(out, ns.profile)
            return {"profile": ns.profile, "output": ns.output}

        with mock.patch.object(
            fetch_build_inputs, "fetch_all", side_effect=_fake_fetch_side_effect
        ) as fetch_mock, mock.patch.object(
            build_installer, "_run", side_effect=_fake_compile(self.repo_root, None)
        ) as run_mock, mock.patch.object(
            build_installer.package_edl, "package", side_effect=fake_package
        ) as pkg_mock:
            record = build_installer.prepare(
                self.repo_root,
                self.work_dir,
                manifest_path=self.repo_root / "maintenance" / "build-inputs.json",
                **prepare_kwargs,
            )
        return record, fetch_mock, run_mock, pkg_mock

    def _offline_assets(self):
        root = self.work_parent / "offline & inputs"
        script = _write(
            root / "armada-offline-bootstrap", b"#!/bin/sh\necho bootstrap\n"
        )
        bundle = _write(
            root / "armada-offline-updater.tar.gz", _offline_bundle_bytes()
        )
        return script, bundle

    def test_full_pipeline_and_artifacts(self):
        record, fetch_mock, run_mock, pkg_mock = self._run_prepare()

        # fetch called exactly once with the work/downloads cache dir
        self.assertEqual(fetch_mock.call_count, 1)
        cache_dir = Path(fetch_mock.call_args[0][1])
        self.assertEqual(cache_dir.name, "downloads")
        self.assertEqual(cache_dir.parent, self.work_dir)
        # only the Windows inputs: an offline rebuild never needs the macOS platform-tools
        self.assertEqual(fetch_mock.call_args.kwargs["ids"], fetch_build_inputs.BUILDER_IDS["windows"])
        self.assertNotIn("platformToolsMacos", fetch_mock.call_args.kwargs["ids"])

        # two compilations (edl + boot-packers)
        self.assertGreaterEqual(run_mock.call_count, 2)

        # props exists and is now the stable pointer
        props_path = Path(record["propsPath"])
        self.assertTrue(props_path.is_file())
        if os.name == "posix":
            self.assertEqual(props_path.stat().st_mode & 0o777, 0o644)
            self.assertEqual(Path(record["toolPinsJson"]).stat().st_mode & 0o777, 0o644)
            self.assertEqual(Path(record["toolPinsSource"]).stat().st_mode & 0o777, 0o644)
            self.assertEqual((self.work_dir / "build-record.json").stat().st_mode & 0o777, 0o644)
        props_xml = props_path.read_text(encoding="utf-8")

        # all eleven properties present, absolute-injected
        for prop in build_installer._PROPERTY_NAMES:
            self.assertIn(f"<{prop}>", props_xml)
        self.assertIn("<BundledToolPinsSource>", props_xml)
        # no offline inputs supplied: the optional pair stays out of props
        for prop in build_installer._OFFLINE_PROPERTY_NAMES:
            self.assertNotIn(f"<{prop}>", props_xml)
        self.assertEqual(record["offlineBootstrap"], {})
        self.assertIsNone(record["offlineUpdaterVersion"])

        # fresh tool pins on disk match record.tools
        pins_json = json.loads(Path(record["toolPinsJson"]).read_text(encoding="utf-8"))
        self.assertEqual(pins_json["schemaVersion"], 1)
        self.assertEqual(pins_json["tools"], record["tools"])

        # CompiledToolPins.g.cs contains the actual hashes of fresh fake outputs
        cs_text = Path(record["toolPinsSource"]).read_text(encoding="utf-8")
        self.assertIn("namespace FreedomInstaller.Core", cs_text)
        self.assertIn("internal static class CompiledToolPins", cs_text)
        for name, label in (
            ("client", "Client"),
            ("packer", "Packer"),
            ("unpacker", "Unpacker"),
        ):
            info = record["tools"][name]
            self.assertIn(f"internal const long {label}Bytes = {info['bytes']};", cs_text)
            self.assertIn(f'internal const string {label}Sha256 = "{info["sha256"]}";', cs_text)
        # record.toolPaths match real on-disk files that were hashed
        for name in ("client", "packer", "unpacker"):
            actual = Path(record["toolPaths"][name])
            self.assertTrue(actual.is_file())
            self.assertEqual(actual.stat().st_size, record["tools"][name]["bytes"])

        # package called once per profile, and all share the same freshly
        # generated pins file
        self.assertEqual(pkg_mock.call_count, 3)
        seen_profiles = set()
        pins_seen = set()
        for call in pkg_mock.call_args_list:
            ns = call.args[0]
            self.assertTrue(hasattr(ns, "python"))
            self.assertTrue(Path(ns.python).is_file())
            seen_profiles.add(ns.profile)
            pins_seen.add(Path(ns.build_pins_json).resolve())
        self.assertEqual(seen_profiles, {"tb322fc", "tb323fu", "tb710fu"})
        self.assertEqual(
            pins_seen, {Path(record["toolPinsJson"]).resolve()},
            "every profile must receive the same fresh tool-pins file",
        )

        # source digest recorded and stable
        paths = [e["path"] for e in record["sourceFiles"]]
        self.assertEqual(paths, sorted(paths))
        self.assertIn("tools/edl-ng/Program.cs", paths)

        # driver ZIP uses the required serial/amd64/qcusbser.sys archive path
        driver_zip = Path(record["inputs"]["BundledQualcommDriverArchive"])
        with zipfile.ZipFile(driver_zip) as zf:
            names = sorted(zf.namelist())
        self.assertEqual(names, ["qcser.cat", "qcser.inf", "serial/amd64/qcusbser.sys"])

    def test_vendor_mismatch_fails_before_fetch_or_compile(self):
        bad = self.repo_root / "vendor" / "aosp" / "testkey_rsa4096.pem"
        bad.write_bytes(b"corrupt")
        with mock.patch.object(
            fetch_build_inputs, "fetch_all"
        ) as fetch_mock, mock.patch.object(build_installer, "_run") as run_mock:
            with self.assertRaises(SystemExit):
                build_installer.prepare(
                    self.repo_root,
                    self.work_dir,
                    manifest_path=self.repo_root / "maintenance" / "build-inputs.json",
                )
        fetch_mock.assert_not_called()
        run_mock.assert_not_called()

    def test_missing_vendor_fails_before_fetch_or_compile(self):
        (self.repo_root / "vendor" / "qualcomm" / "g4" / "firehose.melf").unlink()
        with mock.patch.object(
            fetch_build_inputs, "fetch_all"
        ) as fetch_mock, mock.patch.object(build_installer, "_run") as run_mock:
            with self.assertRaises(SystemExit):
                build_installer.prepare(
                    self.repo_root,
                    self.work_dir,
                    manifest_path=self.repo_root / "maintenance" / "build-inputs.json",
                )
        fetch_mock.assert_not_called()
        run_mock.assert_not_called()

    def test_offline_assets_bind_props_and_record(self):
        script, bundle = self._offline_assets()
        originals = {script: script.read_bytes(), bundle: bundle.read_bytes()}
        record, _, _, _ = self._run_prepare(
            offline_bootstrap_script=str(script),
            offline_updater_bundle=str(bundle),
        )

        props_xml = Path(record["propsPath"]).read_text(encoding="utf-8")
        run_dir = Path(record["runDirectory"]).resolve()
        for prop, provided in (
            ("BundledOfflineBootstrapScript", script),
            ("BundledOfflineUpdaterBundle", bundle),
        ):
            entry = record["offlineBootstrap"][prop]
            staged = Path(entry["path"]).resolve()
            # Props and record name the validated snapshot, not the original.
            self.assertNotEqual(staged, provided.resolve())
            self.assertEqual(staged.parent, run_dir / "offline")
            self.assertEqual(record["inputs"][prop], str(staged))
            self.assertIn(
                f"<{prop}>{build_installer._xml_escape(str(staged))}</{prop}>",
                props_xml,
            )
            # The snapshot carries the exact bytes of the original and the
            # record digests describe that snapshot.
            self.assertEqual(staged.read_bytes(), originals[provided])
            self.assertEqual(entry["bytes"], len(originals[provided]))
            self.assertEqual(entry["sha256"], _sha(originals[provided]))
            self.assertEqual(entry["source"], build_installer._OFFLINE_ASSET_SOURCE)
            # The original input itself is left intact.
            self.assertEqual(provided.read_bytes(), originals[provided])

        # the original eleven properties are unaffected by the optional pair
        for prop in build_installer._PROPERTY_NAMES:
            self.assertIn(f"<{prop}>", props_xml)
        for prop in build_installer._OFFLINE_PROPERTY_NAMES:
            self.assertNotIn(prop, build_installer._PROPERTY_NAMES)
        self.assertIn("<BundledToolPinsSource>", props_xml)
        # The version is taken from the validated manifest, not a constant.
        self.assertIn(
            f"<{build_installer._OFFLINE_VERSION_PROPERTY}>0.8.0"
            f"</{build_installer._OFFLINE_VERSION_PROPERTY}>",
            props_xml,
        )
        self.assertEqual(record["offlineUpdaterVersion"], "0.8.0")

    def test_offline_assets_use_the_validated_manifest_version(self):
        offline = self.work_parent / "offline & inputs"
        script = _write(offline / "armada-offline-bootstrap", b"#!/bin/sh\n")
        bundle = _write(
            offline / "armada-offline-updater.tar.gz",
            _offline_bundle_bytes(version="0.8.1"),
        )
        record, _, _, _ = self._run_prepare(
            offline_bootstrap_script=str(script),
            offline_updater_bundle=str(bundle),
        )

        props_xml = Path(record["propsPath"]).read_text(encoding="utf-8")
        self.assertIn(
            f"<{build_installer._OFFLINE_VERSION_PROPERTY}>0.8.1"
            f"</{build_installer._OFFLINE_VERSION_PROPERTY}>",
            props_xml,
        )
        self.assertEqual(record["offlineUpdaterVersion"], "0.8.1")

    def test_offline_snapshot_ignores_source_mutation_before_publication(self):
        # Regression: Phase 0 validates the pair, but props and the record are
        # published after the whole build.  Mutating the originals in between
        # must not change the embedded bytes or the embedded version.
        script, bundle = self._offline_assets()
        snapshot_script = script.read_bytes()
        snapshot_bundle = bundle.read_bytes()
        mutated_script = b"#!/bin/sh\necho mutated\n"
        mutated_entry = b"#!/usr/bin/python3 -I\n# mutated updater\n"
        # A fully valid replacement bundle (new version, new entry bytes), so
        # a re-read of the original would really change what gets published.
        mutated_bundle = _offline_bundle_bytes(
            version="9.9.9",
            entry_bytes=mutated_entry,
            files=[
                {
                    "path": OFFLINE_ENTRY,
                    "bytes": len(mutated_entry),
                    "sha256": _sha(mutated_entry),
                    "mode": "0755",
                }
            ],
        )
        self.assertNotEqual(mutated_script, snapshot_script)
        self.assertNotEqual(mutated_bundle, snapshot_bundle)
        mutations = []

        def fake_package(ns):
            out = Path(ns.output)
            out.parent.mkdir(parents=True, exist_ok=True)
            BuildInstallerTestHelpers.write_valid_zip(out, ns.profile)
            if not mutations:
                # Deterministic, single-threaded edit after the pair has been
                # validated and before anything is published: no race.
                script.write_bytes(mutated_script)
                bundle.write_bytes(mutated_bundle)
                mutations.append(True)
            return {"profile": ns.profile, "output": ns.output}

        with mock.patch.object(
            fetch_build_inputs, "fetch_all", side_effect=_fake_fetch_side_effect
        ), mock.patch.object(
            build_installer, "_run", side_effect=_fake_compile(self.repo_root, None)
        ), mock.patch.object(
            build_installer.package_edl, "package", side_effect=fake_package
        ):
            record = build_installer.prepare(
                self.repo_root,
                self.work_dir,
                manifest_path=self.repo_root / "maintenance" / "build-inputs.json",
                offline_bootstrap_script=str(script),
                offline_updater_bundle=str(bundle),
            )

        # The mutation really happened.
        self.assertTrue(mutations)
        self.assertEqual(script.read_bytes(), mutated_script)
        self.assertEqual(bundle.read_bytes(), mutated_bundle)

        # The published version is the one validated from the snapshot.
        self.assertEqual(record["offlineUpdaterVersion"], "0.8.0")
        props_xml = Path(record["propsPath"]).read_text(encoding="utf-8")
        self.assertIn(
            f"<{build_installer._OFFLINE_VERSION_PROPERTY}>0.8.0"
            f"</{build_installer._OFFLINE_VERSION_PROPERTY}>",
            props_xml,
        )
        self.assertNotIn("9.9.9", props_xml)

        # Props point at snapshots whose bytes still match what was validated,
        # and the record digests describe those same bytes.
        for prop, provided, snapshot in (
            ("BundledOfflineBootstrapScript", script, snapshot_script),
            ("BundledOfflineUpdaterBundle", bundle, snapshot_bundle),
        ):
            entry = record["offlineBootstrap"][prop]
            staged = Path(entry["path"]).resolve()
            self.assertNotEqual(staged, provided.resolve())
            self.assertIn(
                f"<{prop}>{build_installer._xml_escape(str(staged))}</{prop}>",
                props_xml,
            )
            self.assertEqual(staged.read_bytes(), snapshot)
            self.assertEqual(entry["bytes"], len(snapshot))
            self.assertEqual(entry["sha256"], _sha(snapshot))

        # The embedded version really is the one inside the snapshot manifest.
        staged_bundle = Path(
            record["offlineBootstrap"]["BundledOfflineUpdaterBundle"]["path"]
        )
        with tarfile.open(str(staged_bundle), mode="r:gz") as archive:
            staged_document = json.loads(
                archive.extractfile("manifest.json").read().decode("utf-8")
            )
        self.assertEqual(staged_document["version"], record["offlineUpdaterVersion"])
        self.assertEqual(staged_document["version"], "0.8.0")

    def test_offline_bundle_over_the_transport_limit_fails_before_build(self):
        # The managed SSH session refuses uploads above 256 MiB; accepting a
        # larger bundle here would only fail later on the tablet.
        self.assertEqual(
            build_installer._OFFLINE_MAX_BUNDLE_BYTES, 256 * 1024 * 1024
        )
        offline = self.work_parent / "offline & inputs"
        script = _write(offline / "armada-offline-bootstrap", b"#!/bin/sh\n")
        bundle = offline / "too-big.tar.gz"
        # Sparse file: only the logical size matters, the manifest is never read.
        with open(bundle, "wb") as handle:
            handle.truncate(build_installer._OFFLINE_MAX_BUNDLE_BYTES + 1)

        with mock.patch.object(
            fetch_build_inputs, "fetch_all"
        ) as fetch_mock, mock.patch.object(build_installer, "_run") as run_mock:
            with self.assertRaises(SystemExit) as caught:
                build_installer.prepare(
                    self.repo_root,
                    self.work_dir,
                    manifest_path=(
                        self.repo_root / "maintenance" / "build-inputs.json"
                    ),
                    offline_bootstrap_script=str(script),
                    offline_updater_bundle=str(bundle),
                )
        fetch_mock.assert_not_called()
        run_mock.assert_not_called()
        message = str(caught.exception)
        self.assertIn("BundledOfflineUpdaterBundle", message)
        self.assertIn("size is out of bounds", message)

    def test_offline_bootstrap_script_shares_the_transport_upload_limit(self):
        # Both offline inputs travel through the one managed SSH session, whose
        # MaxUploadBytes is 256 MiB, so the production cap must match that
        # contract and must actually be applied to the script, not only to the
        # bundle.
        self.assertEqual(
            build_installer._OFFLINE_MAX_UPLOAD_BYTES, 256 * 1024 * 1024
        )
        self.assertEqual(
            build_installer._OFFLINE_MAX_BUNDLE_BYTES,
            build_installer._OFFLINE_MAX_UPLOAD_BYTES,
        )
        script, bundle = self._offline_assets()
        seen = {}
        real_stage = build_installer._stage_offline_asset

        def stage_recording_limit(prop, source, dest_dir, limit=None):
            seen[prop] = limit
            return real_stage(prop, source, dest_dir, limit=limit)

        with mock.patch.object(
            build_installer, "_stage_offline_asset",
            side_effect=stage_recording_limit,
        ):
            staged, version, _ = build_installer._validate_offline_inputs(
                str(script), str(bundle), self.work_dir
            )

        self.assertEqual(
            seen["BundledOfflineBootstrapScript"],
            build_installer._OFFLINE_MAX_UPLOAD_BYTES,
        )
        self.assertEqual(
            seen["BundledOfflineUpdaterBundle"],
            build_installer._OFFLINE_MAX_BUNDLE_BYTES,
        )
        self.assertEqual(version, "0.8.0")
        # Snapshot semantics are untouched: the staged script is a copy of the
        # original bytes, described by the digest computed while copying.
        entry = staged["BundledOfflineBootstrapScript"]
        self.assertEqual(Path(entry["path"]).read_bytes(), script.read_bytes())
        self.assertEqual(entry["bytes"], len(script.read_bytes()))
        self.assertEqual(entry["sha256"], _sha(script.read_bytes()))

    def test_offline_bootstrap_script_must_not_be_empty(self):
        # The runtime upload accepts 1..256 MiB inclusive, so a zero-byte
        # script is refused here instead of failing during deployment.
        offline = self.work_parent / "offline & inputs"
        script = _write(offline / "empty-bootstrap", b"")
        bundle = _write(
            offline / "armada-offline-updater.tar.gz", _offline_bundle_bytes()
        )
        self.assertEqual(script.stat().st_size, 0)

        with mock.patch.object(
            fetch_build_inputs, "fetch_all"
        ) as fetch_mock, mock.patch.object(build_installer, "_run") as run_mock:
            with self.assertRaises(SystemExit) as caught:
                build_installer.prepare(
                    self.repo_root,
                    self.work_dir,
                    manifest_path=(
                        self.repo_root / "maintenance" / "build-inputs.json"
                    ),
                    offline_bootstrap_script=str(script),
                    offline_updater_bundle=str(bundle),
                )
        fetch_mock.assert_not_called()
        run_mock.assert_not_called()
        message = str(caught.exception)
        self.assertIn("BundledOfflineBootstrapScript", message)
        self.assertIn("size is out of bounds", message)

    def test_offline_bootstrap_script_at_the_reduced_cap_is_accepted(self):
        # A 256 MiB fixture is unnecessary: the injected cap drives the same
        # inclusive comparison the real transport limit uses.
        limit = 64
        offline = self.work_parent / "offline & inputs"
        payload = b"x" * limit
        script = _write(offline / "bootstrap-at-cap", payload)
        bundle = _write(
            offline / "armada-offline-updater.tar.gz", _offline_bundle_bytes()
        )

        with mock.patch.object(
            build_installer, "_OFFLINE_MAX_UPLOAD_BYTES", limit
        ):
            staged, version, _ = build_installer._validate_offline_inputs(
                str(script), str(bundle), self.work_dir
            )

        entry = staged["BundledOfflineBootstrapScript"]
        self.assertEqual(entry["bytes"], limit)
        self.assertEqual(Path(entry["path"]).read_bytes(), payload)
        self.assertEqual(entry["sha256"], _sha(payload))
        self.assertEqual(version, "0.8.0")

    def test_offline_bootstrap_script_over_the_reduced_cap_is_rejected(self):
        limit = 64
        offline = self.work_parent / "offline & inputs"
        script = _write(offline / "bootstrap-over-cap", b"x" * (limit + 1))
        bundle = _write(
            offline / "armada-offline-updater.tar.gz", _offline_bundle_bytes()
        )

        with mock.patch.object(
            build_installer, "_OFFLINE_MAX_UPLOAD_BYTES", limit
        ):
            with self.assertRaises(SystemExit) as caught:
                build_installer._validate_offline_inputs(
                    str(script), str(bundle), self.work_dir
                )

        message = str(caught.exception)
        self.assertIn("BundledOfflineBootstrapScript", message)
        self.assertIn("size is out of bounds", message)
        # Nothing from the rejected script was left staged for a later phase.
        for staged in (self.work_dir / "runs").rglob("*"):
            if staged.is_file():
                self.assertNotEqual(staged.name, "BundledOfflineBootstrapScript")

    def test_offline_assets_must_be_supplied_together(self):
        script, bundle = self._offline_assets()
        for kwargs in (
            {"offline_bootstrap_script": str(script)},
            {"offline_updater_bundle": str(bundle)},
        ):
            with self.subTest(kwargs=kwargs):
                with mock.patch.object(
                    fetch_build_inputs, "fetch_all"
                ) as fetch_mock, mock.patch.object(
                    build_installer, "_run"
                ) as run_mock:
                    with self.assertRaises(SystemExit):
                        build_installer.prepare(
                            self.repo_root,
                            self.work_dir,
                            manifest_path=(
                                self.repo_root / "maintenance" / "build-inputs.json"
                            ),
                            **kwargs,
                        )
                fetch_mock.assert_not_called()
                run_mock.assert_not_called()

    def test_offline_asset_invalid_path_fails_before_build(self):
        script, bundle = self._offline_assets()
        missing = self.work_parent / "offline & inputs" / "does-not-exist"
        directory = self.work_parent / "offline & inputs" / "a-directory"
        directory.mkdir(parents=True, exist_ok=True)

        for label, other in (
            ("missing path", missing),
            ("directory instead of file", directory),
        ):
            with self.subTest(case=label):
                with mock.patch.object(
                    fetch_build_inputs, "fetch_all"
                ) as fetch_mock, mock.patch.object(
                    build_installer, "_run"
                ) as run_mock:
                    with self.assertRaises(SystemExit):
                        build_installer.prepare(
                            self.repo_root,
                            self.work_dir,
                            manifest_path=(
                                self.repo_root / "maintenance" / "build-inputs.json"
                            ),
                            offline_bootstrap_script=str(script),
                            offline_updater_bundle=str(other),
                        )
                fetch_mock.assert_not_called()
                run_mock.assert_not_called()

    def _prepare_with_bundle(self, bundle_bytes):
        """Prepare with a paired script and this exact bundle payload."""
        offline = self.work_parent / "offline & inputs"
        script = _write(offline / "armada-offline-bootstrap", b"#!/bin/sh\n")
        bundle = _write(offline / "armada-offline-updater.tar.gz", bundle_bytes)
        with mock.patch.object(
            fetch_build_inputs, "fetch_all"
        ) as fetch_mock, mock.patch.object(
            build_installer, "_run"
        ) as run_mock:
            with self.assertRaises(SystemExit) as caught:
                build_installer.prepare(
                    self.repo_root,
                    self.work_dir,
                    manifest_path=(
                        self.repo_root / "maintenance" / "build-inputs.json"
                    ),
                    offline_bootstrap_script=str(script),
                    offline_updater_bundle=str(bundle),
                )
        fetch_mock.assert_not_called()
        run_mock.assert_not_called()
        return str(caught.exception)

    def test_offline_bundle_without_capability_fails_before_build(self):
        # The earlier 0.8.0 bundle that only listed files must not be accepted.
        message = self._prepare_with_bundle(_offline_bundle_bytes(capabilities=None))
        self.assertIn(build_installer._OFFLINE_REQUIRED_CAPABILITY, message)

    def test_offline_bundle_with_malformed_capabilities_fails_before_build(self):
        cases = (
            "preflight-session-v1",
            {"preflight-session-v1": True},
            [],
            [1],
            ["Preflight Session"],
            ["preflight-session-v1", "preflight-session-v1"],
            ["a" * (build_installer._OFFLINE_MAX_CAPABILITY_LENGTH + 1)],
            ["cap-%d" % index
             for index in range(build_installer._OFFLINE_MAX_CAPABILITIES + 1)],
        )
        for value in cases:
            with self.subTest(capabilities=value):
                message = self._prepare_with_bundle(
                    _offline_bundle_bytes(capabilities=value))
                self.assertIn("offline bundle error", message)

    def test_offline_bundle_fixture_preserves_malformed_capability_types(self):
        # Regression: the fixture must serialize the caller's exact type so the
        # malformed-capabilities cases really exercise the validator instead of
        # a list() coercion of a string into characters.
        for value in ("preflight-session-v1", {"preflight-session-v1": True}, 7):
            with self.subTest(capabilities=value):
                data = _offline_bundle_bytes(capabilities=value)
                with tarfile.open(fileobj=io.BytesIO(data), mode="r:gz") as archive:
                    document = json.loads(
                        archive.extractfile("manifest.json").read().decode("utf-8")
                    )
                self.assertEqual(document["capabilities"], value)
                self.assertIs(type(document["capabilities"]), type(value))

    def test_offline_bundle_identity_and_version_are_validated(self):
        cases = (
            {"schemaVersion": 2},
            {"bundle": "other-bundle"},
            {"version": "not-a-version"},
            {"entry": "usr/bin/other"},
        )
        for overrides in cases:
            with self.subTest(**overrides):
                message = self._prepare_with_bundle(
                    _offline_bundle_bytes(**overrides))
                self.assertIn("offline bundle error", message)

    def test_offline_bundle_version_segments_must_fit_int32(self):
        # The bootstrap coordinator parses this version with int.TryParse, so a
        # segment above Int32.MaxValue would build here and fail on the tablet.
        rejected = (
            "2147483648",
            "0.2147483648",
            "0.8.99999999999999999999",
            # Far beyond any Python int-from-string digit limit: the check must
            # compare digit strings and never raise ValueError.
            "9" * 5000,
        )
        for version in rejected:
            with self.subTest(version=version):
                message = self._prepare_with_bundle(
                    _offline_bundle_bytes(version=version))
                self.assertIn("version", message)
                self.assertIn("32-bit", message)

        # The maximum itself succeeds, and leading zeros stay accepted because
        # int.TryParse treats them as insignificant.
        accepted = (
            "2147483647",
            "0008.000.2147483647",
            "0.8." + "0" * 5000 + "1",
        )
        for version in accepted:
            with self.subTest(version=version):
                document = _offline_manifest(
                    version=version, capabilities=["preflight-session-v1"])
                spec = build_installer._validate_offline_manifest(
                    "armada-offline-updater.tar.gz", document)
                self.assertEqual(spec["path"], OFFLINE_ENTRY)

    def test_offline_bundle_with_duplicate_manifest_fails_before_build(self):
        data = json.dumps(_offline_manifest()).encode("utf-8")
        message = self._prepare_with_bundle(
            _tar_gz_bytes([("manifest.json", data), ("manifest.json", data)]))
        self.assertIn("more than one manifest.json", message)

    def test_offline_bundle_with_oversized_manifest_fails_before_build(self):
        data = b" " * (build_installer._OFFLINE_MAX_MANIFEST_BYTES + 1)
        message = self._prepare_with_bundle(_tar_gz_bytes([("manifest.json", data)]))
        self.assertIn("size is out of bounds", message)

    def test_offline_bundle_with_non_regular_manifest_fails_before_build(self):
        buffer = io.BytesIO()
        with tarfile.open(fileobj=buffer, mode="w:gz") as archive:
            info = tarfile.TarInfo("manifest.json")
            info.type = tarfile.SYMTYPE
            info.linkname = "elsewhere.json"
            archive.addfile(info)
        message = self._prepare_with_bundle(buffer.getvalue())
        self.assertIn("not a regular file", message)

    def test_offline_bundle_entry_member_is_accepted(self):
        # Allow semantics: the real small updater member, declared with its
        # exact size and digest, passes both validation passes.
        offline = self.work_parent / "offline & inputs"
        bundle = _write(
            offline / "accepted.tar.gz",
            _offline_bundle_bytes(),
        )
        document = build_installer._offline_manifest_document(str(bundle))
        spec = build_installer._validate_offline_manifest(str(bundle), document)
        self.assertEqual(spec["path"], OFFLINE_ENTRY)
        self.assertEqual(spec["bytes"], len(OFFLINE_ENTRY_BYTES))
        build_installer._offline_entry_member(str(bundle), spec)
        with tarfile.open(str(bundle), mode="r:gz") as archive:
            names = sorted(member.name for member in archive)
        self.assertEqual(names, ["manifest.json", OFFLINE_ENTRY])

    def test_offline_bundle_with_missing_entry_member_fails_before_build(self):
        message = self._prepare_with_bundle(_offline_bundle_bytes(entry_bytes=None))
        self.assertIn(f"bundle has no {OFFLINE_ENTRY}", message)

    def test_offline_bundle_with_duplicate_entry_member_fails_before_build(self):
        document = _offline_manifest()
        document["capabilities"] = ["preflight-session-v1"]
        message = self._prepare_with_bundle(
            _tar_gz_bytes(
                [
                    ("manifest.json", json.dumps(document).encode("utf-8")),
                    (OFFLINE_ENTRY, OFFLINE_ENTRY_BYTES, OFFLINE_ENTRY_MODE),
                    (OFFLINE_ENTRY, OFFLINE_ENTRY_BYTES, OFFLINE_ENTRY_MODE),
                ]
            )
        )
        self.assertIn(f"more than one {OFFLINE_ENTRY}", message)

    def test_offline_bundle_with_entry_digest_mismatch_fails_before_build(self):
        files = [
            {
                "path": OFFLINE_ENTRY,
                "bytes": len(OFFLINE_ENTRY_BYTES),
                "sha256": _sha(b"tampered-updater"),
                "mode": "0755",
            }
        ]
        message = self._prepare_with_bundle(_offline_bundle_bytes(files=files))
        self.assertIn("sha256", message)
        self.assertIn(OFFLINE_ENTRY, message)

    def test_offline_bundle_with_entry_size_mismatch_fails_before_build(self):
        files = [
            {
                "path": OFFLINE_ENTRY,
                "bytes": len(OFFLINE_ENTRY_BYTES) + 3,
                "sha256": _sha(OFFLINE_ENTRY_BYTES),
                "mode": "0755",
            }
        ]
        message = self._prepare_with_bundle(_offline_bundle_bytes(files=files))
        self.assertIn("size", message)
        self.assertIn(str(len(OFFLINE_ENTRY_BYTES)), message)

    def _bundle_with_entry_type(self, member_type, linkname=None):
        document = _offline_manifest()
        document["capabilities"] = ["preflight-session-v1"]
        payload = json.dumps(document).encode("utf-8")
        buffer = io.BytesIO()
        with tarfile.open(fileobj=buffer, mode="w:gz") as archive:
            manifest = tarfile.TarInfo("manifest.json")
            manifest.size = len(payload)
            manifest.mode = 0o644
            archive.addfile(manifest, io.BytesIO(payload))
            entry = tarfile.TarInfo(OFFLINE_ENTRY)
            entry.type = member_type
            entry.mode = OFFLINE_ENTRY_MODE
            if linkname is not None:
                entry.linkname = linkname
            archive.addfile(entry)
        return buffer.getvalue()

    def test_offline_bundle_with_non_regular_entry_fails_before_build(self):
        for label, member_type, linkname in (
            ("symlink", tarfile.SYMTYPE, "elsewhere-update"),
            ("hard link", tarfile.LNKTYPE, "manifest.json"),
            ("directory", tarfile.DIRTYPE, None),
        ):
            with self.subTest(case=label):
                message = self._prepare_with_bundle(
                    self._bundle_with_entry_type(member_type, linkname)
                )
                self.assertIn(f"{OFFLINE_ENTRY} is not a regular file", message)

    def test_offline_bundle_with_non_executable_entry_fails_before_build(self):
        message = self._prepare_with_bundle(
            _offline_bundle_bytes(entry_mode=0o644)
        )
        self.assertIn(f"{OFFLINE_ENTRY} is not executable", message)

    def test_offline_manifest_must_declare_the_entry_file(self):
        cases = (
            (
                "entry not declared",
                [
                    {
                        "path": "usr/libexec/armada/other",
                        "bytes": 1,
                        "sha256": "0" * 64,
                        "mode": "0755",
                    }
                ],
                "must declare the entry",
            ),
            (
                "entry declared twice",
                [
                    {
                        "path": OFFLINE_ENTRY,
                        "bytes": len(OFFLINE_ENTRY_BYTES),
                        "sha256": _sha(OFFLINE_ENTRY_BYTES),
                        "mode": "0755",
                    },
                    {
                        "path": OFFLINE_ENTRY,
                        "bytes": len(OFFLINE_ENTRY_BYTES),
                        "sha256": _sha(OFFLINE_ENTRY_BYTES),
                        "mode": "0755",
                    },
                ],
                "more than once",
            ),
            (
                "entry mode not executable",
                [
                    {
                        "path": OFFLINE_ENTRY,
                        "bytes": len(OFFLINE_ENTRY_BYTES),
                        "sha256": _sha(OFFLINE_ENTRY_BYTES),
                        "mode": "0644",
                    }
                ],
                "not executable",
            ),
            (
                "entry path traversal",
                [
                    {
                        "path": "usr/../../etc/passwd",
                        "bytes": 1,
                        "sha256": "0" * 64,
                        "mode": "0755",
                    }
                ],
                "not normalized",
            ),
        )
        for label, files, fragment in cases:
            with self.subTest(case=label):
                message = self._prepare_with_bundle(
                    _offline_bundle_bytes(files=files)
                )
                self.assertIn(fragment, message)

    def test_offline_bundle_that_is_not_a_tar_gz_fails_before_build(self):
        message = self._prepare_with_bundle(b"not a tar.gz\n")
        self.assertIn("not a readable tar.gz", message)


def _fake_split_scripts(argv, cwd, env, phase):
    '''Distinguishable _run hook for the CLI test that yields fresh outputs.'''
    joined = " ".join(str(a) for a in argv)
    if "build-edl-ng.sh" in joined:
        out = Path(argv[2])
        out.mkdir(parents=True, exist_ok=True)
        _write(out / "edl-ng.exe", b"cli-client-xyz")
        return
    if "build-boot-packers.sh" in joined:
        out = Path(argv[3])
        out.mkdir(parents=True, exist_ok=True)
        _write(out / "MultiBootKernelPatcher.exe", b"cli-packer-xyz")
        _write(out / "UnpackTool.exe", b"cli-unpacker-xyz")
        return
    raise AssertionError(f"unexpected _run: {argv}")


def _cli_fetch(manifest, output_dir, ids=None):
    out = Path(output_dir)
    out.mkdir(parents=True, exist_ok=True)
    files = {}
    for dl in manifest["downloads"]:
        rid = dl["id"]
        if ids is not None and rid not in ids:
            continue
        if rid == "libusbPackage":
            data = _make_nupkg()
        elif rid == "libusbLicense":
            data = b"libusb license text\n"
        else:
            data = f"{rid}-payload\n".encode("utf-8")
        p = out / dl["name"]
        _write(p, data)
        dl["bytes"] = len(data)
        dl["sha256"] = _sha(data)
        files[rid] = str(p.resolve())
    return files


class CliTests(unittest.TestCase):
    def test_cli_publish_uses_generated_props_and_does_not_hit_release(self):
        with tempfile.TemporaryDirectory(prefix="bi cli \u2013 ") as tmp:
            tmp = Path(tmp)
            repo_root, _ = _build_fixture_repo(tmp / "repo & dst")
            work_dir = tmp / "work dir"
            output_dir = tmp / "publish out & x"
            script = _write(
                tmp / "offline & inputs" / "armada-offline-bootstrap",
                b"#!/bin/sh\necho bootstrap\n",
            )
            bundle = _write(
                tmp / "offline & inputs" / "armada-offline-updater.tar.gz",
                _offline_bundle_bytes(),
            )

            published_calls = []

            def run_hook(argv, cwd, env, phase):
                joined = " ".join(str(a) for a in argv)
                if "build-edl-ng.sh" in joined or "build-boot-packers.sh" in joined:
                    return _fake_split_scripts(argv, cwd, env, phase)
                if "dotnet" in argv[0]:
                    published_calls.append(list(argv))
                    Path(output_dir).mkdir(parents=True, exist_ok=True)
                    return
                raise AssertionError(f"unexpected _run: {argv}")

            def fake_cli_package(ns):
                out = Path(ns.output)
                out.parent.mkdir(parents=True, exist_ok=True)
                BuildInstallerTestHelpers.write_valid_zip(out, ns.profile)
                return {"profile": ns.profile, "output": ns.output}

            with mock.patch.object(
                fetch_build_inputs, "fetch_all", side_effect=_cli_fetch
            ), mock.patch.object(build_installer, "_run", side_effect=run_hook), mock.patch.object(
                build_installer.package_edl, "package", side_effect=fake_cli_package
            ), mock.patch.object(
                build_installer, "_HERE", repo_root / "maintenance"
            ):
                rc = build_installer.main(
                    [
                        "--work-dir", str(work_dir),
                        "--output-dir", str(output_dir),
                        "--manifest", str(repo_root / "maintenance" / "build-inputs.json"),
                        "--offline-bootstrap-script", str(script),
                        "--offline-updater-bundle", str(bundle),
                    ]
                )
            self.assertEqual(rc, 0)
            self.assertEqual(len(published_calls), 1)
            publish_argv = published_calls[0]
            joined = " ".join(publish_argv)
            self.assertIn("-p:BundledBuildInputsProps=", joined)
            props_arg = next(a for a in publish_argv if a.startswith("-p:BundledBuildInputsProps="))
            props_path = Path(props_arg.split("=", 1)[1])
            self.assertEqual(props_path.resolve(), (work_dir / "build-inputs.props").resolve())
            self.assertTrue(props_path.is_file())
            # configuration must be Release and no build-input release URL
            cfg_idx = publish_argv.index("-c")
            self.assertEqual(publish_argv[cfg_idx + 1], "Release")
            self.assertNotIn("/releases/download/build-inputs", joined)
            self.assertNotIn("old-release", joined)
            # the publish props carry the snapshots staged for this run
            props_xml = props_path.read_text(encoding="utf-8")
            run_dirs = list((work_dir / "runs").iterdir())
            self.assertEqual(len(run_dirs), 1)
            staged_dir = run_dirs[0] / "offline"
            for prop, provided in (
                ("BundledOfflineBootstrapScript", script),
                ("BundledOfflineUpdaterBundle", bundle),
            ):
                staged = (staged_dir / prop).resolve()
                self.assertNotEqual(staged, provided.resolve())
                value = build_installer._xml_escape(str(staged))
                self.assertIn(f"<{prop}>{value}</{prop}>", props_xml)
                self.assertEqual(staged.read_bytes(), provided.read_bytes())

    def test_publish_without_offline_assets_fails_before_prepare(self):
        with tempfile.TemporaryDirectory(prefix="bi cli \u2013 ") as tmp:
            tmp = Path(tmp)
            repo_root, _ = _build_fixture_repo(tmp / "repo & dst")
            work_dir = tmp / "work dir"
            output_dir = tmp / "publish out"
            script = _write(tmp / "offline" / "bootstrap", b"#!/bin/sh\n")
            bundle = _write(tmp / "offline" / "updater.tar.gz",
                            _offline_bundle_bytes())

            cases = (
                [],
                ["--offline-bootstrap-script", str(script)],
                ["--offline-updater-bundle", str(bundle)],
            )
            stderr = io.StringIO()
            with mock.patch.object(
                fetch_build_inputs, "fetch_all"
            ) as fetch_mock, mock.patch.object(
                build_installer, "_run"
            ) as run_mock, mock.patch.object(
                build_installer, "_HERE", repo_root / "maintenance"
            ), contextlib.redirect_stderr(stderr):
                for extra in cases:
                    with self.subTest(extra=extra):
                        with self.assertRaises(SystemExit):
                            build_installer.main(
                                [
                                    "--work-dir", str(work_dir),
                                    "--output-dir", str(output_dir),
                                    "--manifest", str(
                                        repo_root / "maintenance" / "build-inputs.json"
                                    ),
                                ] + extra
                            )
            fetch_mock.assert_not_called()
            run_mock.assert_not_called()
            self.assertIn("--offline-bootstrap-script", stderr.getvalue())


if __name__ == "__main__":
    unittest.main()
