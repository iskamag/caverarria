#!/usr/bin/env python3
"""Build the original game core as an import-free, memory-bounded WASM module."""
import argparse
from pathlib import Path
import shutil
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
TARGET = 'wasm32-unknown-unknown'
MAX_MEMORY = 256 * 1024 * 1024


def unsigned(data, offset):
    value = shift = 0
    while True:
        byte = data[offset]
        offset += 1
        value |= (byte & 127) << shift
        if not byte & 128:
            return value, offset
        shift += 7
        if shift > 35:
            raise ValueError('Invalid WASM integer')


def validate(module):
    data = module.read_bytes()
    if data[:8] != b'\0asm\1\0\0\0':
        raise ValueError('Invalid WASM header')
    offset = 8
    memory_checked = False
    while offset < len(data):
        section = data[offset]
        length, start = unsigned(data, offset + 1)
        end = start + length
        if end > len(data):
            raise ValueError('Invalid WASM section')
        if section == 2:
            count, _ = unsigned(data, start)
            if count:
                raise ValueError(f'Portable engine has {count} imports; expected zero')
        if section == 5:
            count, at = unsigned(data, start)
            if count != 1:
                raise ValueError('Expected one private linear memory')
            flags, at = unsigned(data, at)
            _, at = unsigned(data, at)
            if flags != 1:
                raise ValueError('Memory must be private, 32-bit, and have a maximum')
            maximum, _ = unsigned(data, at)
            if maximum * 65536 != MAX_MEMORY:
                raise ValueError('Unexpected guest memory limit')
            memory_checked = True
        offset = end
    if not memory_checked:
        raise ValueError('Engine memory was not found')
    return len(data)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--target-dir', type=Path, default=ROOT/'runtime/wasm-check-target')
    parser.add_argument('--output', type=Path,
                        default=ROOT/'mod/Caverarria/Assets/Engine/caverarria_bridge.wasm')
    args = parser.parse_args()
    log = ROOT/'runtime/wasm-build.log'
    log.parent.mkdir(parents=True, exist_ok=True)
    command = [shutil.which('cargo') or 'cargo', 'rustc', '--lib', '--release',
               '--manifest-path', str(ROOT/'rust-bridge/Cargo.toml'),
               '--no-default-features', '--features', 'portable', '--target', TARGET,
               '--target-dir', str(args.target_dir), '--',
               '-C', f'link-arg=--max-memory={MAX_MEMORY}']
    with log.open('w') as output:
        result = subprocess.run(command, cwd=ROOT, stdout=output, stderr=subprocess.STDOUT)
    if result.returncode:
        print('\n'.join(log.read_text().splitlines()[-50:]), file=sys.stderr)
        return result.returncode
    module = args.target_dir/TARGET/'release/caverarria_bridge.wasm'
    size = validate(module)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    temporary = args.output.with_suffix('.wasm.tmp')
    shutil.copyfile(module, temporary)
    temporary.replace(args.output)
    print(f'Built {args.output.relative_to(ROOT) if args.output.is_relative_to(ROOT) else args.output}: '
          f'{size:,} bytes, zero imports, 256 MiB maximum guest memory')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
