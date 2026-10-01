#!/usr/bin/env python3
"""Package a playable alpha from final build artifacts, without building the mod."""
from __future__ import annotations

import argparse
import gzip
import hashlib
import io
import json
from pathlib import Path
import re
import struct
import zipfile
import zlib

ROOT = Path(__file__).resolve().parents[1]
FIXED_ZIP_TIME = (1980, 1, 1, 0, 0, 0)
MAX_MEMBER = 64 * 1024 * 1024


def sha(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def uleb(data: bytes, offset: int) -> tuple[int, int]:
    value = shift = 0
    while offset < len(data) and shift <= 35:
        byte = data[offset]; offset += 1
        value |= (byte & 127) << shift
        if not byte & 128:
            return value, offset
        shift += 7
    raise ValueError('Invalid binary integer')


def tmod_members(data: bytes) -> tuple[dict[str, bytes], dict]:
    source = io.BytesIO(data)

    def read(length: int) -> bytes:
        if not 0 <= length <= MAX_MEMBER:
            raise ValueError('Invalid mod member size')
        value = source.read(length)
        if len(value) != length:
            raise ValueError('Truncated mod')
        return value

    def integer() -> int:
        return struct.unpack('<i', read(4))[0]

    def string() -> str:
        length = shift = 0
        while shift <= 28:
            byte = read(1)[0]
            length |= (byte & 127) << shift
            if byte < 128:
                return read(length).decode('utf-8')
            shift += 7
        raise ValueError('Invalid mod string')

    if read(4) != b'TMOD':
        raise ValueError('Expected a tModLoader .tmod archive')
    tml_version = string(); expected_hash = read(20); read(256)
    payload_length = integer(); payload_start = source.tell()
    if payload_length != len(data) - payload_start or hashlib.sha1(data[payload_start:]).digest() != expected_hash:
        raise ValueError('Mod archive length or embedded checksum mismatch')
    name, version = string(), string()
    if name != 'Caverarria':
        raise ValueError(f'Expected Caverarria mod; got {name}')
    count = integer()
    if not 1 <= count <= 10000:
        raise ValueError('Invalid mod member count')
    table = [(string(), integer(), integer()) for _ in range(count)]
    members = {}
    forbidden_suffixes = {'.so', '.dylib', '.a', '.exe', '.pbm', '.pxm', '.pxe', '.tsc', '.org', '.pcm', '.plr', '.tplr', '.dat'}
    forbidden_parts = {'players', 'campaigns', 'logs', 'freeware', 'runtime'}
    for name, length, stored in table:
        path = Path(name)
        if path.is_absolute() or '..' in path.parts or '\\' in name or name in members:
            raise ValueError(f'Invalid or duplicate mod path: {name}')
        if path.suffix.lower() in forbidden_suffixes or forbidden_parts & {p.lower() for p in path.parts}:
            raise ValueError(f'Release mod contains a forbidden asset/native library/save: {name}')
        if path.suffix.lower() == '.dll' and name not in {'Caverarria.dll', 'lib/WebAssembly.dll'}:
            raise ValueError(f'Unexpected DLL in portable release: {name}')
        blob = read(stored)
        if stored != length:
            inflater = zlib.decompressobj(-15)
            blob = inflater.decompress(blob, MAX_MEMBER + 1)
            if not inflater.eof or inflater.unused_data or len(blob) > MAX_MEMBER:
                raise ValueError(f'Invalid compressed mod member: {name}')
        if not 0 <= length <= MAX_MEMBER or len(blob) != length:
            raise ValueError(f'Mod member length mismatch: {name}')
        members[name] = blob
    if source.tell() != len(data):
        raise ValueError('Unexpected bytes after mod archive')
    return members, {'name': 'Caverarria', 'version': version, 'tModLoaderBuildVersion': tml_version}


def verify_portable_mod(data: bytes) -> dict:
    members, information = tmod_members(data)
    required = {'Caverarria.dll', 'Assets/Engine/caverarria_bridge.wasm', 'lib/WebAssembly.dll',
                'lib/WebAssembly.LICENSE', 'ThirdPartyInventory.json', 'ThirdPartyNotices.txt'}
    if not required <= members.keys():
        raise ValueError(f'Mod missing portable engine or license files: {sorted(required - members.keys())}')
    if sha(members['lib/WebAssembly.dll']) != '7904c9db84ed069bc735ae33b86add27e4680dd3e3cc2d74f8bbbc4a6d936fb1':
        raise ValueError('Packaged runner differs from pinned WebAssembly2.1.0')
    if sha(members['lib/WebAssembly.LICENSE']) != 'b9fd7c9a677750c930f827b8ff6e3fad93bac91f04ca54df2e3cca68971dcef2':
        raise ValueError('Packaged managed runner license differs from upstream')
    wasm = members['Assets/Engine/caverarria_bridge.wasm']
    if wasm[:8] != b'\0asm\1\0\0\0':
        raise ValueError('Invalid embedded WASM')
    offset = 8; memory_max = None
    while offset < len(wasm):
        section = wasm[offset]; length, start = uleb(wasm, offset + 1); end = start + length
        if end > len(wasm):
            raise ValueError('Truncated WASM section')
        if section == 2 and uleb(wasm, start)[0] != 0:
            raise ValueError('Embedded portable engine must declare zero imports')
        if section == 5:
            count, at = uleb(wasm, start)
            flags, at = uleb(wasm, at)
            _, at = uleb(wasm, at)
            if count != 1 or flags != 1:
                raise ValueError('Embedded engine must have bounded private memory')
            pages, _ = uleb(wasm, at); memory_max = pages * 65536
        offset = end
    if memory_max != 256 * 1024 * 1024:
        raise ValueError('Unexpected embedded engine memory limit')
    inventory = json.loads(members['ThirdPartyInventory.json'])
    licenses = [f for package in inventory['packages'] for f in package['licenseFiles']]
    licenses += inventory['rustTargetDistribution']['licenseFiles']
    for notice in licenses:
        if notice['path'] not in members or sha(members[notice['path']]) != notice['sha256']:
            raise ValueError(f'Missing or changed third-party notice: {notice["path"]}')
    information.update({'wasmSha256': sha(wasm), 'wasmImports': 0, 'wasmMaximumMemoryBytes': memory_max,
                        'managedRunner': 'WebAssembly for .NET 2.1.0',
                        'managedRunnerSha256': sha(members['lib/WebAssembly.dll']),
                        'thirdPartyCargoPackages': inventory['thirdPartyPackageCount'], 'licenseFilesVerified': len(licenses)})
    return information


def section(markdown: str, heading: str) -> str:
    match = re.search(r'^## ' + re.escape(heading) + r'\n(.*?)(?=^## |\Z)', markdown, re.M | re.S)
    if match is None:
        raise ValueError(f'Alpha documentation missing section: {heading}')
    return match.group(1).strip() + '\n'


def package(args) -> tuple[Path, dict]:
    if not re.fullmatch(r'\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?', args.version):
        raise ValueError('Use a release version such as 0.1.0-alpha.1')
    mod = args.mod.read_bytes(); world = args.world.read_bytes(); world_data = args.world.with_suffix('.twld').read_bytes()
    information = verify_portable_mod(mod)
    if world[4:11] != b'relogic' or not world_data.startswith(b'\x1f\x8b'):
        raise ValueError('Invalid paired Terraria world template')
    with gzip.GzipFile(fileobj=io.BytesIO(world_data)) as compressed:
        world_tags = compressed.read(MAX_MEMBER + 1)
    # TagIO stores an NBT byte for the marker and a 32-character GUID string.
    # A small Terraria world already expands to about20MiB of tile data.
    campaign_id = re.search(rb'\x08\x00\x0acampaignId\x00\x20([0-9a-f]{32})', world_tags)
    if len(world_tags) > MAX_MEMBER or b'\x01\x00\x12caverarriaCampaign\x01' not in world_tags or campaign_id is None:
        raise ValueError('World template does not contain the campaign marker')
    alpha = (ROOT / 'docs/ALPHA.md').read_text()
    install = alpha.split('## Known alpha limitations')[0].rstrip() + '\n\n'
    install += '## Alpha feedback\n\nRead `KNOWN_ALPHA_LIMITATIONS.md` before playing.\n\n' + section(alpha, 'Report a problem')
    install = install.replace('# Caverarria playable alpha\n', f'# Caverarria playable alpha {args.version}\n', 1)
    files = {'Caverarria.tmod': mod, 'Worlds/Caverarria.wld': world, 'Worlds/Caverarria.twld': world_data,
             'INSTALL.md': install.encode(),
             'KNOWN_ALPHA_LIMITATIONS.md': (f'# Known alpha limitations — {args.version}\n\n' + section(alpha, 'Known alpha limitations')).encode(),
             'CREDITS.md': ('# Credits\n\n' + section(alpha, 'Credits')).encode()}
    manifest = {'schemaVersion': 1, 'releaseVersion': args.version, 'mod': information,
                'worldTemplate': {'campaignId': campaign_id.group(1).decode()},
                'verificationScope': 'Playable alpha; Linux menu/early-game/save/audio checks. Full campaign and every ending remain unverified.',
                'files': {name: {'bytes': len(data), 'sha256': sha(data)} for name, data in sorted(files.items())}}
    files['MANIFEST.json'] = (json.dumps(manifest, indent=2) + '\n').encode()
    files['SHA256SUMS.txt'] = ''.join(f'{sha(data)}  {name}\n' for name, data in sorted(files.items())).encode()
    destination = args.output or ROOT / 'runtime/release' / f'Caverarria-{args.version}.zip'
    destination.parent.mkdir(parents=True, exist_ok=True)
    temporary = destination.with_suffix('.zip.tmp')
    with zipfile.ZipFile(temporary, 'w') as archive:
        for name, data in sorted(files.items()):
            item = zipfile.ZipInfo(name, FIXED_ZIP_TIME)
            item.create_system = 3; item.external_attr = 0o100644 << 16
            item.compress_type = zipfile.ZIP_DEFLATED
            archive.writestr(item, data, compresslevel=9)
    with zipfile.ZipFile(temporary) as archive:
        if set(archive.namelist()) != set(files) or archive.testzip() is not None:
            raise ValueError('Release archive member/CRC verification failed')
        for name, data in files.items():
            if archive.read(name) != data:
                raise ValueError(f'Release archive contents differ: {name}')
    temporary.replace(destination)
    digest = sha(destination.read_bytes())
    destination.with_suffix('.zip.sha256').write_text(f'{digest}  {destination.name}\n')
    return destination, {'release': str(destination), 'bytes': destination.stat().st_size, 'sha256': digest,
                         'files': sorted(files), 'mod': information}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--version', default='0.1.0-alpha.1')
    parser.add_argument('--mod', type=Path, default=ROOT / 'runtime/build/Caverarria.tmod')
    parser.add_argument('--world', type=Path, default=ROOT / 'runtime/world-template/Caverarria.wld')
    parser.add_argument('--output', type=Path)
    args = parser.parse_args()
    try:
        _, result = package(args)
    except (ValueError, OSError, KeyError, zlib.error) as error:
        raise SystemExit(str(error)) from error
    print(json.dumps(result, indent=2))


if __name__ == '__main__':
    main()
