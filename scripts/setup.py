#!/usr/bin/env python3
"""Build the portable campaign runtime and the mod using installed tModLoader."""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]


def run(*arguments: str, cwd: Path = ROOT) -> None:
    subprocess.run(arguments, cwd=cwd, check=True)


def engine_source() -> None:
    specification = json.loads((ROOT / "docs/upstream.json").read_text())["doukutsu-rs"]
    engine = ROOT / "vendor/doukutsu-rs"
    if not (engine / "Cargo.toml").exists():
        engine.parent.mkdir(parents=True, exist_ok=True)
        run("git", "clone", specification["repository"], str(engine))
        run("git", "checkout", "--detach", specification["revision"], cwd=engine)
    revision = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=engine, text=True).strip()
    if revision != specification["revision"]:
        raise SystemExit(f"Engine revision is {revision}; expected {specification['revision']}")
    if "pub mod cavebridge;" not in (engine / "src/lib.rs").read_text():
        patch = ROOT / "rust-bridge/doukutsu-rs.patch"
        if not patch.exists():
            raise SystemExit("Missing doukutsu-rs integration patch")
        run("git", "apply", "--check", str(patch), cwd=engine)
        run("git", "apply", str(patch), cwd=engine)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--skip-engine", "--skip-native", dest="skip_engine", action="store_true")
    parser.add_argument("--skip-mod", action="store_true")
    parser.add_argument("--skip-world", action="store_true", help="Do not generate the ready-to-enter campaign world")
    parser.add_argument("--archive", type=Path, help="Already downloaded freeware zip")
    args = parser.parse_args()
    if not (ROOT / "runtime/data/Head.tsc").is_file():
        command = [sys.executable, str(ROOT / "scripts/setup-data.py")]
        if args.archive:
            command.extend(["--archive", str(args.archive)])
        run(*command)
    if not args.skip_engine:
        engine_source()
        run(sys.executable, str(ROOT / "scripts/build-wasm.py"))
    if not args.skip_mod:
        run(sys.executable, str(ROOT / "scripts/setup-managed-runtime.py"))
        run("bash", str(ROOT / "scripts/build-mod.sh"))
    if not args.skip_world and not (ROOT / "runtime/world-template/Caverarria.wld").exists():
        run(sys.executable, str(ROOT / "scripts/create-world.py"))
    print("Caverarria build ready. Install into normal tModLoader with python3 scripts/install.py, or launch with ./play.sh")


if __name__ == "__main__":
    main()
