#!/usr/bin/env python3
"""Unit tests for the installer-provided ARMADA_BACKUP descriptor migration.

The on-device helper is a standalone script, not an importable package, so it
is loaded from its embedded source path.  Only the pure layout logic and a
file-backed descriptor write are exercised here; locating the real partition
needs a tablet and /proc.
"""

import importlib.machinery
import importlib.util
import json
import os
import struct
import tempfile
import unittest
import uuid
import zlib
from pathlib import Path

_HERE = Path(__file__).resolve().parent
_REPO = _HERE.parent.parent
_SCRIPT = _REPO / "src" / "FreedomInstaller.Core" / "Resources" / "offline-recovery-descriptor-migration.py"

_loader = importlib.machinery.SourceFileLoader("offline_recovery_descriptor_migration", str(_SCRIPT))
_spec = importlib.util.spec_from_loader(_loader.name, _loader)
migrate = importlib.util.module_from_spec(_spec)
_loader.exec_module(migrate)

SIZE = 2 * 1024 * 1024 * 1024
REGION_BYTES = 65536
SLOT = 4096
REGION_OFFSET = SIZE - REGION_BYTES


def make_header(installation, part_uuid, board, compressed=1024, **overrides):
    meta = {
        "kind": "armada-board-recovery-image",
        "schemaVersion": 3,
        "imageBytes": SIZE,
        "headerBytes": REGION_BYTES,
        "payloadOffset": REGION_BYTES,
        "configRegionOffset": REGION_OFFSET,
        "configRegionBytes": REGION_BYTES,
        "configBoardId": board,
        "compressedBytes": compressed,
        "installationId": str(installation),
        "configPartitionUuid": str(part_uuid),
    }
    meta.update(overrides)
    raw = json.dumps(meta, separators=(",", ":")).encode("utf-8")
    return raw + b" " * (REGION_BYTES - len(raw))


def make_descriptor(version, part_uuid, installation, board, compressed=1024):
    d = bytearray(SLOT)
    d[:8] = b"KSCFGV3\0"
    struct.pack_into(
        "<IIQQQII", d, 8,
        version, SLOT, SIZE, REGION_BYTES + compressed, REGION_OFFSET, REGION_BYTES, SLOT,
    )
    d[48:64] = part_uuid.bytes
    d[64:80] = installation.bytes
    struct.pack_into("<II", d, 80, SLOT, 2 * SLOT)
    struct.pack_into("<I", d, 88, board)
    if version == 3:
        struct.pack_into("<III", d, 92, 7 * SLOT, 8 * SLOT, SLOT)
    struct.pack_into("<I", d, 4092, zlib.crc32(d[:4092]) & 0xFFFFFFFF)
    return bytes(d)


def make_region(descriptor):
    return descriptor + b"\x00" * (REGION_BYTES - SLOT)


class DescriptorMigrationTests(unittest.TestCase):
    def setUp(self):
        self.part = uuid.UUID("11111111-2222-3333-4444-555555555555")
        self.installation = uuid.UUID("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")
        self.board = 2
        self.header = make_header(self.installation, self.part, self.board)

    def assert_migrated(self, version):
        region = make_region(make_descriptor(version, self.part, self.installation, self.board))

        migrated, info = migrate.migrate_region(self.header, region, str(self.part), self.board)

        self.assertIsNotNone(migrated)
        self.assertIsNotNone(info)
        self.assertEqual(version, info["fromVersion"])
        self.assertEqual(3, info["toVersion"])
        self.assertEqual(REGION_BYTES, len(migrated))

        d = migrated[:SLOT]
        self.assertEqual(b"KSCFGV3\0", d[:8])
        self.assertEqual(3, struct.unpack_from("<I", d, 8)[0])
        self.assertEqual(7 * SLOT, struct.unpack_from("<I", d, 92)[0])
        self.assertEqual(8 * SLOT, struct.unpack_from("<I", d, 96)[0])
        self.assertEqual(SLOT, struct.unpack_from("<I", d, 100)[0])
        self.assertEqual(zlib.crc32(d[:4092]) & 0xFFFFFFFF, struct.unpack_from("<I", d, 4092)[0])
        self.assertEqual(region[SLOT:], migrated[SLOT:])
        self.assertEqual(region[48:80], d[48:80])
        self.assertEqual(version, struct.unpack_from("<I", region, 8)[0])

    def test_v1_is_upgraded_in_place(self):
        self.assert_migrated(1)

    def test_v2_is_upgraded_in_place(self):
        self.assert_migrated(2)

    def test_v3_is_a_no_op(self):
        region = make_region(make_descriptor(3, self.part, self.installation, self.board))
        migrated, info = migrate.migrate_region(self.header, region, str(self.part), self.board)
        self.assertIsNone(migrated)
        self.assertIsNone(info)

    def test_v1_with_nonzero_v3_reserved_bytes_is_refused(self):
        descriptor = bytearray(make_descriptor(1, self.part, self.installation, self.board))
        descriptor[92] = 1
        struct.pack_into("<I", descriptor, 4092, zlib.crc32(descriptor[:4092]) & 0xFFFFFFFF)
        region = make_region(bytes(descriptor))

        with self.assertRaises(migrate.MigrationError) as caught:
            migrate.migrate_region(self.header, region, str(self.part), self.board)
        self.assertEqual("bad-descriptor", caught.exception.code)

    def test_v1_with_nonzero_trial_slot_is_refused(self):
        region = bytearray(make_region(make_descriptor(1, self.part, self.installation, self.board)))
        region[7 * SLOT] = 1

        with self.assertRaises(migrate.MigrationError) as caught:
            migrate.migrate_region(self.header, bytes(region), str(self.part), self.board)
        self.assertEqual("bad-descriptor", caught.exception.code)

    def test_identity_mismatch_is_refused(self):
        header = make_header(uuid.uuid4(), self.part, self.board)
        region = make_region(make_descriptor(1, self.part, self.installation, self.board))

        with self.assertRaises(migrate.MigrationError) as caught:
            migrate.migrate_region(header, region, str(self.part), self.board)
        self.assertEqual("bad-descriptor", caught.exception.code)

    def test_header_partition_mismatch_is_refused(self):
        header = make_header(self.installation, uuid.uuid4(), self.board)
        region = make_region(make_descriptor(1, self.part, self.installation, self.board))

        with self.assertRaises(migrate.MigrationError) as caught:
            migrate.migrate_region(header, region, str(self.part), self.board)
        self.assertEqual("bad-header", caught.exception.code)

    def test_legacy_header_schema_is_refused(self):
        header = make_header(self.installation, self.part, self.board, schemaVersion=2)
        region = make_region(make_descriptor(1, self.part, self.installation, self.board))

        with self.assertRaises(migrate.MigrationError) as caught:
            migrate.migrate_region(header, region, str(self.part), self.board)
        self.assertEqual("bad-header", caught.exception.code)

    def test_board_mismatch_is_refused(self):
        header = make_header(self.installation, self.part, 3)
        region = make_region(make_descriptor(1, self.part, self.installation, self.board))

        with self.assertRaises(migrate.MigrationError) as caught:
            migrate.migrate_region(header, region, str(self.part), self.board)
        self.assertEqual("bad-header", caught.exception.code)

    @unittest.skipUnless(
        os.name == "posix" and hasattr(os, "pread") and migrate.fcntl is not None,
        "requires pread/pwrite and flock",
    )
    def test_fd_write_is_flushed_and_read_back(self):
        region = make_region(make_descriptor(1, self.part, self.installation, self.board))
        handle, path = tempfile.mkstemp(prefix="armada-backup-")
        os.close(handle)
        try:
            with open(path, "r+b") as image:
                image.truncate(SIZE)
                image.seek(0)
                image.write(self.header)
                image.seek(REGION_OFFSET)
                image.write(region)
                image.flush()
                os.fsync(image.fileno())

            fd = os.open(path, os.O_RDWR)
            try:
                info = migrate.migrate_fd(fd, self.header, region, str(self.part), self.board)
            finally:
                os.close(fd)

            self.assertEqual(1, info["fromVersion"])
            with open(path, "rb") as image:
                image.seek(REGION_OFFSET)
                after = image.read(REGION_BYTES)
            self.assertEqual(3, struct.unpack_from("<I", after, 8)[0])
            self.assertEqual(region[SLOT:], after[SLOT:])
        finally:
            os.unlink(path)

    @unittest.skipUnless(
        os.name == "posix" and hasattr(os, "pread") and migrate.fcntl is not None,
        "requires pread/pwrite and flock",
    )
    def test_fd_write_tolerates_a_concurrent_settings_slot_write(self):
        region = make_region(make_descriptor(1, self.part, self.installation, self.board))
        handle, path = tempfile.mkstemp(prefix="armada-backup-")
        os.close(handle)
        try:
            with open(path, "r+b") as image:
                image.truncate(SIZE)
                image.seek(0)
                image.write(self.header)
                image.seek(REGION_OFFSET)
                image.write(region)
                image.flush()
                os.fsync(image.fileno())

            # The launcher wrote its first settings slot between the caller's
            # read and this migration. That slot is mutable and must not abort
            # the descriptor repair or be overwritten from the stale copy.
            stale = bytearray(region)
            stale[SLOT] = 0x7F

            fd = os.open(path, os.O_RDWR)
            try:
                info = migrate.migrate_fd(fd, self.header, bytes(stale), str(self.part), self.board)
            finally:
                os.close(fd)

            self.assertEqual(1, info["fromVersion"])
            with open(path, "rb") as image:
                image.seek(REGION_OFFSET)
                after = image.read(REGION_BYTES)
            self.assertEqual(3, struct.unpack_from("<I", after, 8)[0])
            self.assertEqual(0, after[SLOT])
        finally:
            os.unlink(path)


if __name__ == "__main__":
    unittest.main()
