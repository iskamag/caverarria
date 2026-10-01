#!/usr/bin/env python3
"""Pin and package the portable .NET WebAssembly runtime at build/setup time."""
from __future__ import annotations

import argparse
import hashlib
import io
from pathlib import Path
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parents[1]
VERSION = "2.1.0"
URL = f"https://api.nuget.org/v3-flatcontainer/webassembly/{VERSION}/webassembly.{VERSION}.nupkg"
PACKAGE_SHA256 = "f1f893988f13a58957eb7d6361ff085f84954aa5ed7739a21db1bcd2a1fe9238"
FILES = {
    "lib/net8.0/WebAssembly.dll": ("WebAssembly.dll", "7904c9db84ed069bc735ae33b86add27e4680dd3e3cc2d74f8bbbc4a6d936fb1"),
    "LICENSE/LICENSE": ("WebAssembly.LICENSE", "b9fd7c9a677750c930f827b8ff6e3fad93bac91f04ca54df2e3cca68971dcef2"),
}


def verify_package(data: bytes) -> dict[str, bytes]:
    digest = hashlib.sha256(data).hexdigest()
    if digest != PACKAGE_SHA256:
        raise ValueError(f"WebAssembly {VERSION} package checksum mismatch: {digest}")
    result = {}
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        for source, (destination, expected) in FILES.items():
            content = archive.read(source)
            if hashlib.sha256(content).hexdigest() != expected:
                raise ValueError(f"Pinned package member checksum mismatch: {source}")
            result[destination] = content
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cache", type=Path, default=ROOT / "runtime/managed-wasm-probe/packages" / f"webassembly.{VERSION}.nupkg")
    parser.add_argument("--output", type=Path, default=ROOT / "mod/Caverarria/lib")
    parser.add_argument("--offline", action="store_true", help="Require the already downloaded pinned package")
    args = parser.parse_args()
    if args.cache.exists():
        data = args.cache.read_bytes()
    elif args.offline:
        raise SystemExit(f"Missing pinned package cache: {args.cache}")
    else:
        with urllib.request.urlopen(URL, timeout=30) as response:
            data = response.read()
    try:
        files = verify_package(data)
    except ValueError as error:
        raise SystemExit(str(error)) from error
    args.cache.parent.mkdir(parents=True, exist_ok=True)
    if not args.cache.exists():
        args.cache.write_bytes(data)
    args.output.mkdir(parents=True, exist_ok=True)
    for name, content in files.items():
        destination = args.output / name
        if not destination.exists() or destination.read_bytes() != content:
            temporary = destination.with_suffix(destination.suffix + ".tmp")
            temporary.write_bytes(content)
            temporary.replace(destination)
        print(f"Packaged {destination.relative_to(ROOT) if destination.is_relative_to(ROOT) else destination} sha256={hashlib.sha256(content).hexdigest()}")


if __name__ == "__main__":
    main()
