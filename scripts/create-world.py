#!/usr/bin/env python3
"""Generate the ready-to-enter campaign world through tModLoader's world generator."""
from __future__ import annotations

import argparse
import os
from pathlib import Path
import subprocess
import time

from install import install
from run import ROOT, find_tml


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=ROOT / "runtime/world-template/Caverarria.wld")
    args = parser.parse_args()
    world = args.output.expanduser().resolve()
    if world.exists() or world.with_suffix(".twld").exists():
        raise SystemExit(f"World already exists; preserving it: {world}")
    world.parent.mkdir(parents=True, exist_ok=True)
    profile = ROOT / "runtime/world-generation-profile"
    install(profile / "tModLoader", None)
    configuration = profile / "serverconfig.txt"
    configuration.write_text(f"world={world}\nautocreate=1\nseed=caverarria\nworldname=Caverarria — The Island\nmaxplayers=1\nport=7785\n")
    tml = find_tml()
    log_path = world.parent / "generation.log"
    env = {key: value for key, value in os.environ.items() if not key.startswith("CAVERARRIA_")}
    with log_path.open("w") as log:
        process = subprocess.Popen([str(tml / "dotnet/dotnet"), str(tml / "tModLoader.dll"),
            "-server", "-nosteam", "-savedirectory", str(profile),
            "-tmlsavedirectory", str(profile / "tModLoader"), "-config", str(configuration)],
            cwd=tml, env=env, stdin=subprocess.PIPE, stdout=log, stderr=subprocess.STDOUT, text=True)
        try:
            deadline = time.monotonic() + 180
            while time.monotonic() < deadline:
                if process.poll() is not None:
                    raise RuntimeError(f"World generator exited with code {process.returncode}; see {log_path}")
                output = log_path.read_text(errors="replace")
                if "Server started" in output or "Listening on port" in output:
                    process.communicate("exit\n", timeout=30)
                    break
                time.sleep(.2)
            else:
                raise TimeoutError(f"World generation timed out; see {log_path}")
        finally:
            if process.poll() is None:
                process.terminate()
                try:
                    process.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait()
    if not world.is_file() or not world.with_suffix(".twld").is_file():
        raise SystemExit(f"tModLoader did not write both world files; see {log_path}")
    print(f"Fresh campaign world: {world}")


if __name__ == "__main__":
    main()
