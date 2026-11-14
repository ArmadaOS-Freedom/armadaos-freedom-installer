#!/usr/bin/env python3
"""Runs the public maintenance unit tests.

Every maintenance/tests/test_*.py module is discovered automatically, so a new
ordinary test needs no registration. The bootstrap lifecycle harness is the one
deliberate exclusion: it executes real BusyBox shell fixtures inside a
disposable container and needs runtime prerequisites the generic checkout job
does not provide. It is reported here and remains available for explicit
invocation with those prerequisites; it is never faked or skipped in place.
"""

import sys
import unittest
from pathlib import Path

TESTS_DIR = Path(__file__).resolve().parent / "tests"
EXCLUDED = {"test_bootstrap_lifecycle.py"}


def main():
    sys.path.insert(0, str(TESTS_DIR))
    loader = unittest.TestLoader()
    suite = unittest.TestSuite()
    for path in sorted(TESTS_DIR.glob("test_*.py")):
        if path.name in EXCLUDED:
            print(f"EXCLUDED {path.name}: privileged container harness; needs "
                  "TF_BOOTSTRAP_CONTAINER_TEST=1, a container environment, "
                  "C#-exported fixtures, a real BusyBox, a work dir beneath "
                  "/evidence and CAP_SYS_ADMIN for the mount cases", file=sys.stderr)
            continue
        suite.addTests(loader.loadTestsFromModule(__import__(path.stem)))
    return 0 if unittest.TextTestRunner(verbosity=2).run(suite).wasSuccessful() else 1


if __name__ == "__main__":
    sys.exit(main())
