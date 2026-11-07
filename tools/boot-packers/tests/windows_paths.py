#!/usr/bin/env python3
"""Exercise real native tools with synthetic payloads and Windows path spellings."""
import argparse
import os
from pathlib import Path
import shutil
import struct
import subprocess
import sys
import uuid


def extended(path):
    value = str(path)
    if value.startswith('\\\\?\\'):
        return value
    return '\\\\?\\UNC\\' + value[2:] if value.startswith('\\\\') else '\\\\?\\' + value


def fs(path):
    return Path(extended(path)) if os.name == 'nt' else Path(path)


def run(tool, args, cwd, expect_success=True):
    result = subprocess.run([str(tool), *map(str, args)], cwd=cwd,
                            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=60)
    text = result.stdout.decode('utf-8', 'replace')
    if expect_success and result.returncode != 0:
        raise AssertionError(f'exit {result.returncode}: {text}')
    return result, text


def exercise(name, root, packer, unpacker):
    src = root / name / 'source'
    if name == 'long-config':
        while len(str(src)) < 340:
            src /= 'deep-' + 'x' * 45
    if name == 'boundary-relative':
        src /= 'x' * max(1, 248 - len(str(src)) - 1)
    if name == 'unicode':
        src /= '中文 路径'
    fs(src).mkdir(parents=True)
    fs(src / 'sub').mkdir()
    image_name = '内核 image.bin' if name == 'unicode' else 'BaseImage'
    config_name = '配置 config.cfg' if name == 'unicode' else 'build.cfg'
    header = bytearray(4096)
    struct.pack_into('<II', header, 0, 0x14000010, 0xD503201F)
    struct.pack_into('<Q', header, 16, 4096)
    struct.pack_into('<I', header, 56, 0x644D5241)
    fs(src / image_name).write_bytes(header)
    fs(src / 'runtime.bin').write_bytes(bytes(4 * 1024 * 1024))
    # Minimal valid flattened device tree with one empty root node.
    dtb = struct.pack('>10I', 0xD00DFEED, 72, 56, 72, 40, 17, 16, 0, 0, 16)
    dtb += bytes(16) + struct.pack('>4I', 1, 0, 2, 9)
    fs(src / 'dtb-normal.dtb').write_bytes(dtb)
    if name == 'missing-input':
        fs(src / 'dtb-normal.dtb').unlink()

    def spelling(filename):
        if name in ('absolute-io', 'extended'):
            return extended(src / filename) if name == 'extended' else str(src / filename)
        if name == 'normalized':
            return 'sub/../' + filename
        return filename

    cfg = src / config_name
    fs(cfg).write_text('\n'.join([
        '[Pack]', 'Shim=' + spelling('runtime.bin'), 'Output=' + spelling('PackedKernel'),
        'Default=Image-00', 'Timeout=0', '', '[Image-00]', 'Name=Base',
        'Path=' + spelling(image_name), 'Type=Linux', 'BaseImage=true',
        'DeviceTreeBlob=Image-DTB', '', '[Image-DTB]', 'Name=DeviceTree',
        'Path=' + spelling('dtb-normal.dtb'), 'Type=DTB', '', '[Manifest]',
        'Type=Manifest', '',
    ]), encoding='utf-8')
    cwd, arg = src, config_name
    if name in ('absolute-cfg', 'unicode', 'extended', 'long-config'):
        cwd, arg = root, extended(cfg) if name == 'extended' else str(cfg)
    if name == 'normalized':
        arg = 'sub/../' + config_name
    result, text = run(packer, [arg], cwd, name != 'missing-input')
    if name == 'missing-input':
        assert result.returncode != 0 and 'errno=' in text and 'dtb-normal.dtb' in text, text
        assert not fs(src / 'PackedKernel').exists()
        return len(str(cfg))
    packed = fs(src / 'PackedKernel').read_bytes()
    unpack_dir = src / 'unpacked'
    if name == 'long-output':
        parent = root / name / 'output'
        while len(str(parent)) < 340:
            parent /= 'output-' + 'y' * 45
        fs(parent).mkdir(parents=True)
        unpack_dir = parent / 'unpacked'
    # Baseline relative argv isolates old MAX_PATH failures from Unicode argv failures.
    target = 'unpacked' if unpack_dir.parent == src else str(unpack_dir)
    run(unpacker, [str(src / 'PackedKernel'), str(unpack_dir)] if name == 'long-config' else ['PackedKernel', target], root if name == 'long-config' else src)
    run(packer, [str(unpack_dir / 'config.cfg')], root) if len(str(unpack_dir)) >= 260 else run(packer, ['config.cfg'], unpack_dir)
    assert packed == fs(unpack_dir / 'RepackedKernel').read_bytes(), 'round-trip content changed'
    return max(len(str(cfg)), len(str(unpack_dir / 'config.cfg')))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for option in ('packer', 'unpacker', 'work-root'):
        parser.add_argument('--' + option, required=True, type=Path)
    parser.add_argument('--keep', action='store_true')
    args = parser.parse_args()
    if hasattr(sys.stdout, 'reconfigure'):
        sys.stdout.reconfigure(encoding='utf-8', errors='replace')
    args.work_root.mkdir(parents=True, exist_ok=True)
    root = args.work_root.resolve() / ('npath-' + uuid.uuid4().hex[:12])
    root.mkdir()
    failures = 0
    cases = ['relative', 'boundary-relative', 'long-config', 'unicode', 'absolute-cfg', 'absolute-io',
             'long-output', 'normalized', 'missing-input']
    if os.name == 'nt':
        cases.append('extended')
    for name in cases:
        try:
            length = exercise(name, root, args.packer.resolve(), args.unpacker.resolve())
            print(f'PASS {name}: longest checked path {length} characters')
        except Exception as error:
            failures += 1
            print(f'FAIL {name}: {error}')
    print(f'{len(cases) - failures}/{len(cases)} passed; artifacts: {root}')
    if not failures and not args.keep:
        shutil.rmtree(fs(root))
    return int(failures != 0)


if __name__ == '__main__':
    sys.exit(main())