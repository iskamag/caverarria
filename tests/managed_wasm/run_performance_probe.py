#!/usr/bin/env python3
"""Partition real adapter CPU work; this does not establish campaign completion."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
from probe_paths import BINARY, DATA, MODULE, ROOT, WORK, build, host, inspect_module, write_report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dotnet', type=Path)
    parser.add_argument('--module', type=Path, default=MODULE)
    parser.add_argument('--data', type=Path, default=DATA)
    parser.add_argument('--output', type=Path, default=WORK / 'performance-current.json')
    parser.add_argument('--frames', type=int, default=300)
    parser.add_argument('--skip-build', action='store_true')
    args = parser.parse_args()
    if not 100 <= args.frames <= 500:
        parser.error('--frames must be 100–500')
    dotnet = host(args.dotnet)
    inspect_module(args.module.resolve())
    if not args.skip_build:
        build()
    completed = subprocess.run([str(dotnet), str(BINARY), '--performance', str(args.module.resolve()),
                                str(ROOT), str(args.frames), str(args.data.resolve())],
                               text=True, stdout=subprocess.PIPE, check=True, timeout=180)
    evidence = json.loads(completed.stdout)
    evidence['dotnetHost'] = str(dotnet)
    evidence['adapterSha256'] = hashlib.sha256((ROOT / 'mod/Caverarria/WasmEngine.cs').read_bytes()).hexdigest()
    evidence['dotnetEnvironment'] = {k:v for k,v in os.environ.items() if k.startswith(('DOTNET_', 'COMPlus_'))}
    write_report(args.output, evidence)
    print(f'Passed performance partition; wrote {args.output}')


if __name__ == '__main__':
    main()
