import hashlib
import json
import sys
import tempfile
import unittest
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "maintenance"))
import build_installer


class AndroidDriverPackagingTests(unittest.TestCase):
    def test_all_shipped_driver_files_are_pinned(self):
        manifest = json.loads((ROOT / "maintenance/build-inputs.json").read_text())
        pins = {item["path"]: item for item in manifest["vendorFiles"]}
        files = list((ROOT / "vendor/android").rglob("*"))
        files = [p for p in files if p.is_file() and p.suffix.lower() in {".inf", ".cat", ".dll"}]
        self.assertEqual(8, len(files))
        for path in files:
            rel = path.relative_to(ROOT).as_posix()
            self.assertIn(rel, pins, rel)
            data = path.read_bytes()
            self.assertEqual(pins[rel]["bytes"], len(data), rel)
            self.assertEqual(pins[rel]["sha256"].lower(), hashlib.sha256(data).hexdigest(), rel)

    def test_zip_preserves_signed_bytes_and_published_property(self):
        with tempfile.TemporaryDirectory() as temp:
            archive = Path(temp) / "android-driver.zip"
            build_installer._build_android_driver_zip(ROOT, archive)
            with zipfile.ZipFile(archive) as z:
                self.assertEqual(8, len(z.namelist()))
                for name in z.namelist():
                    self.assertEqual((ROOT / "vendor/android" / name).read_bytes(), z.read(name))
        self.assertIn("BundledAndroidDriverArchive", build_installer._PROPERTY_NAMES)

    def test_missing_driver_input_stops_packaging(self):
        with tempfile.TemporaryDirectory() as temp:
            with self.assertRaises(SystemExit):
                build_installer._build_android_driver_zip(Path(temp), Path(temp) / "out.zip")
