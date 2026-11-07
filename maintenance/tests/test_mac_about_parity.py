"""The macOS window's About page must match the WPF window's.

The two windows cannot share markup (WPF and Avalonia XAML differ), but they already share every string and
image. This test compares what each page shows and does: the string keys, the images, the click handlers, the
crop of the sponsor QR codes, the link targets and the QQ group number. When the WPF page changes, it fails
until the Mac page follows.
"""

import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
WPF_XAML = ROOT / "src" / "FreedomInstaller.App" / "MainWindow.xaml"
WPF_CODE = ROOT / "src" / "FreedomInstaller.App" / "MainWindow.Support.cs"
MAC_XAML = ROOT / "src" / "FreedomInstaller.Mac" / "MainWindow.axaml"
MAC_CODE = ROOT / "src" / "FreedomInstaller.Mac" / "MainWindow.About.cs"

# The page's own back button is not part of the page content.
_IGNORED_HANDLERS = {"OnCloseSupport"}


def _support_page(path):
    text = path.read_text(encoding="utf-8")
    start = text.index('x:Name="SupportPage"')
    end = text.index("</ScrollViewer>", start)
    return text[start:end]


def _keys(page):
    return set(re.findall(r"\{DynamicResource ([A-Za-z0-9_.]+)\}", page))


def _images(page):
    return set(re.findall(r"Assets/([A-Za-z0-9_.-]+\.(?:png|jpg))", page))


def _handlers(page):
    return set(re.findall(r'Click="([A-Za-z0-9_]+)"', page)) - _IGNORED_HANDLERS


def _crops(page):
    return re.findall(r'SourceRect="([0-9, ]+)"', page)


def _handler_urls(code):
    """Maps each handler in a code-behind file to the URL it opens."""
    return dict(re.findall(r'void (On[A-Za-z0-9]+)\([^)]*\)\s*=>[^;]*?"(https://[^"]+)"', code))


def _qq(code):
    return re.search(r'AboutQqGroupNumber = "([0-9]+)"', code).group(1)


class MacAboutParityTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.wpf = _support_page(WPF_XAML)
        cls.mac = _support_page(MAC_XAML)
        cls.wpf_code = WPF_CODE.read_text(encoding="utf-8")
        cls.mac_code = MAC_CODE.read_text(encoding="utf-8")

    def test_same_strings(self):
        self.assertEqual(_keys(self.mac), _keys(self.wpf))

    def test_same_images(self):
        self.assertEqual(_images(self.mac), _images(self.wpf))

    def test_same_actions(self):
        self.assertEqual(_handlers(self.mac), _handlers(self.wpf))

    def test_same_qr_crops(self):
        self.assertEqual(_crops(self.mac), _crops(self.wpf))

    def test_same_link_targets(self):
        wpf_urls = _handler_urls(self.wpf_code)
        self.assertTrue(wpf_urls, "no About links found in the WPF code-behind")
        self.assertEqual(_handler_urls(self.mac_code), wpf_urls)

    def test_same_author_links(self):
        # The author card reuses the footer's handlers; the WPF window keeps their targets in constants.
        wpf_main = (ROOT / "src" / "FreedomInstaller.App" / "MainWindow.xaml.cs").read_text(encoding="utf-8")
        mac_main = (ROOT / "src" / "FreedomInstaller.Mac" / "MainWindow.axaml.cs").read_text(encoding="utf-8")
        for name in ("BilibiliUrl", "GithubUrl"):
            url = re.search(name + r' = "(https://[^"]+)"', wpf_main).group(1)
            self.assertIn(f'"{url}"', mac_main, name)

    def test_same_qq_group(self):
        self.assertEqual(_qq(self.mac_code), _qq(self.wpf_code))

    def test_every_image_is_packaged(self):
        assets = ROOT / "src" / "FreedomInstaller.App" / "Assets"
        for name in _images(self.mac):
            self.assertTrue((assets / name).is_file(), name)


if __name__ == "__main__":
    unittest.main()
