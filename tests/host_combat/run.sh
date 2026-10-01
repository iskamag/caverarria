#!/usr/bin/env bash
set -euo pipefail
repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
tml_dir="$(python3 "$repo_root/scripts/find-tml.py")"
probe_dir="$repo_root/runtime/host-combat-probe"
dotnet build "$repo_root/tests/host_combat/HostCombat.csproj" --nologo -v:q \
    -p:TmlDirectory="$tml_dir" -p:BaseIntermediateOutputPath="$probe_dir/obj/" -p:OutputPath="$probe_dir/bin/"
"$tml_dir/dotnet/dotnet" "$probe_dir/bin/HostCombat.dll"
