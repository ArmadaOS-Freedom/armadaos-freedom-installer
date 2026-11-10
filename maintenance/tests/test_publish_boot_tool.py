"""Unit tests for publish-boot-tool validation logic (no network, no dotnet)."""
import contextlib
import importlib.util
import io
import os
from pathlib import Path
import struct
import sys
import tempfile
import unittest
from unittest import mock

REPO = Path(__file__).resolve().parent.parent.parent

def load_module():
    spec = importlib.util.spec_from_file_location(
        "publish_boot_tool_under_test", REPO / "tools" / "publish-boot-tool.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module

sys.path.insert(0, str(REPO / "maintenance"))
import fetch_build_inputs  # noqa: E402  same module main() will import


def elf_header(machine, ei_class=2, ei_data=1):
    header = bytearray(20)
    header[0:4] = b"\x7fELF"
    header[4] = ei_class
    header[5] = ei_data
    struct.pack_into("<H", header, 18, machine)
    return bytes(header)


class TestPublishBootTool(unittest.TestCase):
    def setUp(self):
        self.publisher = load_module()
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)

    def write(self, name, data):
        path = self.root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)
        return path

    # 1. ELF validation with real synthetic files
    def test_elf_accepts_matching_architecture(self):
        for rid, machine in (("linux-x64", 62), ("linux-arm64", 183)):
            path = self.write(f"tool-{rid}", elf_header(machine))
            self.publisher.check_elf(path, machine)

    def test_elf_rejects_invalid_headers(self):
        good = elf_header(62)
        cases = {
            "truncated": good[:19],
            "not-elf": b"MZ" + good[2:],
            "32-bit": elf_header(62, ei_class=1),
            "big-endian": elf_header(62, ei_data=2),
        }
        for name, data in cases.items():
            path = self.write(name, data)
            with self.assertRaises(ValueError, msg=name):
                self.publisher.check_elf(path, 62)
        wrong = self.write("arm-binary", elf_header(183))
        with self.assertRaises(ValueError):
            self.publisher.check_elf(wrong, 62)

    # 2. output_files
    def test_output_files_sorted_regular_files(self):
        self.write("out/armada-boot-tool", elf_header(183))
        self.write("out/runtimes/sub/libcoreclr.so", elf_header(183))
        self.write("out/manifest.json", b"{}")
        files = self.publisher.output_files(self.root / "out")
        rels = [p.relative_to(self.root / "out").as_posix() for p in files]
        self.assertEqual(rels, sorted(rels))
        self.assertIn("manifest.json", rels)
        self.assertIn("runtimes/sub/libcoreclr.so", rels)

    def test_output_files_rejects_symlinks(self):
        out = self.root / "out"
        out.mkdir()
        (out / "real").write_bytes(b"x")
        os.symlink(out / "real", out / "link-file")
        with self.assertRaises(ValueError):
            self.publisher.output_files(out)
        out2 = self.root / "out2"
        out2.mkdir()
        target = self.root / "target-dir"
        target.mkdir()
        os.symlink(target, out2 / "link-dir")
        with self.assertRaises(ValueError):
            self.publisher.output_files(out2)

    # 3. main rejects bad outputs before any fetch or publish
    def run_main(self, output):
        sentinel = self.root / "sentinel"
        sentinel.write_bytes(b"untouched")
        argv = ["publish-boot-tool", "--output", str(output), "--inputs", str(self.root / "inputs")]
        stderr = io.StringIO()
        with mock.patch.object(sys, "argv", argv), \
                contextlib.redirect_stderr(stderr), \
                mock.patch.object(fetch_build_inputs, "fetch_all") as fetch_all, \
                mock.patch.object(self.publisher.subprocess, "run") as run:
            code = self.publisher.main()
        self.assertFalse(fetch_all.called)
        self.assertFalse(run.called)
        self.assertEqual(code, 1)
        self.assertIn("error:", stderr.getvalue())
        self.assertEqual(sentinel.read_bytes(), b"untouched")

    def test_main_rejects_nonempty_output(self):
        out = self.root / "nonempty"
        out.mkdir()
        (out / "stale").write_bytes(b"data")
        self.run_main(out)

    def test_main_rejects_symlink_output(self):
        real = self.root / "real-out"
        real.mkdir()
        link = self.root / "link-out"
        os.symlink(real, link)
        self.run_main(link)


if __name__ == "__main__":
    unittest.main()
