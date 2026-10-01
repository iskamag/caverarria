#!/usr/bin/env python3
"""Run the actual Rust core and exact mod adapter on a .NET 8 host."""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import subprocess
from probe_paths import BINARY, DATA, MODULE, ROOT, WORK, build, host, inspect_module, write_report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dotnet', type=Path, help='Use this .NET 8 host instead of the bundled tModLoader host')
    parser.add_argument('--module', type=Path, default=MODULE)
    parser.add_argument('--data', type=Path, default=DATA,
                        help='Original freeware data directory with adjacent Doukutsu.exe')
    parser.add_argument('--adapter-only', action='store_true')
    parser.add_argument('--output-dir', type=Path, default=WORK)
    args = parser.parse_args()
    dotnet = host(args.dotnet)
    imports = inspect_module(args.module.resolve())
    build()
    for mode in (['adapter'] if args.adapter_only else ['core', 'adapter']):
        completed = subprocess.run([str(dotnet), str(BINARY), '--' + mode,
                                    str(args.module.resolve()), str(ROOT), str(args.data.resolve())],
                                   text=True, capture_output=True, timeout=90)
        if completed.returncode:
            raise SystemExit(completed.stderr or completed.stdout)
        evidence = json.loads(completed.stdout)
        evidence['declaredImports'] = imports
        evidence['dotnetHost'] = str(dotnet)
        target = args.output_dir / f'{mode}-evidence.json'
        write_report(target, evidence)
        print(f'Passed actual {mode}; wrote {target}')


if __name__ == '__main__':
    main()
