"""The internal-install capacity default is one Core function shared by the WPF and macOS windows.

A reinstall over the installed Linux layout must propose the whole userdata, so leaving the slider alone is the
overlay install that keeps the Android data (issue #172: the macOS window used to propose half and planned a
repartition that erased Android). Both windows take the proposal from InternalPartitionPlanner.DefaultAndroidGiB
and neither computes its own.
"""

import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
WPF = ROOT / "src/FreedomInstaller.App/MainWindow.InternalInstall.Capacity.cs"
MAC = ROOT / "src/FreedomInstaller.Mac/MainWindow.Install.cs"
CORE = ROOT / "src/FreedomInstaller.Core/InternalPartitionPlanner.cs"
CALL = "InternalPartitionPlanner.DefaultAndroidGiB("


class MacInstallCapacityParityTests(unittest.TestCase):
    def test_both_windows_take_the_core_default(self):
        for path in (WPF, MAC):
            with self.subTest(window=path.name):
                text = path.read_text(encoding="utf-8")
                self.assertEqual(1, text.count(CALL))
                # No window derives its own proposal from the userdata size.
                self.assertIsNone(re.search(r"userdataBytes\s*/\s*Gibibyte", text))
                self.assertNotIn("InternalInstallDefaultAndroidGiB", text)

    def test_core_default_keeps_a_reinstall_whole(self):
        text = CORE.read_text(encoding="utf-8")
        self.assertIn("public static long DefaultAndroidGiB(GptTable original, AndroidAllocationRange range)", text)
        self.assertRegex(text, r"reinstall\s*\?\s*wholeGiB\s*:\s*wholeGiB\s*/\s*2")


if __name__ == "__main__":
    unittest.main()
