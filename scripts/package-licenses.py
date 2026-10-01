#!/usr/bin/env python3
"""Package exact notices from the locked wasm32 portable Cargo dependency graph."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import tarfile
import tomllib
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
TARGET = 'wasm32-unknown-unknown'
FORBIDDEN = {'cpal', 'imgui', 'imgui-sys', 'sdl2', 'sdl2-sys', 'glutin', 'winit',
             'webbrowser', 'open', 'wasm-bindgen', 'js-sys', 'web-sys', 'wasi'}
# These published crates omit their workspace-root licenses. Their package VCS
# metadata identifies the exact upstream revision used here; every fetched byte
# is additionally pinned and cached for subsequent offline packaging.
POINTER_REV = '0fe399f8f7e519959224069360f3900189086683'
POINTER_LICENSES = [
    ('LICENSE-MIT', f'https://raw.githubusercontent.com/CAD97/pointer-utils/{POINTER_REV}/LICENSE/MIT',
     '68360edd6e6db7a433dd72abcd0f81516c0dd438d9b9f7d687cb6ba0afc7242e'),
    ('LICENSE-APACHE', f'https://raw.githubusercontent.com/CAD97/pointer-utils/{POINTER_REV}/LICENSE/APACHE',
     'a6cba85bc92e0cff7a450b1d873c0eaa2e9fc96bf472df0247a26bec77bf3ff9'),
]
FALLBACKS = {
    ('erasable', '1.3.0'): (POINTER_REV, 'pointer-utils', POINTER_LICENSES),
    ('rc-box', '1.3.0'): (POINTER_REV, 'pointer-utils', POINTER_LICENSES),
    ('pelite-macros', '0.1.1'): (
        '432e769bf3152f21452a4b8908639f5ed1595912', 'pelite-macros', [
            ('license.txt', 'https://raw.githubusercontent.com/CasualX/pelite/432e769bf3152f21452a4b8908639f5ed1595912/license.txt',
             '1365d50f7e842b1e743ba450b43eb711d748dd04f04554c43b39dd4715d30e11')]),
}


def sha(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def run(arguments: list[str]) -> str:
    return subprocess.check_output(arguments, cwd=ROOT, text=True, timeout=60)


def graph(metadata: dict) -> tuple[list[dict], dict]:
    packages = {p['id']: p for p in metadata['packages']}
    nodes = {n['id']: n for n in metadata['resolve']['nodes']}
    todo = [metadata['resolve']['root']]
    included = set()
    while todo:
        package = todo.pop()
        if package in included:
            continue
        included.add(package)
        for dependency in nodes[package]['deps']:
            if any(kind['kind'] != 'dev' for kind in dependency['dep_kinds']):
                todo.append(dependency['pkg'])
    selected = sorted((packages[i] for i in included), key=lambda p: (p['name'], p['version']))
    unwanted = FORBIDDEN & {p['name'] for p in selected}
    if unwanted:
        raise ValueError(f'Native/OS runner dependencies resolved unexpectedly: {sorted(unwanted)}')
    return selected, nodes


def license_files(directory: Path) -> list[Path]:
    return sorted((p for p in directory.iterdir() if p.is_file() and p.name.lower().startswith(
        ('license', 'copying', 'unlicense', 'notice', 'copyright'))), key=lambda p: p.name)


def fallback_files(package: dict, cache: Path, offline: bool) -> list[tuple[str, bytes, dict]]:
    key = package['name'], package['version']
    if key not in FALLBACKS:
        raise ValueError(f'No full license texts found for {key}')
    revision, group, entries = FALLBACKS[key]
    vcs = json.loads((Path(package['manifest_path']).parent / '.cargo_vcs_info.json').read_text())
    if vcs['git']['sha1'] != revision:
        raise ValueError(f'Unexpected source revision for license fallback: {key}')
    files = []
    for filename, url, expected in entries:
        path = cache / group / filename
        if path.exists():
            data = path.read_bytes()
        elif offline:
            raise ValueError(f'Missing pinned license cache: {path}; run without --offline once')
        else:
            with urllib.request.urlopen(url, timeout=30) as response:
                data = response.read()
            if sha(data) != expected:
                raise ValueError(f'Upstream license checksum mismatch: {url}')
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(data)
        if sha(data) != expected:
            raise ValueError(f'Cached upstream license checksum mismatch: {path}')
        files.append((filename, data, {'kind': 'exact-upstream-vcs-file', 'url': url, 'revision': revision}))
    return files


def verify_registry_sources(package: dict, checksum: str | None, files: list[Path]) -> str | None:
    if not (package.get('source') or '').startswith('registry+'):
        return None
    directory = Path(package['manifest_path']).parent
    registry = directory.parent.name
    archive = directory.parents[2] / 'cache' / registry / f"{package['name']}-{package['version']}.crate"
    if not archive.is_file() or not checksum:
        raise ValueError(f'Missing locked registry archive for license verification: {archive}')
    if sha(archive.read_bytes()) != checksum:
        raise ValueError(f'Cargo.lock archive checksum mismatch: {archive}')
    vcs = directory / '.cargo_vcs_info.json'
    verification_files = files + ([vcs] if vcs.is_file() else [])
    with tarfile.open(archive, 'r:gz') as original:
        for file in verification_files:
            member = f"{package['name']}-{package['version']}/{file.relative_to(directory).as_posix()}"
            content = original.extractfile(member)
            if content is None or content.read() != file.read_bytes():
                raise ValueError(f'License source differs from the locked registry archive: {file}')
    return checksum


def collect(args) -> dict[str, bytes]:
    command = ['cargo', 'metadata', '--locked', '--format-version', '1', '--filter-platform', TARGET,
               '--no-default-features', '--features', 'portable', '--manifest-path', 'rust-bridge/Cargo.toml']
    if args.offline:
        command.append('--offline')
    metadata = json.loads(run(command))
    selected, nodes = graph(metadata)
    lock_path = ROOT / 'rust-bridge/Cargo.lock'
    lock = tomllib.loads(lock_path.read_text())
    checksums = {(p['name'], p['version'], p.get('source')): p.get('checksum') for p in lock['package']}
    output: dict[str, bytes] = {'LICENSE.txt': (ROOT / 'LICENSE').read_bytes()}
    inventory = []
    for package in selected:
        name, version = package['name'], package['version']
        directory = Path(package['manifest_path']).parent
        declared = package.get('license')
        row = {'name': name, 'version': version, 'licenseExpression': declared,
               'source': package.get('source') or ('workspace/rust-bridge' if name == 'caverarria-bridge' else 'vendor/doukutsu-rs'),
               'repository': package.get('repository'), 'features': sorted(nodes[package['id']]['features']),
               'cargoPackageChecksum': checksums.get((name, version, package.get('source'))), 'licenseFiles': []}
        if name == 'caverarria-bridge':
            row['exception'] = 'Project-owned integration code; MIT license at LICENSE.txt.'
            row['licenseFiles'] = [{'path': 'LICENSE.txt', 'bytes': len(output['LICENSE.txt']), 'sha256': sha(output['LICENSE.txt'])}]
            inventory.append(row)
            continue
        candidates = license_files(directory)
        declared_file = package.get('license_file')
        if declared_file:
            explicit = directory / declared_file
            if explicit.is_file() and explicit not in candidates:
                candidates.append(explicit)
        verified_archive = verify_registry_sources(package, row['cargoPackageChecksum'], candidates)
        if verified_archive:
            row['licenseSourceVerification'] = 'Exact license files compared with Cargo.lock-checksummed registry archive'
        if name == 'doukutsu-rs':
            row['licenseExpression'] = 'MIT'
            row['upstreamRevision'] = json.loads((ROOT / 'docs/upstream.json').read_text())['doukutsu-rs']['revision']
            row['modificationNotice'] = 'Caverarria modifies the upstream engine for host stepping, external Terraria kinematics, a portable memory VFS and PCM, and software rendering.'
        files = [(p.name, p.read_bytes(), {'kind': 'package-source-file', 'path': p.relative_to(directory).as_posix()})
                 for p in candidates]
        if not files:
            files = fallback_files(package, args.cache, args.offline)
        for filename, data, provenance in files:
            destination = f'licenses/rust/{name}-{version}/{filename}'
            output[destination] = data
            row['licenseFiles'].append({'path': destination, 'bytes': len(data), 'sha256': sha(data), 'provenance': provenance})
        inventory.append(row)

    # The precompiled Rust standard library is not represented in Cargo metadata.
    # Preserve the target distribution's exact notices separately, together with
    # the Unicode text used by Rust core's Unicode data and the release notice.
    rust_directory = args.rust_license_dir
    if rust_directory is None:
        candidates = [Path('/usr/share/licenses/rust-wasm'), Path(run(['rustc', '--print', 'sysroot']).strip()) / 'share/doc/rust/html']
        rust_directory = next((p for p in candidates if (p / 'LICENSE-MIT').is_file()), None)
    if rust_directory is None:
        raise ValueError('Rust target distribution notices not found; provide --rust-license-dir')
    rust_files = license_files(rust_directory)
    if not any(p.name == 'LICENSE-MIT' for p in rust_files):
        raise ValueError('Rust target distribution requires its original LICENSE-MIT')
    standard = {'name': 'Rust target standard library', 'rustcVersionVerbose': run(['rustc', '--version', '--verbose']).strip(),
                'licenseExpression': 'Apache-2.0 OR MIT, with separately noted components', 'licenseFiles': []}
    for p in rust_files:
        destination = 'licenses/rust-standard-library/' + p.name
        data = p.read_bytes(); output[destination] = data
        standard['licenseFiles'].append({'path': destination, 'sha256': sha(data), 'bytes': len(data)})
    library_notice = Path('/usr/share/licenses/rust/COPYRIGHT-library.html.rustc')
    if library_notice.is_file():
        destination = 'licenses/rust-standard-library/COPYRIGHT-library.html'
        data = library_notice.read_bytes(); output[destination] = data
        standard['licenseFiles'].append({'path': destination, 'sha256': sha(data), 'bytes': len(data)})
    unicode = next(p for p in inventory if p['name'] == 'unicode-ident')
    standard['additionalUnicodeText'] = next(f['path'] for f in unicode['licenseFiles'] if 'UNICODE' in f['path'])
    result = {'schemaVersion': 1, 'cargoTarget': TARGET, 'rootFeatures': ['portable'], 'defaultFeatures': False,
              'cargoLockSha256': sha(lock_path.read_bytes()), 'graphCommand': ' '.join(command),
              'scope': 'Locked normal/build dependency graph for the portable target, including build-time proc macros; not a claim that every graph package is embedded in the module.',
              'resolvedPackageCount': len(inventory), 'thirdPartyPackageCount': len(inventory)-1,
              'packages': inventory, 'rustTargetDistribution': standard,
              'managedRuntime': {'name': 'WebAssembly', 'version': '2.1.0', 'licenseExpression': 'Apache-2.0',
                                 'licenseFile': 'lib/WebAssembly.LICENSE', 'copyright': 'Copyright © Ryan Lamansky. All rights reserved.'},
              'excluded': ['Optional native/audio/windowing runners not in the portable graph', 'NXEngine-evo (reference only)',
                           'WACS (comparison probe only)', 'Original freeware game data (first-use download, not bundled)']}
    # Whether network was permitted does not affect the resolved graph/results.
    result['graphCommand'] = result['graphCommand'].replace(' --offline', '')
    output['ThirdPartyInventory.json'] = (json.dumps(result, indent=2, ensure_ascii=False) + '\n').encode()
    lines = ['Caverarria third-party notices', '',
             'Generated by scripts/package-licenses.py from rust-bridge/Cargo.lock.',
             'Target: wasm32-unknown-unknown; no default features; portable feature.',
             'The complete original license texts are packaged at the paths below.',
             'Build-time dependencies are retained in the inventory for completeness.',
             'Caverarria-owned integration code is MIT licensed; see LICENSE.txt.', '',
             'Cave Story: Studio Pixel. English freeware translation: Aeon Genesis.',
             'Original game data is downloaded separately on first use and is not in this mod.', '',
             'WebAssembly for .NET 2.1.0: Ryan Lamansky; Apache-2.0.',
             'Copyright © Ryan Lamansky. All rights reserved.', 'Full license: lib/WebAssembly.LICENSE', '']
    for row in inventory:
        if 'exception' in row:
            continue
        lines += [f"{row['name']} {row['version']} — {row['licenseExpression']}"]
        if row.get('modificationNotice'):
            lines += [row['modificationNotice']]
        lines += [f"  {f['path']}" for f in row['licenseFiles']] + ['']
    lines += ['Rust standard library and target distribution:', *[f"  {f['path']}" for f in standard['licenseFiles']],
              f"  Unicode license text: {standard['additionalUnicodeText']}", '',
              'ThirdPartyInventory.json records package versions, selected features, source provenance and notice SHA256 values.', '']
    output['ThirdPartyNotices.txt'] = '\n'.join(lines).encode()
    return output


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, default=ROOT / 'mod/Caverarria')
    parser.add_argument('--cache', type=Path, default=ROOT / 'runtime/license-cache')
    parser.add_argument('--rust-license-dir', type=Path)
    parser.add_argument('--offline', action='store_true', help='Use existing Cargo packages and the pinned upstream license cache only')
    parser.add_argument('--check', action='store_true', help='Verify generated content without changing the mod files')
    args = parser.parse_args()
    try:
        files = collect(args)
    except (ValueError, subprocess.CalledProcessError) as error:
        raise SystemExit(str(error)) from error
    stale = []
    old_inventory = args.output / 'ThirdPartyInventory.json'
    if old_inventory.is_file():
        previous = json.loads(old_inventory.read_text())
        obsolete = {f['path'] for p in previous.get('packages', []) for f in p.get('licenseFiles', [])} - files.keys()
        for relative in sorted(obsolete):
            if not relative.startswith('licenses/rust/') or '..' in Path(relative).parts:
                raise SystemExit('Previous inventory contains an invalid generated license path')
            destination = args.output / relative
            if destination.is_file():
                if args.check:
                    stale.append(relative + ' (obsolete)')
                else:
                    destination.unlink()
    for relative, data in sorted(files.items()):
        destination = args.output / relative
        if not destination.is_file() or destination.read_bytes() != data:
            if args.check:
                stale.append(relative)
            else:
                destination.parent.mkdir(parents=True, exist_ok=True)
                destination.write_bytes(data)
    if stale:
        raise SystemExit('License package is missing or stale:\n' + '\n'.join(stale))
    inventory = json.loads(files['ThirdPartyInventory.json'])
    print(f"{'Verified' if args.check else 'Packaged'} {inventory['thirdPartyPackageCount']} third-party Cargo packages, "
          f"{sum(len(p['licenseFiles']) for p in inventory['packages'] if 'exception' not in p)} exact license/notice files, plus Rust target notices. "
          'Own integration code is MIT licensed.')


if __name__ == '__main__':
    main()
