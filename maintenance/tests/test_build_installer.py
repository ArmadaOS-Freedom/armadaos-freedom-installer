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
        "entry": "usr/libexec/armada/armada-offline-update",
        "files": [
            {
                "path": "usr/libexec/armada/armada-offline-update",
                "bytes": 1,
                "sha256": "0" * 64,
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


def _offline_bundle_bytes(capabilities=("preflight-session-v1",), **overrides):
    document = _offline_manifest(**overrides)
    if capabilities is not None:
        document["capabilities"] = list(capabilities)
    return _tar_gz_bytes([("manifest.json", json.dumps(document).encode("utf-8"))])


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
        record, _, _, _ = self._run_prepare(
            offline_bootstrap_script=str(script),
            offline_updater_bundle=str(bundle),
        )

        props_xml = Path(record["propsPath"]).read_text(encoding="utf-8")
        for prop, provided in (
            ("BundledOfflineBootstrapScript", script),
            ("BundledOfflineUpdaterBundle", bundle),
        ):
            resolved = str(provided.resolve())
            self.assertIn(
                f"<{prop}>{build_installer._xml_escape(resolved)}</{prop}>",
                props_xml,
            )
            self.assertEqual(record["inputs"][prop], resolved)
            entry = record["offlineBootstrap"][prop]
            self.assertEqual(Path(entry["path"]).resolve(), Path(resolved))
            self.assertEqual(entry["bytes"], provided.stat().st_size)
            self.assertEqual(entry["sha256"], _sha(provided.read_bytes()))
            self.assertEqual(entry["source"], build_installer._OFFLINE_ASSET_SOURCE)

        # the original eleven properties are unaffected by the optional pair
        for prop in build_installer._PROPERTY_NAMES:
            self.assertIn(f"<{prop}>", props_xml)
        for prop in build_installer._OFFLINE_PROPERTY_NAMES:
            self.assertNotIn(prop, build_installer._PROPERTY_NAMES)
        self.assertIn("<BundledToolPinsSource>", props_xml)

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
            # the publish props carry both offline assets
            props_xml = props_path.read_text(encoding="utf-8")
            for prop, provided in (
                ("BundledOfflineBootstrapScript", script),
                ("BundledOfflineUpdaterBundle", bundle),
            ):
                value = build_installer._xml_escape(str(provided.resolve()))
                self.assertIn(f"<{prop}>{value}</{prop}>", props_xml)

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
