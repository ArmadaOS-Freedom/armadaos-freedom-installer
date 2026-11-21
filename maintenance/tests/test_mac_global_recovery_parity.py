"""The macOS window's 9008 global backup / restore page must match the WPF window's.

The two windows cannot share markup (WPF and Avalonia XAML differ), but they share every string. This test
compares what each page shows and does: the named controls the code drives, the static string keys, the click
handlers, and the Dynamic.Global.* messages the code-behind can show. When the WPF page or its code changes,
it fails until the Mac page follows.
"""

import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
WPF_XAML = ROOT / "src" / "FreedomInstaller.App" / "MainWindow.xaml"
WPF_CODE = [
    ROOT / "src" / "FreedomInstaller.App" / "MainWindow.GlobalRecovery.cs",
    ROOT / "src" / "FreedomInstaller.App" / "MainWindow.GlobalRecovery.Execution.cs",
]
MAC_XAML = ROOT / "src" / "FreedomInstaller.Mac" / "MainWindow.axaml"
MAC_CODE = [
    ROOT / "src" / "FreedomInstaller.Mac" / "MainWindow.GlobalRecovery.cs",
    # The close warning lives in the Mac window's single close handler.
    ROOT / "src" / "FreedomInstaller.Mac" / "MainWindow.axaml.cs",
]

# Intentional differences, each with its reason:
# - the WPF page shows a collapsible connection drawing (a WPF-only UserControl); the Mac page shows the same
#   lead text without the drawing, so neither the drawing nor its expander title appear there;
# - the WPF log expander and output section carry names the code never uses.
_WPF_ONLY_KEYS = {"Static.Global.ConnectionGuideTitle"}
_WPF_ONLY_NAMES = set()
# The WPF window may run without an adb transport; the Mac page is only reachable after the environment check
# found adb, so its transport always exists and this refusal cannot occur there.
_WPF_ONLY_MESSAGES = {"Dynamic.Global.Error.TransportUnavailable"}


def _page(path):
    text = path.read_text(encoding="utf-8")
    start = text.index('x:Name="GlobalRecoveryPage"')
    end = text.index("</ScrollViewer>\n", text.index('x:Name="GlobalLogScrollViewer"', start))
    end = text.index("</ScrollViewer>", end + 1)
    return text[start:end]


def _keys(page, prefix):
    return {key for key in re.findall(r"\{DynamicResource ([A-Za-z0-9_.]+)\}", page) if key.startswith(prefix)}


def _names(page):
    return set(re.findall(r'x:Name="(Global[A-Za-z0-9_]+)"', page))


# WPF wires a check box's Checked and Unchecked to one handler; Avalonia has one IsCheckedChanged event for both.
_EVENTS = {"Click": "Click", "Checked": "CheckedChanged", "Unchecked": "CheckedChanged",
           "IsCheckedChanged": "CheckedChanged"}


def _handlers(page):
    """(control name, normalized event, handler) for every element of the page that wires an event."""
    bindings = set()
    for tag in re.findall(r"<[A-Za-z][\w:.]*\b[^>]*>", page):
        name = re.search(r'x:Name="([A-Za-z0-9_]+)"', tag)
        for event, handler in re.findall(r'(?<![A-Za-z])(Click|Checked|Unchecked|IsCheckedChanged)="([A-Za-z0-9_]+)"', tag):
            bindings.add((name.group(1) if name else None, _EVENTS[event], handler))
    return bindings


def _code_messages(paths):
    text = "\n".join(path.read_text(encoding="utf-8") for path in paths)
    return set(re.findall(r'"(Dynamic\.Global\.[A-Za-z0-9_.]+)"', text))


class MacGlobalRecoveryParityTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.wpf = _page(WPF_XAML)
        cls.mac = _page(MAC_XAML)

    def test_static_keys_match(self):
        self.assertEqual(_keys(self.wpf, "Static.") - _WPF_ONLY_KEYS, _keys(self.mac, "Static."))

    def test_named_controls_match(self):
        self.assertEqual(_names(self.wpf) - _WPF_ONLY_NAMES, _names(self.mac))

    def test_handlers_match(self):
        # Compared per control and event, so two controls that swap handlers are caught.
        wpf = _handlers(self.wpf)
        self.assertIn(("GlobalBackupButton", "Click", "OnGlobalBackupAction"), wpf)
        self.assertIn(("GlobalRestoreConfirmCheck", "CheckedChanged", "OnGlobalRestoreConsentChanged"), wpf)
        self.assertEqual(wpf, _handlers(self.mac))

    def test_initial_dynamic_texts_match(self):
        self.assertEqual(_keys(self.wpf, "Dynamic."), _keys(self.mac, "Dynamic."))

    def test_code_shows_the_same_global_messages(self):
        # Messages built from a prefix ("Dynamic.Global.Stage." + stage) are matched by their prefix.
        wpf = _code_messages(WPF_CODE) - _WPF_ONLY_MESSAGES
        mac = _code_messages(MAC_CODE)
        self.assertLessEqual(wpf, mac)
        self.assertEqual(set(), {key for key in mac - wpf if not key.startswith("Dynamic.Global.Dialog.")})

    def test_home_cards_open_the_page(self):
        text = MAC_XAML.read_text(encoding="utf-8")
        for tag, title in (("global-backup", "GlobalBackup"), ("global-restore", "GlobalRestore")):
            card = re.search(
                r'<Button Tag="%s" Click="OnSelectOperation"[^>]*>.*?Static\.Function\.%s\.Title.*?</Button>'
                % (tag, title),
                text,
                re.S,
            )
            self.assertIsNotNone(card, tag)
            self.assertNotIn("Static.Mac.WindowsOnly", card.group(0))


if __name__ == "__main__":
    unittest.main()
