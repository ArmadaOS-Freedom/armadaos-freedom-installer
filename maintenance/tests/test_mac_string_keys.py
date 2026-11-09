"""Every string key the macOS window uses must exist in the catalogs it loads.

The Mac window reads the WPF window's Static.* / Dynamic.* catalogs with Mac.*.json laid over them. When
upstream removes or renames a key, the Mac window would show a blank line or the raw key; this test fails
instead, naming the key.
"""

import json
import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
APP_RESOURCES = ROOT / "src" / "FreedomInstaller.App" / "Resources"
MAC = ROOT / "src" / "FreedomInstaller.Mac"

# Complete keys only: a literal ending in "." is a prefix the code completes at run time.
_KEY = re.compile(r'"((?:Static|Dynamic)\.[A-Za-z0-9_.]*[A-Za-z0-9_])"')
_RESOURCE = re.compile(r"\{DynamicResource ((?:Static|Dynamic)\.[A-Za-z0-9_.]+)\}")


def _catalog(language):
    keys = set()
    for path in (APP_RESOURCES / f"Static.{language}.json",
                 APP_RESOURCES / f"Dynamic.{language}.json",
                 MAC / "Resources" / f"Mac.{language}.json"):
        keys.update(json.loads(path.read_text(encoding="utf-8")))
    return keys


def _used_keys():
    used = {}
    for path in sorted(MAC.glob("*.axaml")):
        for key in _RESOURCE.findall(path.read_text(encoding="utf-8")):
            used.setdefault(key, path.name)
    for path in sorted(MAC.glob("*.cs")):
        for key in _KEY.findall(path.read_text(encoding="utf-8")):
            used.setdefault(key, path.name)
    return used


class MacStringKeyTests(unittest.TestCase):
    def test_every_used_key_exists(self):
        used = _used_keys()
        self.assertGreater(len(used), 100)
        for language in ("zh-CN", "en"):
            catalog = _catalog(language)
            missing = sorted(f"{key} ({where})" for key, where in used.items() if key not in catalog)
            self.assertEqual(missing, [], f"missing in {language}")


if __name__ == "__main__":
    unittest.main()
