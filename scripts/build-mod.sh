#!/usr/bin/env bash
set -euo pipefail
repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
tml_dir="$(python3 "$repo_root/scripts/find-tml.py")"
profile_dir="${CAVERARRIA_BUILD_PROFILE:-$repo_root/runtime/build-profile}"
mkdir -p "$profile_dir/Mods" "$repo_root/runtime/build"
python3 "$repo_root/scripts/package-licenses.py"
dotnet build "$repo_root/mod/Caverarria/Caverarria.csproj" -c Release -p:TmlDirectory="$tml_dir" -p:BuildMod=false --nologo
(cd "$tml_dir" && ./dotnet/dotnet tModLoader.dll -server -build "$repo_root/mod/Caverarria" -eac "$repo_root/mod/Caverarria/bin/Release/net8.0/Caverarria.dll" -savedirectory "$profile_dir" -tmlsavedirectory "$profile_dir/tModLoader" -nosteam)
cp "$profile_dir/tModLoader/Mods/Caverarria.tmod" "$repo_root/runtime/build/Caverarria.tmod"
printf 'Built %s\n' "$repo_root/runtime/build/Caverarria.tmod"
