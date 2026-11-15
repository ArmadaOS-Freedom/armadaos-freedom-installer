#!/usr/bin/env python3
"""Focused checks for maintenance/prepare_test_inputs.py.

Only the pure input-mapping and validation helpers are exercised; the real
prepare()/CMake run is a CI step, not a unit test.  Nothing here restates the
build record schema beyond the public key names the mapper consumes.
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
        native = {}
        for variable in prepare_test_inputs._LINUX_ELF_VARIABLES:
            key = "unpacker" if "UNPACKER" in variable else "packer"
            native[variable] = _touch(root / (key + ".elf"), b"\x7fELF")
        return {"inputs": inputs, "propsPath": props}, native

    def test_maps_every_mandatory_variable_to_a_real_path(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            record, native = self._fixture(root)
            work = root / "work"

            environment = prepare_test_inputs.compose_environment(record, native, work)

            expected = {variable for variable, _ in prepare_test_inputs._RECORD_INPUTS}
            expected.update(prepare_test_inputs._LINUX_ELF_VARIABLES)
            expected.update({"TEST_ARTIFACT_ROOT", "EDL_CLIENT_TEST_WORK_ROOT",
                             "CORE_TEST_PROPS_PATH"})
            self.assertEqual(expected, set(environment))
            self.assertTrue((work / "artifacts").is_dir())
            self.assertTrue((work / "edl-client").is_dir())
            self.assertEqual(record["propsPath"], environment["CORE_TEST_PROPS_PATH"])
            self.assertEqual(b"\x7fELF", Path(environment["EDL_RAW_LINUX_PACKER_PATH"]).read_bytes()[:4])

    def test_missing_generated_archive_aborts(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            record, native = self._fixture(root)
            Path(record["inputs"]["BundledEdlG5Archive"]).unlink()

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
