#!/usr/bin/env python3
"""Install Caverarria into a normal tModLoader profile, preserving existing saves."""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import shutil
import tempfile

from run import ROOT, find_tml


def default_profile() -> Path:
    home = Path.home()
    candidates = [
        home / ".var/app/com.valvesoftware.Steam/.local/share/Terraria/tModLoader",
        home / ".local/share/Terraria/tModLoader",
        home / "Documents/My Games/Terraria/tModLoader",
    ]
    for candidate in candidates:
        if candidate.is_dir():
            return candidate
    return candidates[0] if ".var/app/" in str(find_tml()) else candidates[1]


def atomic_copy(source: Path, destination: Path) -> None:
    destination.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.NamedTemporaryFile(prefix=".caverarria-", dir=destination.parent, delete=False) as pending:
        temporary = Path(pending.name)
    try:
        shutil.copy2(source, temporary)
        os.replace(temporary, destination)
    finally:
        temporary.unlink(missing_ok=True)


def write_json(destination: Path, value: object) -> None:
    destination.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.NamedTemporaryFile(mode="w", prefix=".caverarria-", dir=destination.parent, delete=False) as pending:
        temporary = Path(pending.name)
        json.dump(value, pending, indent=2)
        pending.write("\n")
    try:
        os.replace(temporary, destination)
    finally:
        temporary.unlink(missing_ok=True)


def install(profile: Path, template: Path | None, include_data: bool = False) -> dict:
    mod = ROOT / "runtime/build/Caverarria.tmod"
    data = ROOT / "runtime/data"
    required = [mod]
    if include_data:
        required.extend([data / "Head.tsc", ROOT / "runtime/Doukutsu.exe"])
    if template is not None:
        required.extend([template, template.with_suffix(".twld")])
    missing = [str(path) for path in required if not path.is_file()]
    if missing:
        raise SystemExit("Missing build or world files:\n" + "\n".join(missing))

    enabled_path = profile / "Mods/enabled.json"
    enabled = json.loads(enabled_path.read_text()) if enabled_path.exists() else []
    if not isinstance(enabled, list) or any(not isinstance(name, str) for name in enabled):
        raise SystemExit(f"Invalid mod list; installation stopped: {enabled_path}")

    assets = profile / "Caverarria"
    manifest_path = assets / "installation.json"
    manifest = json.loads(manifest_path.read_text()) if manifest_path.exists() else {}
    atomic_copy(mod, profile / "Mods" / mod.name)
    if include_data:
        shutil.copytree(data, assets / "data", dirs_exist_ok=True)
        executable = ROOT / "runtime/Doukutsu.exe"
        atomic_copy(executable, assets / executable.name)
    if "Caverarria" not in enabled:
        enabled.append("Caverarria")
    write_json(enabled_path, enabled)

    if template is not None:
        previous = manifest.get("world")
        world = Path(previous) if previous else None
        if world is None or world.parent != profile / "Worlds" or not world.exists():
            worlds = profile / "Worlds"
            worlds.mkdir(parents=True, exist_ok=True)
            world = worlds / "Caverarria.wld"
            number = 2
            while world.exists() or world.with_suffix(".twld").exists():
                world = worlds / f"Caverarria_{number}.wld"
                number += 1
            atomic_copy(template.with_suffix(".twld"), world.with_suffix(".twld"))
            atomic_copy(template, world)
        manifest["world"] = str(world)
    manifest.update(profile=str(profile), assets=str(assets), mod=str(profile / "Mods" / mod.name))
    write_json(manifest_path, manifest)
    return manifest


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--profile", type=Path, help="The tModLoader save folder itself (containing Mods, Players and Worlds)")
    parser.add_argument("--world", type=Path, default=ROOT / "runtime/world-template/Caverarria.wld", help="Fresh campaign world to install")
    parser.add_argument("--no-world", action="store_true", help="Install the mod and assets; create a world in-game with seed caverarria")
    parser.add_argument("--include-data", action="store_true", help="Copy already downloaded freeware data for an offline first launch")
    args = parser.parse_args()
    profile = (args.profile or default_profile()).expanduser().resolve()
    result = install(profile, None if args.no_world else args.world.expanduser().resolve(), args.include_data)
    print(f"Installed Caverarria into {profile}")
    if result.get("world"):
        print(f"Campaign world: {result['world']}")
    print("Open tModLoader → Single Player → choose your character → Caverarria — The Island.")
    print("For a separate new campaign, create a world with seed caverarria.")


if __name__ == "__main__":
    main()
