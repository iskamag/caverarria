#!/usr/bin/env python3
"""Run bounded Rust/WASM feature checks through the packaged managed runtime."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import zipfile
from probe_paths import BINARY, HERE, WORK, build, host, load_script, write_report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dotnet', type=Path, help='Use this .NET 8 host instead of the bundled tModLoader host')
    parser.add_argument('--output', type=Path, default=WORK / 'feature-current.json')
    args = parser.parse_args()
    dotnet = host(args.dotnet)
    build()
    module = WORK / 'probe.wasm'
    subprocess.run(['rustc', '--edition=2021', '--crate-type=cdylib', '--target=wasm32-unknown-unknown',
                    '-C', 'opt-level=2', '-C', 'panic=abort', '-C', 'strip=debuginfo',
                    str(HERE / 'probe.rs'), '-o', str(module)], check=True, timeout=60)
    result = subprocess.run([str(dotnet), str(BINARY), str(module), 'cil'],
                            check=True, text=True, capture_output=True, timeout=60)
    execution = json.loads(result.stdout)
    setup = load_script('setup-managed-runtime.py')
    package = WORK / 'packages/webassembly.2.1.0.nupkg'
    with package.open('rb') as source:
        setup.verify_package(source.read())
    with zipfile.ZipFile(package) as archive:
        native = [name for name in archive.namelist() if '/native/' in name or name.endswith(('.so', '.dylib', '.a'))]
    if native or execution['pinvoke']:
        raise SystemExit('Pinned managed runtime has native package assets or explicit P/Invoke methods')
    evidence = {'scope':'Rust feature and synthetic RGBA probe; not real engine or campaign throughput',
                'rust_version':subprocess.check_output(['rustc', '--version'], text=True).strip(),
                'wasm_sha256':hashlib.sha256(module.read_bytes()).hexdigest(),
                'dotnet_host':str(dotnet), 'package_native_assets':{'WebAssembly/2.1.0':native},
                'results':[execution]}
    write_report(args.output, evidence)
    print(f'Passed packaged runtime; wrote {args.output}')


if __name__ == '__main__':
    main()
