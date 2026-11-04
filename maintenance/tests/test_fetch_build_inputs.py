"""Tests for maintenance/fetch_build_inputs.py using real temporary files."""

from __future__ import annotations

import contextlib
import hashlib
import io
import json
import os
import sys
import tempfile
import unittest
from unittest import mock

_HERE = os.path.dirname(os.path.abspath(__file__))
_MAINT = os.path.dirname(_HERE)
if _MAINT not in sys.path:
    sys.path.insert(0, _MAINT)

import fetch_build_inputs as fbi  # noqa: E402


PROPS = list(fbi.EXPECTED_PROPERTIES)


def _sha(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def _manifest(entries):
    return {"schemaVersion": 1, "release": fbi.RELEASE, "files": entries, "sources": []}


def _entry(name, prop, data):
    return {
        "name": name,
        "property": prop,
        "bytes": len(data),
        "sha256": _sha(data),
        "url": f"https://example.invalid/{fbi.RELEASE}/{name}",
    }


def _write_manifest(directory, entries):
    path = os.path.join(directory, "build-inputs.json")
    with open(path, "w", encoding="utf-8") as fh:
        json.dump(_manifest(entries), fh)
    return path


class _FakeResp:
    def __init__(self, data: bytes):
        self._buf = io.BytesIO(data)

    def read(self, n=-1):
        return self._buf.read(n)

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        return False


class FetchTests(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.dir = self._tmp.name
        self.payloads = {name: (f"payload-{name}".encode() + os.urandom(64)) for name in PROPS}
        self.entries = [_entry(f"file-{i}.bin", prop, self.payloads[prop]) for i, prop in enumerate(PROPS)]
        self.manifest = _write_manifest(self.dir, self.entries)

    def tearDown(self):
        self._tmp.cleanup()

    def _serve(self, entries):
        by_url = {e["url"]: self.payloads[e["property"]] for e in entries}
        calls = {"n": 0}

        def fake_urlopen(req, timeout=None):
            calls["n"] += 1
            url = req.full_url if hasattr(req, "full_url") else req
            if url not in by_url:
                raise AssertionError(f"unexpected url {url}")
            return _FakeResp(by_url[url])

        return fake_urlopen, calls

    def _run(self, extra=()):
        argv = ["--output-dir", self.dir, "--manifest", self.manifest, *extra]
        return fbi.main(argv)

    def test_successful_download_and_props(self):
        fake, _ = self._serve(self.entries)
        with mock.patch.object(fbi.urllib.request, "urlopen", fake):
            rc = self._run()
        self.assertEqual(rc, 0)
        for entry in self.entries:
            with open(os.path.join(self.dir, entry["name"]), "rb") as fh:
                self.assertEqual(_sha(fh.read()), entry["sha256"])
        props = os.path.join(self.dir, "build-inputs.props")
        self.assertTrue(os.path.isfile(props))
        import xml.etree.ElementTree as ET
        tree = ET.parse(props)
        values = {el.tag.split("}")[-1]: el.text for el in tree.getroot().iter() if el.text}
        for entry in self.entries:
            self.assertEqual(values[entry["property"]], os.path.join(self.dir, entry["name"]))

    def test_cache_bypasses_network(self):
        fake, calls = self._serve(self.entries)
        with mock.patch.object(fbi.urllib.request, "urlopen", fake):
            self.assertEqual(self._run(), 0)
        first = calls["n"]
        def explode(req, timeout=None):
            raise AssertionError("network must not be used when cached")
        with mock.patch.object(fbi.urllib.request, "urlopen", explode):
            self.assertEqual(self._run(), 0)
        self.assertEqual(calls["n"], first)

    def test_verify_only_leaves_output_untouched(self):
        fake, _ = self._serve(self.entries)
        with mock.patch.object(fbi.urllib.request, "urlopen", fake):
            self.assertEqual(self._run(), 0)
        props = os.path.join(self.dir, "build-inputs.props")
        os.unlink(props)
        rc = self._run(["--verify-only"])
        self.assertEqual(rc, 0)
        self.assertFalse(os.path.exists(props))
        victim = os.path.join(self.dir, self.entries[0]["name"])
        with open(victim, "wb") as fh:
            fh.write(b"corrupt")
        rc = self._run(["--verify-only"])
        self.assertNotEqual(rc, 0)
        self.assertFalse(os.path.exists(props))

    def test_corrupt_cache_redownloads(self):
        fake, _ = self._serve(self.entries)
        with mock.patch.object(fbi.urllib.request, "urlopen", fake):
            self.assertEqual(self._run(), 0)
        victim = os.path.join(self.dir, self.entries[2]["name"])
        with open(victim, "wb") as fh:
            fh.write(b"garbage")
        fake, calls = self._serve(self.entries)
        with mock.patch.object(fbi.urllib.request, "urlopen", fake):
            self.assertEqual(self._run(), 0)
        self.assertEqual(calls["n"], 1)
        with open(victim, "rb") as fh:
            self.assertEqual(_sha(fh.read()), self.entries[2]["sha256"])

    def test_sha_mismatch_aborts_and_no_part(self):
        bad = dict(self.entries[0])
        bad["sha256"] = "0" * 64
        self.entries[0] = bad
        self.manifest = _write_manifest(self.dir, self.entries)
        fake, _ = self._serve(self.entries)
        with mock.patch.object(fbi.urllib.request, "urlopen", fake):
            rc = self._run()
        self.assertNotEqual(rc, 0)
        leftovers = [n for n in os.listdir(self.dir) if n.endswith(".part")]
        self.assertEqual(leftovers, [])
        self.assertFalse(os.path.exists(os.path.join(self.dir, "build-inputs.props")))

    def test_size_mismatch_aborts(self):
        bad = dict(self.entries[0])
        bad["bytes"] = bad["bytes"] + 100
        self.entries[0] = bad
        self.manifest = _write_manifest(self.dir, self.entries)
        fake, _ = self._serve(self.entries)
        with mock.patch.object(fbi.urllib.request, "urlopen", fake):
            rc = self._run()
        self.assertNotEqual(rc, 0)
        self.assertEqual([n for n in os.listdir(self.dir) if n.endswith(".part")], [])

    def test_invalid_manifests_rejected_before_write(self):
        cases = []
        # duplicate property
        dup = list(self.entries)
        dup[1] = dict(dup[0])
        cases.append(dup)
        # path traversal name
        trav = list(self.entries)
        trav[0] = dict(trav[0], name="../evil.zip")
        cases.append(trav)
        # malformed sha
        bad_sha = list(self.entries)
        bad_sha[0] = dict(bad_sha[0], sha256="nope")
        cases.append(bad_sha)
        # non-https url
        bad_url = list(self.entries)
        bad_url[0] = dict(bad_url[0], url="http://example.invalid/x")
        cases.append(bad_url)
        # missing property
        cases.append(self.entries[:-1])
        for entries in cases:
            with self.subTest(entries=entries[:1]):
                self.manifest = _write_manifest(self.dir, entries)
                def explode(req, timeout=None):
                    raise AssertionError("must not download")
                with mock.patch.object(fbi.urllib.request, "urlopen", explode):
                    rc = self._run()
                self.assertNotEqual(rc, 0)
                for entry in entries:
                    name = entry.get("name", "")
                    if name and "/" not in name and ".." not in name:
                        self.assertFalse(os.path.exists(os.path.join(self.dir, name)))
                self.assertFalse(os.path.exists(os.path.join(self.dir, "build-inputs.props")))

    def test_spaces_and_non_ascii_output(self):
        sub = os.path.join(self.dir, "a b 中文")
        os.makedirs(sub)
        self.dir = sub
        self.manifest = _write_manifest(self.dir, self.entries)
        fake, _ = self._serve(self.entries)
        with mock.patch.object(fbi.urllib.request, "urlopen", fake):
            rc = self._run()
        self.assertEqual(rc, 0)
        import xml.etree.ElementTree as ET
        tree = ET.parse(os.path.join(self.dir, "build-inputs.props"))
        values = [el.text for el in tree.getroot().iter() if el.text]
        self.assertTrue(any("a b 中文" in v for v in values))

    def test_verify_only_missing_dir_fails(self):
        argv = ["--output-dir", os.path.join(self.dir, "missing"), "--manifest", self.manifest, "--verify-only"]
        self.assertNotEqual(fbi.main(argv), 0)

    def test_normal_missing_output_dir_created(self):
        out = os.path.join(self.dir, "newout", "nested")
        fake, _ = self._serve(self.entries)
        argv = ["--output-dir", out, "--manifest", self.manifest]
        with mock.patch.object(fbi.urllib.request, "urlopen", fake):
            rc = fbi.main(argv)
        self.assertEqual(rc, 0)
        self.assertTrue(os.path.isdir(out))
        self.assertTrue(os.path.isfile(os.path.join(out, "build-inputs.props")))

    def test_invalid_manifest_missing_output_dir_not_created(self):
        out = os.path.join(self.dir, "shouldnotexist")
        dup = list(self.entries)
        dup[1] = dict(dup[0])
        bad_manifest = _write_manifest(self.dir, dup)
        def explode(req, timeout=None):
            raise AssertionError("must not download")
        argv = ["--output-dir", out, "--manifest", bad_manifest]
        with mock.patch.object(fbi.urllib.request, "urlopen", explode):
            rc = fbi.main(argv)
        self.assertNotEqual(rc, 0)
        self.assertFalse(os.path.exists(out))


if __name__ == "__main__":
    unittest.main()
