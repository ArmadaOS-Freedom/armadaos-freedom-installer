"""The macOS window's offline upgrade page must match the WPF window's.

The WPF page is its own UserControl (OfflineUpgradeView.xaml) whose handlers are wired in code
(InitializeOfflineUpgradeView); the Mac page is a section of MainWindow.axaml whose controls carry an "Offline"
prefix (one name scope with the rest of the window) and whose handlers are wired in the markup. This test
compares what each page shows and does: the static string keys, the named controls, the event wiring (control,
event, handler), the Dynamic/Static OfflineUpdate messages the code can show, the shared state helpers and Core
calls the code makes, and the models the connection accepts. Workflow behavior stays matched while approved
platform-only layout (WPF-only containers, the common sidebar back control vs. the Mac per-page back) may differ;
every other shared feature, action, confirmation, string and wiring remains strict.
"""

import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
WPF_XAML = ROOT / "src" / "FreedomInstaller.App" / "OfflineUpgradeView.xaml"
WPF_CODE = [
    ROOT / "src" / "FreedomInstaller.App" / "MainWindow.OfflineUpgrade.cs",
    ROOT / "src" / "FreedomInstaller.App" / "MainWindow.OfflinePairing.cs",
]
MAC_XAML = ROOT / "src" / "FreedomInstaller.Mac" / "MainWindow.axaml"
MAC_CODE = [
    ROOT / "src" / "FreedomInstaller.Mac" / "MainWindow.OfflineUpgrade.cs",
    ROOT / "src" / "FreedomInstaller.Mac" / "MainWindow.OfflinePairing.cs",
]
MAC_PREFIX = "Offline"
MAC_PAGE = "OfflineUpgradePage"

# Workflow behavior is matched while approved platform-only layout may differ.
# - The WPF window resolves the records location from a work directory chosen on its environment page and refuses
#   with ErrorWorkDirNotReady until that check has passed. The Mac window has no work directory: its records live
#   under the Mac cache root, which always exists, so this refusal cannot occur there.
# - PR #118 moved the Windows back control into the common sidebar and added WPF-only layout containers; the Mac
#   page keeps its own per-page back button (Static.OfflineUpdate.Back, OnOfflineUpgradeBackAsync).
_WPF_ONLY_MESSAGES = {"Dynamic.OfflineUpdate.ErrorWorkDirNotReady"}
_WPF_ONLY_NAMES = {"OfflineWorkflowScrollViewer", "OfflineConnectionStep", "OfflinePackageStep", "OfflineConfirmationStep"}
_WPF_ONLY_KEYS = set()
_MAC_ONLY_NAMES = {"BackButton"}
_MAC_ONLY_KEYS = {"Static.OfflineUpdate.Back"}
_MAC_ONLY_WIRING = {("BackButton", "Click", "OnOfflineUpgradeBackAsync")}
# - WPF wires Checked and Unchecked to one handler; Avalonia has one IsCheckedChanged event for both.
_EVENT_MAP = {"Checked": "IsCheckedChanged", "Unchecked": "IsCheckedChanged"}


def _mac_page():
    text = MAC_XAML.read_text(encoding="utf-8")
    start = text.index('x:Name="%s"' % MAC_PAGE)
    end = text.index("<!-- =================", start)
    return text[start:end]


def _keys(page, prefix):
    return {key for key in re.findall(r"\{DynamicResource ([A-Za-z0-9_.]+)\}", page) if key.startswith(prefix)}


def _wpf_names(page):
    # The PasswordBox template in UserControl.Resources names its own parts (Chrome, PART_ContentHost); the Mac
    # page masks a TextBox instead and needs no template, so only the page body is compared.
    body = page[page.index("</UserControl.Resources>"):]
    return set(re.findall(r'x:Name="([A-Za-z0-9_]+)"', body))


def _mac_names(page):
    names = set(re.findall(r'x:Name="([A-Za-z0-9_]+)"', page)) - {MAC_PAGE}
    unprefixed = {name for name in names if not name.startswith(MAC_PREFIX)}
    assert not unprefixed, unprefixed
    return {name[len(MAC_PREFIX):] for name in names}


def _wpf_wiring():
    text = "\n".join(path.read_text(encoding="utf-8") for path in WPF_CODE)
    wiring = set()
    for control, event, handler in re.findall(r"OfflineUpgradePanel\.(\w+)\.(\w+) \+= (\w+);", text):
        wiring.add((control, _EVENT_MAP.get(event, event), handler))
    return wiring


def _mac_wiring(page):
    wiring = set()
    for element in re.findall(r"<[A-Za-z]+\s[^>]*>", page, re.S):
        name = re.search(r'x:Name="%s([A-Za-z0-9_]+)"' % MAC_PREFIX, element)
        if name is None:
            continue
        for event, handler in re.findall(r'\b([A-Za-z]+)="(On[A-Za-z0-9_]+)"', element):
            wiring.add((name.group(1), event, handler))
    return wiring


def _code(paths):
    return "\n".join(path.read_text(encoding="utf-8") for path in paths)


def _messages(paths):
    return set(re.findall(r'"((?:Dynamic|Static)\.OfflineUpdate\.[A-Za-z0-9_.]+)"', _code(paths)))


def _shared_calls(paths):
    """Calls into Core and into the shared state helpers: the decisions the page delegates."""
    text = _code(paths)
    calls = set(re.findall(r"(?<![.\w])((?:OfflineUpdate\w*|OfflineBootstrapAssets|PackageImporter)\.\w+)", text))
    calls |= {"transfer." + name for name in re.findall(r"_offlineTransfer!?\.(\w+)", text)}
    calls |= {"transfer." + name for name in re.findall(r"\btransfer\.(\w+)", text)}
    return calls


class MacOfflineUpgradeParityTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.wpf = WPF_XAML.read_text(encoding="utf-8")
        cls.mac = _mac_page()

    def test_static_keys_match(self):
        wpf = _keys(self.wpf, "Static.")
        mac = _keys(self.mac, "Static.")
        self.assertEqual(wpf - mac, _WPF_ONLY_KEYS)
        self.assertEqual(mac - wpf, _MAC_ONLY_KEYS)

    def test_initial_dynamic_texts_match(self):
        self.assertEqual(_keys(self.wpf, "Dynamic."), _keys(self.mac, "Dynamic."))

    def test_named_controls_match(self):
        wpf = _wpf_names(self.wpf)
        mac = _mac_names(self.mac)
        self.assertEqual(wpf - mac, _WPF_ONLY_NAMES)
        self.assertEqual(mac - wpf, _MAC_ONLY_NAMES)

    def test_event_wiring_matches(self):
        wpf = _wpf_wiring()
        mac = _mac_wiring(self.mac)
        self.assertGreater(len(wpf), 15)
        self.assertIn(("BackButton", "Click", "OnOfflineUpgradeBackAsync"), mac)
        self.assertEqual(wpf, mac - _MAC_ONLY_WIRING)

    def test_shared_sidebar_back_path_is_wired(self):
        code = (ROOT / "src" / "FreedomInstaller.App" / "MainWindow.Functions.cs").read_text(encoding="utf-8")
        self.assertRegex(code, r'if \(_showingOfflineUpgrade\)\s*\{\s*await LeaveOfflineUpgradeAsync\(\);')
        partial = (ROOT / "src" / "FreedomInstaller.App" / "MainWindow.OfflineUpgrade.cs").read_text(encoding="utf-8")
        pattern = r"private async Task LeaveOfflineUpgradeAsync\(\)\s*\{(.*?)\n        \}"
        match = re.search(pattern, partial, re.MULTILINE | re.DOTALL)
        self.assertIsNotNone(match)
        self.assertIn("await DisconnectAndCleanupStageAsync();", match.group(1))

    def test_code_shows_the_same_messages(self):
        wpf = _messages(WPF_CODE) - _WPF_ONLY_MESSAGES
        mac = _messages(MAC_CODE)
        self.assertEqual(wpf, mac)

    def test_code_makes_the_same_core_and_state_calls(self):
        self.assertEqual(_shared_calls(WPF_CODE), _shared_calls(MAC_CODE))

    def test_accepted_models_match(self):
        # Both pages gate the connected model on Core's single offline-upgrade list, never on their own names.
        gate = "UpdatePackageDescriptor.SupportsOfflineUpgrade(model)"
        for paths in (WPF_CODE, MAC_CODE):
            code = _code(paths)
            self.assertIn(gate, code)
            self.assertNotRegex(code, r'string\.Equals\(model, "TB[0-9]{3}[A-Z]{2}"')

    def test_updater_version_floor_matches(self):
        pattern = r'IsUpdaterVersionAtLeast\(deviceInfo, "([0-9.]+)"\)'
        self.assertEqual(re.findall(pattern, _code(WPF_CODE)), re.findall(pattern, _code(MAC_CODE)))

    def test_stop_waiting_button_in_shared_progress_card(self):
        from xml.etree import ElementTree

        root = ElementTree.fromstring(self.wpf)
        ns = "{http://schemas.microsoft.com/winfx/2006/xaml}"

        matches = [e for e in root.iter() if e.get(ns + "Name") == "StopWaitingButton"]
        self.assertEqual(1, len(matches))
        stop = matches[0]

        parent_map = {child: parent for parent in root.iter() for child in parent}
        state_text = next(e for e in root.iter() if e.get(ns + "Name") == "StateText")
        state_parent = parent_map[state_text]
        self.assertIs(state_parent, parent_map[stop])

        step_names = {"OfflinePackageStep", "OfflineConnectionStep", "OfflineConfirmationStep"}
        ancestor = parent_map[stop]
        while ancestor is not None:
            self.assertNotIn(ancestor.get(ns + "Name"), step_names)
            ancestor = parent_map.get(ancestor)

        wires = _wpf_wiring()
        self.assertIn(("StopWaitingButton", "Click", "OnStopWaitingOfflineUpgrade"), wires)

    def test_home_card_opens_the_page(self):
        text = MAC_XAML.read_text(encoding="utf-8")
        card = re.search(
            r'<Button Tag="offline-upgrade" Click="OnSelectOperation"[^>]*>.*?Static\.Function\.OfflineUpdate\.Title.*?</Button>',
            text,
            re.S,
        )
        self.assertIsNotNone(card)
        self.assertNotIn("Static.Mac.WindowsOnly", card.group(0))
        dispatch = (ROOT / "src" / "FreedomInstaller.Mac" / "MainWindow.axaml.cs").read_text(encoding="utf-8")
        self.assertRegex(dispatch, r'case "offline-upgrade"[^:]*:\s*ShowOfflineUpgradePage\(\);')

    def test_close_handler_releases_the_page(self):
        # The WPF window registers OnOfflineUpgradeClosing; the Mac window's single close handler calls the port.
        closing = (ROOT / "src" / "FreedomInstaller.Mac" / "MainWindow.axaml.cs").read_text(encoding="utf-8")
        self.assertIn("await OnOfflineUpgradeClosingAsync(e)", closing)


if __name__ == "__main__":
    unittest.main()
