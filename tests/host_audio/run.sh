#!/usr/bin/env bash
set -euo pipefail
repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
tml_dir="$(python3 "$repo_root/scripts/find-tml.py")"
probe_dir="$repo_root/runtime/host-audio-probe"
dotnet build "$repo_root/tests/host_audio/HostAudio.csproj" --nologo -v:q \
    -p:TmlDirectory="$tml_dir" -p:BaseIntermediateOutputPath="$probe_dir/obj/" -p:OutputPath="$probe_dir/bin/"
ln -sfn "$tml_dir/Libraries/Native/Linux/libFAudio.so.0" "$probe_dir/bin/libFAudio.so"
ln -sfn "$tml_dir/Libraries/Native/Linux/libSDL2-2.0.so.0" "$probe_dir/bin/libSDL2.so"
# A separate audio device; this does not touch a live Terraria client or saves.
SDL_VIDEODRIVER=dummy SDL_AUDIODRIVER=dummy LD_LIBRARY_PATH="$tml_dir/Libraries/Native/Linux${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}" \
    "$tml_dir/dotnet/dotnet" "$probe_dir/bin/HostAudio.dll"
