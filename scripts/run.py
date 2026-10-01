#!/usr/bin/env python3
"""Launch Caverarria in an isolated Terraria profile."""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]


def find_tml() -> Path:
    candidates = []
    if os.environ.get("TMODLOADER_PATH"):
        candidates.append(Path(os.environ["TMODLOADER_PATH"]))
    home = Path.home()
    steam_roots = [
        home / ".var/app/com.valvesoftware.Steam/.local/share/Steam",
        home / ".steam/steam", home / ".local/share/Steam",
    ]
    for steam in steam_roots:
        candidates.append(steam / "steamapps/common/tModLoader")
        library_file = steam / "steamapps/libraryfolders.vdf"
        if library_file.exists():
            import re
            for path in re.findall(r'"path"\s+"([^"]+)"', library_file.read_text()):
                candidates.append(Path(path.replace("\\\\", "\\")) / "steamapps/common/tModLoader")
    for candidate in candidates:
        if (candidate / "tModLoader.dll").is_file():
            return candidate.resolve()
    raise SystemExit("tModLoader not found; set TMODLOADER_PATH to its installation directory.")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--profile", type=Path, default=ROOT / "runtime/profile")
    parser.add_argument("--test-dir", type=Path, help="Enable file-based real-player input automation")
    parser.add_argument("--headless", action="store_true", help="Run the actual graphical client under Xvfb")
    parser.add_argument("--audio", action="store_true", help="Enable Cave Story audio during a headless test")
    parser.add_argument("--no-autostart", action="store_true", help="Show the normal tModLoader menu")
    campaign = parser.add_mutually_exclusive_group()
    campaign.add_argument("--load", action="store_true", help="Resume the saved campaign and Terraria character")
    campaign.add_argument("--new-game", action="store_true", help="Start a new campaign instead of resuming")
    parser.add_argument("--log", type=Path, help="Capture launcher stdout/stderr")
    args, extra = parser.parse_known_args()
    tml = find_tml()
    profile = args.profile.resolve()
    profile.mkdir(parents=True, exist_ok=True)
    mod = ROOT / "runtime/build/Caverarria.tmod"
    if not mod.exists():
        raise SystemExit("Build the mod first: scripts/build-mod.sh")
    # Select both the Terraria base and its tML child explicitly so the global
    # launch config cannot redirect this isolated profile.
    mods = profile / "tModLoader/Mods"
    mods.mkdir(parents=True, exist_ok=True)
    shutil.copy2(mod, mods / mod.name)
    (mods / "enabled.json").write_text(json.dumps(["Caverarria"]))
    config = profile / "tModLoader/config.json"
    if not config.exists():
        config.write_text(json.dumps({"Language": "en-US", "DisplayWidth": 1280,
            "DisplayHeight": 720, "Fullscreen": False, "VolumeMusic": 0.7,
            "VolumeSound": 1.0 if args.audio or not args.headless else 0.0,
            "VolumeAmbient": 1.0 if args.audio or not args.headless else 0.0}))
    settings = json.loads(config.read_text())
    # tML's startup migration prompts before loading mods if this metadata is
    # absent, even when every ordinary game setting is valid.
    if not settings.get("LastLaunchedTModLoaderVersion"):
        baseline = ROOT / "runtime/profile/tModLoader/config.json"
        known = json.loads(baseline.read_text()) if baseline.exists() else {}
        settings["LastLaunchedTModLoaderVersion"] = known.get("LastLaunchedTModLoaderVersion") or "2026.7.3.0"
        settings.setdefault("LastLaunchedVersion", 279)
        config.write_text(json.dumps(settings))
    env = os.environ.copy()
    campaign_directory = profile / "campaign"
    saved_campaign = campaign_directory.is_dir() and any(
        file.name.casefold() == "profile.dat" for file in campaign_directory.iterdir() if file.is_file())
    env.update({
        "CAVERARRIA_AUTOSTART": "0" if args.no_autostart else "1",
        "CAVERARRIA_DATA": env.get("CAVERARRIA_DATA", str(ROOT / "runtime/data")),
        "CAVERARRIA_SAVE": str(profile / "campaign"),
        "CAVERARRIA_LOAD": "0" if args.new_game else "1" if args.load or saved_campaign else "0",
        "CAVERARRIA_AUDIO": "1" if args.audio or not args.headless else "0",
    })
    # Select the working ALSA Pulse plugin where the desktop provides it. The
    # machine's default ALSA card may otherwise point at a missing device.
    pulse_socket = Path(env.get("XDG_RUNTIME_DIR", f"/run/user/{os.getuid()}")) / "pulse/native"
    if pulse_socket.exists():
        env.setdefault("CAVERARRIA_AUDIO_DEVICE", "pulse")
    if args.test_dir:
        test_dir = args.test_dir.resolve()
        test_dir.mkdir(parents=True, exist_ok=True)
        env["CAVERARRIA_TEST_DIR"] = str(test_dir)
    dotnet = tml / "dotnet/dotnet"
    command = [str(dotnet if dotnet.exists() else "dotnet"), str(tml / "tModLoader.dll"),
               "-nosteam", "-savedirectory", str(profile),
               "-tmlsavedirectory", str(profile / "tModLoader"), *extra]
    if args.headless:
        command = ["xvfb-run", "-a", "-s", "-screen 0 1280x720x24", *command]
    if args.log:
        args.log.parent.mkdir(parents=True, exist_ok=True)
        with args.log.open("w") as log:
            result = subprocess.run(command, cwd=tml, env=env, stdout=log, stderr=subprocess.STDOUT)
    else:
        result = subprocess.run(command, cwd=tml, env=env)
    sys.exit(result.returncode)


if __name__ == "__main__":
    main()
