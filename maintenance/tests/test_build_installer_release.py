# SPDX-License-Identifier: GPL-3.0-only
import argparse
from pathlib import Path
import plistlib
import struct
import sys
import tempfile
import unittest
from unittest.mock import patch
import xml.etree.ElementTree as ET

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import build_installer_release as release


class InstallerReleaseTests(unittest.TestCase):
    def test_release_number_has_three_numeric_components(self):
        self.assertEqual(release.release_version("1.1.6"), "1.1.6")
        for invalid in ("", "v1.1.6", "1.1", "1.1.6.0", "1.1.6-preview", "01.1.6", "99999.1.0"):
            with self.subTest(version=invalid), self.assertRaises(argparse.ArgumentTypeError):
                release.release_version(invalid)

    def test_new_release_runs_increment_patch_and_reruns_keep_the_version(self):
        self.assertEqual(release.version_for_run("1.1.6", 1), "1.1.6")
        self.assertEqual(release.version_for_run("1.1.6", 2), "1.1.7")
        self.assertEqual(release.version_for_run("1.1.6", 2), "1.1.7")
        with self.assertRaisesRegex(ValueError, "positive"):
            release.version_for_run("1.1.6", 0)
        with self.assertRaises(argparse.ArgumentTypeError):
            release.version_for_run("1.1.65535", 2)

    def test_release_properties_preserve_the_prepared_props_and_escape_values(self):
        with tempfile.TemporaryDirectory() as directory:
            props = Path(directory) / "build-inputs.props"
            props.write_text("<Project><PropertyGroup><BundledTool>tool</BundledTool></PropertyGroup></Project>",
                             encoding="utf-8")
            revision = "e" * 40
            release.write_release_props(props, "1.1.6", revision, {
                "BundledOfflineBootstrapScript": "/build/a&b/bootstrap",
                "BundledOfflineUpdaterVersion": "1.0.0",
            })
            document = ET.parse(props)
            for name, value in {
                "BundledTool": "tool",
                "Version": "1.1.6",
                "SourceRevisionId": revision,
                "BundledOfflineBootstrapScript": "/build/a&b/bootstrap",
                "BundledOfflineUpdaterVersion": "1.0.0",
            }.items():
                self.assertEqual(document.findtext(f".//{name}"), value)

    def test_macos_verifies_the_managed_assembly_for_a_dotted_launcher_name(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            app = root / "Installer.app"
            contents = app / "Contents"
            macos_dir = contents / "MacOS"
            macos_dir.mkdir(parents=True)
            version = "1.1.6"
            revision = "e" * 40
            launcher_name = "FreedomInstaller.Mac"
            with (contents / "Info.plist").open("wb") as stream:
                plistlib.dump({
                    "CFBundleExecutable": launcher_name,
                    "CFBundleShortVersionString": version,
                    "CFBundleVersion": version,
                }, stream)
            script = root / "bootstrap"
            script.write_bytes(b"bootstrap content")
            bundle = root / "updater"
            bundle.write_bytes(b"bundle content")
            assembly = macos_dir / f"{launcher_name}.dll"
            assembly.write_bytes(
                f"{version}+{revision}".encode("utf-8") + script.read_bytes() + bundle.read_bytes()
            )

            with patch.object(release.subprocess, "check_output", return_value="arm64\n") as lipo:
                release.verify_macos_app(app, version, revision, script, bundle)
            lipo.assert_called_once_with(["lipo", "-archs", macos_dir / launcher_name], text=True)

    def test_packages_must_have_matching_version_and_both_offline_components(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            script = root / "bootstrap"
            bundle = root / "updater"
            binary = root / "app"
            script.write_bytes(b"bootstrap content")
            bundle.write_bytes(b"bundle content")
            revision = "e" * 40
            version = "1.1.6"
            prefix = bytearray(128)
            prefix[:2] = b"MZ"
            struct.pack_into("<I", prefix, 0x3C, 64)
            prefix[64:70] = b"PE\0\0\x64\x86"

            for windows_binary in (True, False):
                with self.subTest(windows_binary=windows_binary):
                    metadata = f"{version}+{revision}".encode("utf-16le" if windows_binary else "utf-8")
                    signed = (bytes(prefix) if windows_binary else b"") + metadata + script.read_bytes()
                    binary.write_bytes(signed + bundle.read_bytes())
                    release.verify_payload(binary, version, revision, script, bundle,
                                           windows_binary=windows_binary)
                    with self.assertRaisesRegex(SystemExit, "Release must report"):
                        release.verify_payload(binary, "1.1.7", revision, script, bundle,
                                               windows_binary=windows_binary)
                    binary.write_bytes(signed)
                    with self.assertRaisesRegex(SystemExit, "does not embed"):
                        release.verify_payload(binary, version, revision, script, bundle,
                                               windows_binary=windows_binary)
