#!/usr/bin/env python3
"""Behavioral tests for fetch_build_inputs.py with mocked network and real hashes."""

from __future__ import annotations

import hashlib
import importlib.util
import io
import json
import os
import tempfile
import unittest
import urllib.error
from unittest import mock

_HERE = os.path.dirname(os.path.abspath(__file__))
_MAINT = os.path.dirname(_HERE)
_spec = importlib.util.spec_from_file_location(
    "fetch_build_inputs", os.path.join(_MAINT, "fetch_build_inputs.py")
)
fetch = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(fetch)


def _sha(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


FIXTURES = {
    "platformTools": (b"android-platform-tools-bytes", "platform-tools-37.0.1-windows.zip"),
    "python": (b"python-embed-bytes", "python-3.14.8-embed-amd64.zip"),
    "libusbPackage": (b"libusb-nupkg-bytes", "LibUsbDotNet.3.0.167-alpha.nupkg"),
    "libusbLicense": (b"libusb-Copying-text", "libusb-COPYING.txt"),
    "gblPrc": (b"gbl-prc-efi-bytes", "gbl-6.2.192-mod2-prc.efi"),
    "gblRow": (b"gbl-row-efi-bytes", "gbl-6.2.192-mod2-row.efi"),
}

OFFICIAL_HOSTS = {
    "platformTools": "https://dl.google.com",
    "python": "https://www.python.org",
    "libusbPackage": "https://api.nuget.org",
    "libusbLicense": "https://raw.githubusercontent.com",
    "gblPrc": "https://github.com",
    "gblRow": "https://github.com",
}


def _payload_for(download_id: str) -> bytes:
    return FIXTURES[download_id][0]


def _download_entry(download_id: str, *, url_override: str | None = None, extra: dict | None = None) -> dict:
    data = _payload_for(download_id)
    entry = {
        "id": download_id,
        "name": FIXTURES[download_id][1],
        "url": url_override or f"{OFFICIAL_HOSTS[download_id]}/fixtures/{FIXTURES[download_id][1]}",
        "bytes": len(data),
        "sha256": _sha(data),
        "version": "test",
    }
    if extra:
        entry.update(extra)
    return entry


def _write_manifest(tmp_root: str, downloads: list[dict], **extra) -> str:
    payload = {
        "schemaVersion": 2,
        "target": "win-x64",
        "downloads": downloads,
        "sources": [{"id": "edl-ng", "upstream": "https://github.com/strongtz/edl-ng"}],
        "vendorFiles": [],
    }
    payload.update(extra)
    path = os.path.join(tmp_root, "build-inputs.json")
    with open(path, "w", encoding="utf-8") as handle:
        json.dump(payload, handle)
    return path


class FakeURL:
    def __init__(self, data: bytes) -> None:
        self._buf = io.BytesIO(data)

    def read(self, n: int = -1) -> bytes:
        return self._buf.read(n)

    def __enter__(self) -> "FakeURL":
        return self

    def __exit__(self, *exc) -> None:
        return None


class FetchTests(unittest.TestCase):
    def setUp(self) -> None:
        self.tmp = tempfile.TemporaryDirectory()
        self.root = self.tmp.name
        self.out = os.path.join(self.root, "out")
        self.downloads = [_download_entry(did) for did in FIXTURES]
        self.manifest_path = _write_manifest(self.root, self.downloads)

    def tearDown(self) -> None:
        self.tmp.cleanup()

    def _mock_urlopen(self, *, tamper: str | None = None, truncate: int = 0, fail_on_id: str | None = None):
        by_url = {entry["url"]: _payload_for(entry["id"]) for entry in self.downloads}

        def fake_urlopen(request, timeout=None):
            url = request.full_url if hasattr(request, "full_url") else request
            for entry in self.downloads:
                if entry["url"] == url and fail_on_id and entry["id"] == fail_on_id:
                    raise urllib.error.URLError("boom")
            data = by_url[url]
            if tamper and self._id_for_url(url) == tamper:
                data = b"Y" + data[1:] if data else b"Y"
            if truncate:
                data = data[: max(0, len(data) - truncate)]
            return FakeURL(data)

        return fake_urlopen

    def _id_for_url(self, url: str) -> str | None:
        for entry in self.downloads:
            if entry["url"] == url:
                return entry["id"]
        return None

    def test_manifest_has_six_official_sources_no_own_release(self) -> None:
        real_path = os.path.join(_MAINT, "build-inputs.json")
        with open(real_path, "r", encoding="utf-8") as handle:
            raw = handle.read()
        self.assertNotIn("armadaos-freedom-installer/releases", raw)
        manifest = fetch.load_manifest(real_path)
        ids = [entry["id"] for entry in manifest["downloads"]]
        self.assertEqual(sorted(ids), sorted(fetch.REQUIRED_IDS))
        for entry in manifest["downloads"]:
            self.assertTrue(entry["url"].startswith("https://"))
        self.assertIn("sources", manifest)
        self.assertIn("vendorFiles", manifest)

    def test_load_manifest_preserves_extra_fields(self) -> None:
        extra = {"version": "test", "source": "https://example.com"}
        downloads = [_download_entry(did, extra=extra) for did in FIXTURES]
        path = _write_manifest(self.root, downloads)
        manifest = fetch.load_manifest(path)
        for entry in manifest["downloads"]:
            self.assertEqual(entry["version"], "test")
            self.assertEqual(entry["source"], "https://example.com")

    def test_fetch_all_downloads_then_caches(self) -> None:
        manifest = fetch.load_manifest(self.manifest_path)
        calls: list[str] = []

        def counting(urlopen):
            def inner(req, timeout=None):
                calls.append(req.full_url if hasattr(req, "full_url") else req)
                return urlopen(req, timeout=timeout)
            return inner

        with mock.patch.object(fetch.urllib.request, "urlopen", counting(self._mock_urlopen())):
            first = fetch.fetch_all(manifest, self.out)
        self.assertEqual(set(first), set(FIXTURES))
        for did, path in first.items():
            self.assertTrue(os.path.isabs(path))
            self.assertTrue(os.path.isfile(path))
        with open(os.path.join(self.out, FIXTURES["platformTools"][1]), "rb") as handle:
            self.assertEqual(handle.read(), _payload_for("platformTools"))
        first_calls = len(calls)
        self.assertEqual(first_calls, 6)

        # Second pass: everything cached, no network.
        with mock.patch.object(fetch.urllib.request, "urlopen", side_effect=AssertionError("no network")):
            second = fetch.fetch_all(manifest, self.out)
        self.assertEqual(first, second)

    def test_corrupt_cached_with_failed_replacement_preserves_original(self) -> None:
        manifest = fetch.load_manifest(self.manifest_path)
        with mock.patch.object(fetch.urllib.request, "urlopen", self._mock_urlopen()):
            fetch.fetch_all(manifest, self.out)
        target_id = "gblPrc"
        target = os.path.join(self.out, FIXTURES[target_id][1])
        with open(target, "wb") as handle:
            handle.write(b"corrupt-cached")
        with open(target, "rb") as handle:
            before = handle.read()
        with mock.patch.object(
            fetch.urllib.request, "urlopen", self._mock_urlopen(fail_on_id=target_id)
        ):
            with self.assertRaises(fetch.FetchError):
                fetch.fetch_all(manifest, self.out)
        with open(target, "rb") as handle:
            self.assertEqual(handle.read(), before)
        # No residual .part files.
        for name in os.listdir(self.out):
            self.assertFalse(name.endswith(".part"), name)

    def test_wrong_sha_refused_no_partial(self) -> None:
        manifest = fetch.load_manifest(self.manifest_path)
        with mock.patch.object(fetch.urllib.request, "urlopen", self._mock_urlopen(tamper="gblRow")):
            with self.assertRaises(fetch.FetchError) as ctx:
                fetch.fetch_all(manifest, self.out)
        self.assertIn("sha256", str(ctx.exception))
        self.assertFalse(os.path.exists(os.path.join(self.out, FIXTURES["gblRow"][1])))
        for name in os.listdir(self.out):
            self.assertFalse(name.endswith(".part"), name)

    def test_wrong_size_refused_no_partial(self) -> None:
        manifest = fetch.load_manifest(self.manifest_path)
        with mock.patch.object(fetch.urllib.request, "urlopen", self._mock_urlopen(truncate=1)):
            with self.assertRaises(fetch.FetchError) as ctx:
                fetch.fetch_all(manifest, self.out)
        self.assertIn("size", str(ctx.exception))
        for name in os.listdir(self.out):
            self.assertFalse(name.endswith(".part"), name)

    def test_verify_only_missing_dir_no_write(self) -> None:
        manifest = fetch.load_manifest(self.manifest_path)
        with self.assertRaises(fetch.FetchError):
            fetch.fetch_all(manifest, self.out, verify_only=True)
        self.assertFalse(os.path.exists(self.out))

    def test_verify_only_does_not_network(self) -> None:
        manifest = fetch.load_manifest(self.manifest_path)
        with mock.patch.object(fetch.urllib.request, "urlopen", self._mock_urlopen()):
            fetch.fetch_all(manifest, self.out)
        with mock.patch.object(
            fetch.urllib.request, "urlopen", side_effect=AssertionError("no network")
        ):
            result = fetch.fetch_all(manifest, self.out, verify_only=True)
        self.assertEqual(len(result), 6)

    def test_verify_only_rejects_corrupt(self) -> None:
        manifest = fetch.load_manifest(self.manifest_path)
        with mock.patch.object(fetch.urllib.request, "urlopen", self._mock_urlopen()):
            fetch.fetch_all(manifest, self.out)
        target = os.path.join(self.out, FIXTURES["libusbLicense"][1])
        with open(target, "wb") as handle:
            handle.write(b"corrupt")
        with mock.patch.object(
            fetch.urllib.request, "urlopen", side_effect=AssertionError("no network")
        ):
            with self.assertRaises(fetch.FetchError):
                fetch.fetch_all(manifest, self.out, verify_only=True)

    def _bad_manifest_rejected_without_side_effects(self, mutate) -> None:
        downloads = [_download_entry(did) for did in FIXTURES]
        mutate(downloads)
        path = _write_manifest(self.root, downloads)
        output = os.path.join(self.root, "never-created")
        with mock.patch.object(
            fetch.urllib.request, "urlopen", side_effect=AssertionError("no network")
        ):
            with self.assertRaises(fetch.FetchError):
                fetch.load_manifest(path)
        self.assertFalse(os.path.exists(output))

    def test_reject_duplicate_id(self) -> None:
        def mutate(downloads):
            downloads[1]["id"] = downloads[0]["id"]
        self._bad_manifest_rejected_without_side_effects(mutate)

    def test_reject_duplicate_name(self) -> None:
        def mutate(downloads):
            downloads[1]["name"] = downloads[0]["name"]
        self._bad_manifest_rejected_without_side_effects(mutate)

    def test_reject_path_in_name(self) -> None:
        def mutate(downloads):
            downloads[0]["name"] = "../evil.zip"
        self._bad_manifest_rejected_without_side_effects(mutate)

    def test_reject_non_https(self) -> None:
        def mutate(downloads):
            downloads[0]["url"] = "http://dl.google.com/x"
        self._bad_manifest_rejected_without_side_effects(mutate)

    def test_reject_bool_bytes(self) -> None:
        def mutate(downloads):
            downloads[0]["bytes"] = True
        self._bad_manifest_rejected_without_side_effects(mutate)

    def test_reject_bad_sha(self) -> None:
        def mutate(downloads):
            downloads[0]["sha256"] = "ABCDEF"
        self._bad_manifest_rejected_without_side_effects(mutate)

    def test_reject_missing_required_id(self) -> None:
        downloads = [_download_entry(did) for did in FIXTURES if did != "gblRow"]
        path = _write_manifest(self.root, downloads)
        with mock.patch.object(
            fetch.urllib.request, "urlopen", side_effect=AssertionError("no network")
        ):
            with self.assertRaises(fetch.FetchError):
                fetch.load_manifest(path)

    def test_reject_bad_schema(self) -> None:
        path = os.path.join(self.root, "bad.json")
        with open(path, "w", encoding="utf-8") as handle:
            json.dump({"schemaVersion": 1, "downloads": []}, handle)
        with self.assertRaises(fetch.FetchError):
            fetch.load_manifest(path)

    def test_unicode_and_space_names(self) -> None:
        # Unicode/space names are intentionally rejected to keep flat safe filenames.
        downloads = [_download_entry(did) for did in FIXTURES]
        downloads[0]["name"] = "my tools.zip"
        path = _write_manifest(self.root, downloads)
        with self.assertRaises(fetch.FetchError):
            fetch.load_manifest(path)

    def test_official_urls_are_not_own_release(self) -> None:
        manifest = fetch.load_manifest(self.manifest_path)
        for entry in manifest["downloads"]:
            self.assertNotIn("armadaos-freedom-installer/releases", entry["url"])

    def test_hash_file_roundtrip(self) -> None:
        path = os.path.join(self.root, "x.bin")
        with open(path, "wb") as handle:
            handle.write(b"abc")
        size, digest = fetch.hash_file(path)
        self.assertEqual(size, 3)
        self.assertEqual(digest, _sha(b"abc"))


if __name__ == "__main__":
    unittest.main()
