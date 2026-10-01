#!/usr/bin/env python3
"""Fetch the original English freeware data, independently of commercial installs."""
from __future__ import annotations

import argparse
import hashlib
import io
import json
from pathlib import Path, PurePosixPath
import shutil
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parents[1]
URL = "https://www.cavestory.org/downloads/cavestoryen.zip"
SHA256 = "aa87fa30bee9b4980640c7e104791354e0f1f6411ee0d45a70af70046aa0685f"


def install(archive: bytes, destination: Path) -> None:
    digest = hashlib.sha256(archive).hexdigest()
    if digest != SHA256:
        raise ValueError(f"Freeware archive changed: expected {SHA256}, got {digest}")
    destination.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(io.BytesIO(archive)) as source:
        for entry in source.infolist():
            parts = PurePosixPath(entry.filename).parts
            if ".." in parts or entry.filename.startswith("/"):
                raise ValueError(f"Unsafe archive path: {entry.filename}")
            if not parts or parts[0] != "CaveStory" or entry.is_dir():
                continue
            relative = Path(*parts[1:])
            if not relative.parts:
                continue
            target = destination / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            with source.open(entry) as incoming, target.open("wb") as outgoing:
                shutil.copyfileobj(incoming, outgoing)
    required = ["Doukutsu.exe", "data/npc.tbl", "data/Stage/Start.pxm", "data/Stage/Start.tsc", "data/Head.tsc"]
    missing = [name for name in required if not (destination / name).is_file()]
    if missing:
        raise ValueError(f"Freeware data is incomplete: {missing}")
    (destination / "data-source.json").write_text(json.dumps({
        "url": URL, "sha256": digest,
        "edition": "Cave Story original freeware, Aeon Genesis English translation",
        "files": len(list((destination / "data").rglob("*"))),
    }, indent=2) + "\n")
    print(f"Verified freeware data: {destination}")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--archive", type=Path, help="Use an already downloaded archive")
    parser.add_argument("--destination", type=Path, default=ROOT / "runtime")
    args = parser.parse_args()
    if args.archive:
        archive = args.archive.read_bytes()
    else:
        request = urllib.request.Request(URL, headers={"User-Agent": "Caverarria freeware setup"})
        with urllib.request.urlopen(request, timeout=60) as response:
            archive = response.read()
    install(archive, args.destination)


if __name__ == "__main__":
    main()
