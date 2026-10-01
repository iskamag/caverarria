Run `bash tests/host_audio/run.sh` from the checkout.

This links the exact `CampaignAudio.cs` against the installed FNA assembly and
uses SDL's dummy audio device in a separate, headless process. It checks two
successive stream lifecycles: six queued chunks, playback, pause, resumption by
the host update, and disposal. It also disposes a session on a worker, verifies
that refills stop immediately, and drains its queued release on the main thread
after a new session starts. The old release must leave the new stream playing.
It never opens a game world or reads player saves.
It does not establish speaker playback or full Terraria world-entry behavior.

`CAVERARRIA_LOAD_AUDIO_TEST=1 node rust-bridge/test_wasm.mjs mod/Caverarria/Assets/Engine/caverarria_bridge.wasm`
separately verifies saved native music: create a private checkpoint, destroy the
engine, create another engine, explicitly load (matching the host), and require
nonzero music PCM before its first simulation tick.
