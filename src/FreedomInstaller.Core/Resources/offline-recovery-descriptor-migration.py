#!/usr/bin/python3
# SPDX-License-Identifier: GPL-3.0-only
"""Upgrade a legacy v1/v2 ARMADA_BACKUP settings descriptor to the v3 layout.

The installer runs this file as root, with ``python3 -I``, before the
on-device offline updater starts.  It is deliberately standalone: it must not
import ``updates`` or any other file from the installed runtime, because the
runtime it is meant to repair may be the very thing that refuses a v1
descriptor.

The migration rewrites exactly one 4096-byte descriptor slot.  The archive
header, the payload, the settings slots and the reserved tail are preserved;
the immutable header, descriptor and reserved tail are verified byte for byte,
while the explicitly mutable settings and trial slots are allowed to change
under a running launcher.  A v3 descriptor is a no-op, so running it on every
connection is safe.
"""

from __future__ import annotations

import json
import os
import stat
import struct
import subprocess
import sys
import uuid
import zlib
from pathlib import Path

try:
    import fcntl
except ImportError:  # Windows test hosts import this module for its pure functions.
    fcntl = None

SCHEMA_VERSION = 1
COMMAND = 'migrate'

REGION_BYTES = 65536
SLOT_BYTES = 4096
SIZE = 2 * 1024 * 1024 * 1024
REGION_OFFSET = SIZE - REGION_BYTES

DESCRIPTOR_VERSION = 3
LEGACY_VERSIONS = (1, 2)
V3_SLOT_A = 7 * SLOT_BYTES      # 28672
V3_SLOT_B = 8 * SLOT_BYTES      # 32768
V3_SLOT_C = 9 * SLOT_BYTES      # 36864

MAGIC = b'KSCFGV3\0'
MAX_MESSAGE_CHARS = 400

# (device-tree compatible alias, accepted SoC tokens, native board id).  This
# mirrors updates.models.RECOVERY_PROFILES so the helper resolves the board the
# same way the updater does; the table is frozen with the shipped layouts.
PROFILES = (
    (b'lenovo,elden', (b'qcom,sm8750',), 1),
    (b'lenovo,baldur', (b'qcom,sm8850', b'qcom,kaanapali'), 2),
    (b'lenovo,tb323fu', (b'qcom,sm8850', b'qcom,kaanapali'), 2),
    (b'lenovo,tb710fu', (b'qcom,sm8650',), 3),
)

MODEL_BOARDS = {'TB322FC': 1, 'TB323FU': 2, 'TB710FU': 3}


class MigrationError(RuntimeError):
    def __init__(self, code, message):
        super().__init__(message)
        self.code = code


def _u32(data, offset):
    return struct.unpack_from('<I', data, offset)[0]


def _u64(data, offset):
    return struct.unpack_from('<Q', data, offset)[0]


def _reject_constant(value):
    raise ValueError('non-finite JSON number: %s' % value)


def _no_duplicate_keys(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError('duplicate JSON key: %r' % key)
        result[key] = value
    return result


def strict_loads(data):
    return json.loads(
        data.decode('utf-8'),
        object_pairs_hook=_no_duplicate_keys,
        parse_constant=_reject_constant,
    )


def parse_header(header, board):
    if len(header) != REGION_BYTES:
        raise MigrationError('bad-header', 'archive header is not 65536 bytes')
    try:
        meta = strict_loads(header)
    except (UnicodeDecodeError, ValueError) as error:
        raise MigrationError('bad-header', 'archive header is not valid JSON: %s' % error)

    try:
        if meta['kind'] != 'armada-board-recovery-image':
            raise MigrationError('bad-header', 'archive header kind is not a board recovery image')
        if meta['schemaVersion'] != 3:
            raise MigrationError('bad-header', 'archive header schemaVersion is not 3')
        if meta['imageBytes'] != SIZE or meta['headerBytes'] != REGION_BYTES:
            raise MigrationError('bad-header', 'archive header geometry does not match the 2 GiB backup')
        if meta['payloadOffset'] != REGION_BYTES:
            raise MigrationError('bad-header', 'archive header payloadOffset is not 65536')
        if meta['configRegionOffset'] != REGION_OFFSET or meta['configRegionBytes'] != REGION_BYTES:
            raise MigrationError('bad-header', 'archive header settings region does not match the 2 GiB backup')
        if meta['configBoardId'] != board:
            raise MigrationError('bad-header', 'archive header board id does not match the device tree')
        compressed = meta['compressedBytes']
        if not isinstance(compressed, int) or isinstance(compressed, bool):
            raise MigrationError('bad-header', 'archive header compressedBytes is not an integer')
        if not 0 < compressed <= REGION_OFFSET - REGION_BYTES:
            raise MigrationError('bad-header', 'archive header payload bounds are invalid')
        return meta, compressed
    except KeyError as error:
        raise MigrationError('bad-header', 'archive header is missing %s' % error)
    except TypeError as error:
        raise MigrationError('bad-header', 'archive header has a non-numeric value: %s' % error)


def parse_descriptor(header, region, partition_uuid, board):
    meta, compressed = parse_header(header, board)
    if len(region) != REGION_BYTES:
        raise MigrationError('bad-descriptor', 'settings region is not 65536 bytes')

    try:
        part = uuid.UUID(partition_uuid).bytes
        installation = uuid.UUID(meta['installationId']).bytes
        header_partition = uuid.UUID(meta['configPartitionUuid']).bytes
    except (AttributeError, TypeError, ValueError) as error:
        raise MigrationError('bad-header', 'archive header identity is invalid: %s' % error)
    if header_partition != part:
        raise MigrationError('bad-header', 'archive header names a different backup partition')

    d = region[:SLOT_BYTES]
    version = _u32(d, 8)
    if d[:8] != MAGIC or version not in (1, 2, 3) or _u32(d, 12) != SLOT_BYTES:
        raise MigrationError('bad-descriptor', 'settings descriptor magic, version or length is invalid')
    if zlib.crc32(d[:4092]) & 0xFFFFFFFF != _u32(d, 4092):
        raise MigrationError('bad-descriptor', 'settings descriptor CRC does not match')

    if version < 3:
        extra_ok = not any(d[92:4092])
    else:
        extra_ok = (
            _u32(d, 92) == V3_SLOT_A
            and _u32(d, 96) == V3_SLOT_B
            and _u32(d, 100) == SLOT_BYTES
            and not any(d[104:4092])
        )
    tail = reserved_from(version)
    if not (
        _u64(d, 16) == SIZE
        and _u64(d, 24) == REGION_BYTES + compressed
        and _u64(d, 32) == REGION_OFFSET
        and _u32(d, 40) == REGION_BYTES
        and _u32(d, 44) == SLOT_BYTES
        and _u32(d, 80) == SLOT_BYTES
        and _u32(d, 84) == 2 * SLOT_BYTES
        and _u32(d, 88) == board
        and d[48:64] == part
        and d[64:80] == installation
        and any(part)
        and any(installation)
        and extra_ok
        and not any(region[tail:])
    ):
        raise MigrationError('bad-descriptor', 'settings descriptor identity or layout mismatch')

    return {
        'version': version,
        'partition_uuid': part,
        'installation_uuid': installation,
        'board': board,
    }


def migrate_region(header, region, partition_uuid, board):
    """Return ``(new_region, info)`` or ``(None, None)`` when already v3."""
    layout = parse_descriptor(header, region, partition_uuid, board)
    if layout['version'] == DESCRIPTOR_VERSION:
        return None, None
    if layout['version'] not in LEGACY_VERSIONS:
        raise MigrationError(
            'unsupported-version',
            'unsupported settings descriptor version: %d' % layout['version'],
        )

    old = region[:SLOT_BYTES]
    descriptor = bytearray(old)
    struct.pack_into('<I', descriptor, 8, DESCRIPTOR_VERSION)
    struct.pack_into('<III', descriptor, 92, V3_SLOT_A, V3_SLOT_B, SLOT_BYTES)
    descriptor[104:4092] = b'\x00' * (4092 - 104)
    struct.pack_into(
        '<I', descriptor, 4092,
        zlib.crc32(bytes(descriptor[:4092])) & 0xFFFFFFFF,
    )

    if descriptor[:8] != old[:8] or descriptor[16:92] != old[16:92]:
        raise MigrationError('identity-changed', 'settings descriptor identity changed during migration')

    expected = bytes(descriptor) + region[SLOT_BYTES:]
    info = {
        'fromVersion': layout['version'],
        'toVersion': DESCRIPTOR_VERSION,
        'descriptorSha256': _sha256(bytes(descriptor)),
        'regionSha256Before': _sha256(region),
        'regionSha256After': _sha256(expected),
        'recordSlotOffsets': [V3_SLOT_A, V3_SLOT_B],
    }
    return expected, info


def reserved_from(version):
    return (3 * SLOT_BYTES, 7 * SLOT_BYTES, 9 * SLOT_BYTES)[version - 1]


def _sha256(data):
    import hashlib
    return hashlib.sha256(data).hexdigest()


def migrate_fd(fd, header, region, partition_uuid, board):
    if fcntl is None:
        raise MigrationError('unsupported-host', 'file locking is unavailable on this host')
    fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
    try:
        current_header = os.pread(fd, REGION_BYTES, 0)
        current_region = os.pread(fd, REGION_BYTES, REGION_OFFSET)
        current = parse_descriptor(current_header, current_region, partition_uuid, board)
        # Settings, diagnostic and trial slots are explicitly mutable while the
        # launcher is running. Only the descriptor, the archive header and the
        # zero-filled reserved tail must be stable across the transition.
        stable_from = reserved_from(current['version'])
        if current_header != header or current_region[:SLOT_BYTES] != region[:SLOT_BYTES]:
            raise MigrationError('changed', 'recovery region changed before migration')
        if current_region[stable_from:] != region[stable_from:]:
            raise MigrationError('changed', 'recovery region changed before migration')

        expected, info = migrate_region(header, current_region, partition_uuid, board)
        if expected is None:
            return None

        written = os.pwrite(fd, expected[:SLOT_BYTES], REGION_OFFSET)
        if written != SLOT_BYTES:
            raise MigrationError('short-write', 'settings descriptor short write: %d' % written)
        try:
            os.fsync(fd)
        except OSError as error:
            raise MigrationError('fsync-failed', 'settings descriptor fsync failed: %s' % error)

        if os.pread(fd, REGION_BYTES, 0) != current_header:
            raise MigrationError('changed', 'archive header changed during descriptor migration')
        readback = os.pread(fd, REGION_BYTES, REGION_OFFSET)
        if (readback[:SLOT_BYTES] != expected[:SLOT_BYTES] or
                readback[stable_from:] != expected[stable_from:]):
            raise MigrationError('readback-failed', 'settings descriptor readback differs after migration')
        return info
    finally:
        fcntl.flock(fd, fcntl.LOCK_UN)


def _locate_board():
    dt = Path('/proc/device-tree')
    try:
        compatible = (dt / 'compatible').read_bytes().split(b'\0')
    except OSError as error:
        raise MigrationError('no-devicetree', 'cannot read the device tree: %s' % error)

    matches = {
        board for alias, socs, board in PROFILES
        if alias in compatible and any(soc in compatible for soc in socs)
    }
    if len(matches) != 1:
        raise MigrationError('unsupported-board', 'device tree does not name exactly one supported board')
    board = next(iter(matches))

    report_path = dt / 'chosen' / 'kshim,settings-v1'
    try:
        report = report_path.read_bytes()
    except OSError as error:
        raise MigrationError('missing-settings-report', 'cannot read kshim,settings-v1: %s' % error)
    if len(report) != 64:
        raise MigrationError('missing-settings-report', 'native settings report is not 64 bytes')
    v = struct.unpack('>16I', report)
    if not (v[0] == 1 and v[1] == board and v[2] == 0 and v[3] == 0 and v[4] == 0
            and v[5] == 1 and v[12] == 0 and v[13] == 1):
        raise MigrationError(
            'settings-not-verified',
            'the launcher has not verified usable internal settings',
        )
    return board


def _locate_partition(board):
    try:
        result = subprocess.run(
            ['blkid', '-t', 'PARTLABEL=ARMADA_BACKUP', '-o', 'device'],
            capture_output=True, text=True, check=False,
        )
    except OSError as error:
        raise MigrationError('no-partition', 'cannot run blkid: %s' % error)
    if result.returncode != 0:
        raise MigrationError('no-partition', 'no ARMADA_BACKUP partition was reported')

    candidates = []
    for name in result.stdout.splitlines():
        if not name.strip():
            continue
        dev = Path(name.strip()).resolve()
        try:
            info = dev.stat()
        except OSError as error:
            raise MigrationError('no-partition', 'cannot stat %s: %s' % (dev, error))
        if not stat.S_ISBLK(info.st_mode):
            raise MigrationError('no-partition', 'ARMADA_BACKUP is not a block device')
        node = Path('/sys/dev/block/%d:%d' % (os.major(info.st_rdev), os.minor(info.st_rdev))).resolve()
        if not any(part.endswith('.ufs') for part in node.parts):
            continue
        try:
            size = int((node / 'size').read_text()) * 512
            block = int((node.parent / 'queue' / 'logical_block_size').read_text())
            part_uuid = subprocess.check_output(
                ['blkid', '-s', 'PARTUUID', '-o', 'value', str(dev)], text=True,
            ).strip()
        except (OSError, ValueError, subprocess.CalledProcessError) as error:
            raise MigrationError('no-partition', 'cannot inspect %s: %s' % (dev, error))
        if not (node / 'partition').is_file() or size != SIZE:
            raise MigrationError('no-partition', 'internal backup partition is not exactly 2 GiB')
        if block not in (512, 4096):
            raise MigrationError('no-partition', 'unsupported logical block size: %d' % block)
        try:
            uuid.UUID(part_uuid)
        except ValueError as error:
            raise MigrationError('no-partition', 'invalid partition UUID: %s' % error)
        candidates.append((dev, part_uuid, board, info.st_rdev))

    if len(candidates) != 1:
        raise MigrationError('no-partition', 'expected exactly one internal UFS ARMADA_BACKUP partition')
    return candidates[0]


def run(model):
    board = _locate_board()
    expected = MODEL_BOARDS.get(model)
    if expected is None:
        raise MigrationError('unsupported-model', 'unsupported model: %s' % model)
    if expected != board:
        raise MigrationError('model-mismatch', 'reported model does not match the device tree board')

    dev, partition_uuid, board, expected_rdev = _locate_partition(board)
    flags = os.O_RDWR | os.O_CLOEXEC | os.O_NOFOLLOW
    fd = os.open(str(dev), flags)
    try:
        info = os.fstat(fd)
        if not stat.S_ISBLK(info.st_mode) or info.st_rdev != expected_rdev:
            raise MigrationError('no-partition', 'ARMADA_BACKUP changed after it was located')
        header = os.pread(fd, REGION_BYTES, 0)
        region = os.pread(fd, REGION_BYTES, REGION_OFFSET)
        if len(header) != REGION_BYTES or len(region) != REGION_BYTES:
            raise MigrationError('short-read', 'settings region read was short')
        info_out = migrate_fd(fd, header, region, partition_uuid, board)
    finally:
        os.close(fd)

    if info_out is None:
        return {'state': 'already-v3', 'descriptorVersion': DESCRIPTOR_VERSION}
    return {'state': 'migrated', **info_out}


def _emit(payload):
    sys.stdout.write(json.dumps(payload, separators=(',', ':')) + '\n')
    sys.stdout.flush()


def main(argv=None):
    argv = list(sys.argv[1:] if argv is None else argv)
    model = None
    if '--model' in argv:
        index = argv.index('--model')
        if index + 1 < len(argv):
            model = argv[index + 1]
    try:
        if argv[:1] != [COMMAND]:
            raise MigrationError('usage', 'usage: %s migrate --model MODEL' % sys.argv[0])
        if not model:
            raise MigrationError('usage', 'migrate requires --model')
        result = run(model)
        _emit({
            'schemaVersion': SCHEMA_VERSION,
            'ok': True,
            'command': COMMAND,
            **result,
        })
        return 0
    except MigrationError as error:
        _emit({
            'schemaVersion': SCHEMA_VERSION,
            'ok': False,
            'command': COMMAND,
            'error': error.code,
            'message': str(error)[:MAX_MESSAGE_CHARS],
        })
        return 1
    except Exception as error:  # noqa: BLE001 - the installer needs a parseable failure
        _emit({
            'schemaVersion': SCHEMA_VERSION,
            'ok': False,
            'command': COMMAND,
            'error': 'unexpected',
            'message': ('%s: %s' % (type(error).__name__, error))[:MAX_MESSAGE_CHARS],
        })
        return 1


if __name__ == '__main__':
    raise SystemExit(main())
