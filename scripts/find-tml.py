#!/usr/bin/env python3
"""Find the installed tModLoader directory, without modifying any game files."""
from pathlib import Path
import os
import sys

def find_tml():
    candidates = [os.environ.get('TML_PATH', '')]
    for steam in [Path.home()/'.steam/steam', Path.home()/'.local/share/Steam', Path.home()/'.var/app/com.valvesoftware.Steam/.local/share/Steam']:
        candidates.append(str(steam/'steamapps/common/tModLoader'))
        libraries = steam/'steamapps/libraryfolders.vdf'
        if libraries.exists():
            import re
            for value in re.findall(r'"path"\s*"([^"]+)"', libraries.read_text()):
                candidates.append(str(Path(value)/'steamapps/common/tModLoader'))
    for candidate in candidates:
        if candidate and (Path(candidate)/'tModLoader.dll').exists():
            return str(Path(candidate).resolve())
    raise SystemExit('tModLoader not found. Set TML_PATH to its installation directory.')

if __name__ == '__main__':
    print(find_tml())
