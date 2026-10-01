"""Shared paths for published sources and ignored diagnostic outputs."""
from __future__ import annotations

import hashlib
import importlib.util
from pathlib import Path
import subprocess
import sys

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
WORK = ROOT / 'runtime/managed-wasm-probe'
PROJECT = HERE / 'ManagedWasmProbe.csproj'
BINARY = WORK / 'public-bin/ManagedWasmProbe.dll'
MODULE = ROOT / 'mod/Caverarria/Assets/Engine/caverarria_bridge.wasm'
DATA = ROOT / 'runtime/data'


def load_script(name):
    spec = importlib.util.spec_from_file_location(name.replace('-', '_'), ROOT / 'scripts' / name)
    script = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(script)
    return script


def host(override):
    if override is not None:
        return override.resolve()
    tml = subprocess.check_output([sys.executable, str(ROOT / 'scripts/find-tml.py')], text=True).strip()
    return Path(tml) / 'dotnet/dotnet'


def build():
    setup = load_script('setup-managed-runtime.py')
    runtime = ROOT / 'mod/Caverarria/lib/WebAssembly.dll'
    expected = setup.FILES['lib/net8.0/WebAssembly.dll'][1]
    if not runtime.exists() or hashlib.sha256(runtime.read_bytes()).hexdigest() != expected:
        raise SystemExit('Prepare the pinned DLL with python3 scripts/setup-managed-runtime.py first.')
    WORK.mkdir(parents=True, exist_ok=True)
    subprocess.run(['dotnet', 'build', str(PROJECT), '-c', 'Release', '--nologo',
                    f'-p:BaseIntermediateOutputPath={WORK / "public-obj"}/',
                    f'-p:OutputPath={BINARY.parent}/'], check=True, timeout=60)


def inspect_module(path):
    # Reuse the build validator: zero imports, one private 32-bit memory, 256 MiB cap.
    load_script('build-wasm.py').validate(path)
    return []


def write_report(path, value):
    import json
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2) + '\n')
