#!/usr/bin/env python3
"""Focused checks for maintenance/prepare_test_inputs.py.

Only the pure input-mapping and validation helpers are exercised; the real
prepare()/CMake run is a CI step, not a unit test.  Nothing here restates the
build record schema beyond the public key names the mapper consumes, except
two suites that skip when their variables are unset: the TB710FU toolchain
bundle, and the compiled-tool-pins pair (BUILD_TOOL_PINS_JSON plus the
EDL_CLIENT_TEST_BINARY PE).  Their variable names are pinned literally so a
mapping omission fails here instead of quietly dropping a suite.
"""

import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

MAINTENANCE = Path(__file__).resolve().parents[1]
if str(MAINTENANCE) not in sys.path:
    sys.path.insert(0, str(MAINTENANCE))

import prepare_test_inputs  # noqa: E402


def _touch(path, content):
    path.write_bytes(content)
    return str(path)


class ComposeEnvironmentTests(unittest.TestCase):
    def _fixture(self, root):
        root = Path(root)
        inputs = {}
        for variable, key in prepare_test_inputs._RECORD_INPUTS:
            content = b"MZ\x90\x00" if variable in prepare_test_inputs._WINDOWS_PE_VARIABLES else b"PK\x03\x04"
            inputs[key] = _touch(root / (key + ".bin"), content)
        props = _touch(root / "build-inputs.props", b"<Project />\n")
        pins = _touch(
            root / "tool-pins.json", b'{"schemaVersion": 1, "tools": {}}\n')
        client = _touch(root / "edl-ng.exe", b"MZ\x90\x00")
        native = {}
        for variable in prepare_test_inputs._LINUX_ELF_VARIABLES:
            key = "unpacker" if "UNPACKER" in variable else "packer"
            native[variable] = _touch(root / (key + ".elf"), b"\x7fELF")
        return {
            "inputs": inputs,
            "propsPath": props,
            "toolPinsJson": pins,
            "toolPaths": {"client": client},
        }, native

    def test_maps_every_mandatory_variable_to_a_real_path(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            record, native = self._fixture(root)
            work = root / "work"

            environment = prepare_test_inputs.compose_environment(record, native, work)

            expected = {variable for variable, _ in prepare_test_inputs._RECORD_INPUTS}
            expected.update(prepare_test_inputs._LINUX_ELF_VARIABLES)
            expected.update({"BUILD_TOOL_PINS_JSON", "EDL_CLIENT_TEST_BINARY",
                             "TEST_ARTIFACT_ROOT", "EDL_CLIENT_TEST_WORK_ROOT",
                             "CORE_TEST_PROPS_PATH"})
            self.assertEqual(expected, set(environment))
            self.assertTrue((work / "artifacts").is_dir())
            self.assertTrue((work / "edl-client").is_dir())
            self.assertEqual(record["propsPath"], environment["CORE_TEST_PROPS_PATH"])
            self.assertEqual(b"\x7fELF", Path(environment["EDL_RAW_LINUX_PACKER_PATH"]).read_bytes()[:4])

    def test_tool_pins_and_client_binary_are_exported_by_literal_names(self):
        # Literal names for the compiled-pins test: BUILD_TOOL_PINS_JSON gates
        # the Fact and EDL_CLIENT_TEST_BINARY is the PE whose hash it compares,
        # so both have to reach the environment even though neither lives in the
        # inputs object the table under test covers.
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            record, native = self._fixture(root)

            environment = prepare_test_inputs.compose_environment(record, native, root / "work")

            self.assertEqual(
                str(Path(record["toolPinsJson"]).resolve()),
                environment["BUILD_TOOL_PINS_JSON"])
            self.assertEqual(
                str(Path(record["toolPaths"]["client"]).resolve()),
                environment["EDL_CLIENT_TEST_BINARY"])
            self.assertTrue(Path(environment["BUILD_TOOL_PINS_JSON"]).is_file())
            self.assertTrue(Path(environment["EDL_CLIENT_TEST_BINARY"]).is_file())

    def test_absent_tool_pins_json_aborts(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            record, native = self._fixture(root)
            del record["toolPinsJson"]

            with self.assertRaises(SystemExit) as raised:
                prepare_test_inputs.compose_environment(record, native, root / "work")
            self.assertIn("BUILD_TOOL_PINS_JSON", str(raised.exception))

    def test_absent_client_tool_path_aborts(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            record, native = self._fixture(root)
            del record["toolPaths"]["client"]

            with self.assertRaises(SystemExit) as raised:
                prepare_test_inputs.compose_environment(record, native, root / "work")
            self.assertIn("EDL_CLIENT_TEST_BINARY", str(raised.exception))

    def test_missing_generated_archive_aborts(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            record, native = self._fixture(root)
            Path(record["inputs"]["BundledEdlG5Archive"]).unlink()

            with self.assertRaises(SystemExit):
                prepare_test_inputs.compose_environment(record, native, root / "work")

    def test_tb710fu_bundle_is_exported_alongside_g4_and_g5(self):
        # Literal variable and record key: the TB710FU toolchain tests read
        # EDL_TOOLCHAIN_TB710FU_TEST_BUNDLE_PATH and skip when it is unset, so
        # an omission here silently removes that suite from the run.
        self.assertIn(
            ("EDL_TOOLCHAIN_TB710FU_TEST_BUNDLE_PATH", "BundledEdlTb710fuArchive"),
            prepare_test_inputs._RECORD_INPUTS)
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            record, native = self._fixture(root)
            bundle = _touch(root / "freedom-tb710fu.zip", b"PK\x03\x04")
            record["inputs"]["BundledEdlTb710fuArchive"] = bundle

            environment = prepare_test_inputs.compose_environment(record, native, root / "work")

            exported = environment["EDL_TOOLCHAIN_TB710FU_TEST_BUNDLE_PATH"]
            self.assertEqual(str(Path(bundle).resolve()), exported)
            self.assertTrue(Path(exported).is_file())

    def test_absent_tb710fu_record_input_aborts(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            record, native = self._fixture(root)
            del record["inputs"]["BundledEdlTb710fuArchive"]

            with self.assertRaises(SystemExit) as raised:
                prepare_test_inputs.compose_environment(record, native, root / "work")
            self.assertIn("EDL_TOOLCHAIN_TB710FU_TEST_BUNDLE_PATH", str(raised.exception))

    def test_missing_tb710fu_generated_file_aborts(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            record, native = self._fixture(root)
            Path(record["inputs"]["BundledEdlTb710fuArchive"]).unlink()

            with self.assertRaises(SystemExit):
                prepare_test_inputs.compose_environment(record, native, root / "work")

    def test_windows_pe_is_rejected_as_the_linux_oracle(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            record, native = self._fixture(root)
            native["EDL_RAW_LINUX_PACKER_PATH"] = _touch(root / "pe.exe", b"MZ\x90\x00")

            with self.assertRaises(SystemExit):
                prepare_test_inputs.compose_environment(record, native, root / "work")

    def test_windows_pe_must_start_with_the_mz_signature(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            record, native = self._fixture(root)
            Path(record["inputs"]["BundledEdlRawPacker"]).write_bytes(b"ZM\x90\x00")

            with self.assertRaises(SystemExit):
                prepare_test_inputs.compose_environment(record, native, root / "work")

    def test_environment_file_values_survive_a_real_shell_source(self):
        environment = {
            "TEST_ARTIFACT_ROOT": "/work/artifacts with spaces",
            "BOOT_TOOL_DLL": "/work/boot tool/armada-boot-tool.dll",
            "QUOTED_PATH": "/work/it's $here; `now`",
        }
        shell = shutil.which("sh")
        if shell is None:
            self.skipTest("a POSIX shell is required to source the environment file")

        with tempfile.TemporaryDirectory() as temporary:
            path = prepare_test_inputs.write_environment_file(
                Path(temporary) / "env.sh", environment)
            script = (
                'set -eu\n'
                '. "$1"\n'
                'printf "%s\\n" "$TEST_ARTIFACT_ROOT" "$BOOT_TOOL_DLL" "$QUOTED_PATH"\n'
            )
            result = subprocess.run(
                [shell, "-c", script, "sh", str(path)],
                check=True,
                capture_output=True,
                text=True)

            self.assertEqual(list(environment.values()), result.stdout.splitlines())


if __name__ == "__main__":
    unittest.main()
