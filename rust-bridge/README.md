# Cave Story runtime bridge

The adapter embeds pinned **doukutsu-rs**, runs its real stage scripts, NPC and boss
simulation, save profiles, map transitions, music and endings, and lets tModLoader
supply ordinary player kinematics and equipment. Campaign mining and placement
use persistent guest map overrides shared with Terraria collision.

Portable build: `python3 scripts/build-wasm.py`. This creates
`mod/Caverarria/Assets/Engine/caverarria_bridge.wasm`, an import-free
`wasm32-unknown-unknown` module with a 256 MiB maximum private linear memory.
The host executes it in the managed runtime and supplies the original assets.
The first-use installer downloads the original archive; the module preserves
the original engine's 60 Hz timing mode, three rendering layers, and synthesizers.

Portable exports use 32-bit pointers into exported `memory`:

- `cave_alloc(length)` and `cave_free(pointer,length)` allocate host transfer buffers.
- `cave_fs_put(pathUTF8Z,bytes,length,save)` copies an asset (`save=0`) or profile
  (`save=1`) into the case-insensitive memory VFS; returns 0 or -1.
- `cave_extract_original(exeBytes,length)` extracts the original PE's Organya,
  bitmap and stage-table resources into the asset VFS using the upstream parser.
- `cave_fs_list(save)` returns a UTF8Z JSON array. `cave_fs_get(pathUTF8Z,save)` and
  `cave_fs_len(pathUTF8Z,save)` expose each file for host persistence.
- `cave_fs_revision(save)` returns an i64 counter advanced by save writes,
  truncation and removal. The host persists changes after original `<SVP` events.
- `cave_set_time(seconds:i64)` supplies the initial RNG seed and profile timestamp.
- `cave_create(dataUTF8Z,saveUTF8Z,width,height)` uses the preloaded VFS. The two
  directory strings remain in the common ABI and are ignored by the portable core.

Creation resumes an existing original profile and starts a new campaign when none
exists. The explicit `new` command resets the live campaign. File pointers remain
valid until the corresponding file is modified; copy them before the next call.

`cave_audio(handle,frames)` returns signed 16-bit little-endian interleaved stereo
PCM at 48000 Hz, synthesized by the original Organya/PixTone implementations.
`cave_audio_rate()` reports the rate; `cave_audio_length(handle)` reports the last
byte count. Frames must be 1–8192; each frame is four bytes. The buffer is reused
by the next audio call. The host queues these samples through Terraria/FNA audio;
sample-count-based fades retain the original five-second duration.
`audio_ready` indicates the mixer is initialized. `audio_trace` reports the original
SoundManager's song/SFX request counts and most recent IDs. These counts describe
engine requests; they do not measure audible output. `audio` accepts
`music_volume` and `sfx_volume` in 0–1 and uses the engine's original mixers.
For silent portable play, send `{"op":"audio","enabled":false}` immediately
after creation. This clears pending and active playback, suppresses future
enqueue, and returns `audio_ready:false`; original song IDs and profile/music
restore commands still advance. Reenabling restarts the current and remembered
music without replaying discarded effects.
Live Organya loops indefinitely even when optional OGG support is disabled.
A handle must be used by exactly one thread. Only one handle may be live at a time
within a module instance because upstream graphics buffers are global.

Native diagnostic builds remain available with
`cargo build --manifest-path rust-bridge/Cargo.toml --release` for original CPAL
playback, or `--no-default-features --features pull-audio` for host PCM playback.
Use a separate `--target-dir` while a running process holds an earlier library.
Set `CAVERARRIA_AUDIO=0` for silent native tests. These diagnostic libraries are
not required by the packaged portable mod.

`cave_command(handle, UTF8_JSON)` returns UTF-8 JSON owned by the handle. The return
pointer remains valid until the next command. `cave_pixels(handle, 0, 1 or 2)` returns
RGBA bytes, valid until the next command or destruction. Layers 0 and 1 use the
snapshot's `viewport` dimensions; layer 2 uses `ui_viewport` dimensions. The UI
canvas is at least 320×240 so world zoom cannot crop original dialogue or menus.
Only rendering temporarily uses those UI dimensions; world simulation and camera
clamping keep the actual `viewport`. Layer 0 is
the opaque background and native entities. Layer 1 is transparent foreground
terrain, water and world effects. Layer 2 is HUD, dialogue, menus, fade and credits.
tModLoader draws its real player and projectiles between layers 0 and 1, then its
own interface before layer 2. Snapshots advertise `render_layers:3`. Bytes use
**straight alpha**, so use `NonPremultiplied`.
`cave_destroy(handle)` releases all resources. `cave_last_error()` reports creation
failure. Inspect every command's `ok` field.
Ticks, explicit snapshots, resize and scene changes render the current frame.
Combat, tile, volume and save mutations return updated state and are combined
into the next tick's render; request `render:true` for an immediate image.
Opaque/transparent atlas pixels and integer, untinted sprite copies use direct
paths while scaled, tinted, fractional and flipped sprites retain the sampler.

Normal command is `{"op":"tick","controls":0,"player":{"x":160,"y":128,
"vx":0,"vy":0,"width":6.67,"height":14,"grounded":true}}`.
Positions are player centers in original Cave Story pixels; velocities are pixels
per engine tick. One tick advances exactly one engine tick. The bridge selects
the engine's built-in 60 Hz timing mode and the host ticks it each 60 Hz update.
Snapshots report `timing_hz:60`. It preserves vanilla
Terraria player movement in normal play and reconciles native collision, wind,
water, moving NPCs and script movement through `force_position`/`force_velocity`.
Locked cutscenes and the Ironhead swimming sequence use the original scripted
player motion. In Ironhead mode the bridge ignores supplied host position,
velocity, ground and water contacts; the original swim controller integrates
once per tick while HP and body bounds remain synchronized. Native current tiles
retain the original velocity cap after their forces, without capping ordinary
Terraria motion outside those tiles.
`controls` uses upstream replay flags: left 1, right 2, up 4, down 8,
map 16, inventory 32, jump 64, shoot 128, next weapon 256, previous weapon 512,
pause 1024, confirm 2048, skip 4096. This retains real dialogue choices and
inventory/teleporter interfaces.
Native corner HP/weapon widgets are suppressed for the external player; the
original centered AIR countdown remains visible for drowning information.

Commands: `new`, `load`, `retry`, `save`, `death`, `snapshot`, `tick`, `hit`,
`tile_hit`, `terrain_edit`, `audio`, and `resize`. `resize` accepts native canvas `width`/`height`
(160–1920 by 120–1080), preserves the current scene and cached textures, and
returns pixels at the new size. All previously returned pixel pointers become
invalid. The host reallocates its RGBA buffers/textures using returned `viewport`
and `ui_viewport`, copying `width * height * 4` bytes for the respective layer.
Tick `weapon` selects an acquired original weapon by WeaponType id;
`controls` shoot then runs its unchanged native projectiles, XP, ammo, recoil and
terrain interactions. Changing to Spur resets its XP once, as native weapon
cycling does; holding the same gun preserves its charge/ammo. Explicit `weapon:0`
keeps the native weapon inactive while ordinary Terraria gear is held.
`player.jump_started` reports a genuine host jump launch and plays original sound
15 once; jump input alone can activate boosters without producing that sound.
`tile_hit` `{x,y,width,height}` routes arbitrary Terraria
projectile collisions through the original Polar Star tile-collision routine,
including destructible snack tiles and their native effects.
`terrain_edit` `{epoch,stage,x,y,solid}` edits one native map cell (not a host
subtile). All fields are required. Stale epochs, wrong stages and out-of-bounds
cells return `terrain_edit_accepted:false`; valid edits return `true`. Empty edits
have collision attribute 0, placed blocks 0x41. They bypass original tile art;
the host draws placed block material on the same cell. Original authored tile
indices remain intact, and subsequent script mutations do not erase player edits.
`Terrain.json` in the campaign user VFS stores overrides immediately, independently
of checkpoints. Retry/load/room transfer preserve edits; `new` clears all edits.
A hit is `{"op":"hit","id":4,"boss":false,"damage":3,"epoch":1,
"generation":1}`. It uses native invulnerability, shared boss life, damage
feedback, death events, drops, and flag progression. NPC slot generations prevent
a delayed hit applying to a replacement enemy. Epoch increments on scene reload.

Snapshots contain `viewport`, `ui_viewport`, `camera`, `stage`, `player`, `npcs`, `bosses`,
`weapons`, `items`, `bullets`, `flags`, `script`, `credits`, and the 95-entry
`stages` table. `bullets` are the original weapon projectiles, including their
damage, age and position.
`map` contains raw tile indices, the 256-entry authored tile attribute table,
`cell_attributes` (effective per-cell collision in row-major order), and
`terrain_edits` (`{x,y,solid}` for the current stage) when the content hash changes.
Script tile edits and player overrides change its `revision`. Host projection
must use `cell_attributes`, since edited cells do not reserve authored tile IDs. Preserve the
previous map if omitted. Cave Story tile coordinates are **centers**: tile `(x,y)`
starts at `(x*16-8,y*16-8)`. NPC bbox values `left/top/right/bottom` are extents
from its center. `player.life_delta` describes native script healing/life changes
after host injection; original script max-life increases are in `player.max_life`.
Native injuries retain their original effects, shock and weapon XP loss but are
returned as `player.pending_damage_raw`. The host applies these through real
Terraria damage handling, then calls `death` only when the Terraria player dies.
Booster 0.8/2.0 retain original fuel and directional activation; snapshots expose
`booster_active`, `booster_fuel` and `booster_direction` (0 none, 1 up, 2 left,
3 right, 4 down). The host suppresses ordinary gravity only during active boost.
`native_support` reports an actual rideable/solid NPC contact; jumping there uses
the original jump impulse and refills booster fuel.

`warp`, `event`, `flag`, and `external:false` exist for campaign probes. They are
debug commands, not substitutes for traversing and completing the actual game.

`python3 rust-bridge/test_mechanics.py` checks focused adapter mechanics against
private temporary original-TSC fixtures. It checks booster fuel/activation,
platform jumping, deferred lethal injury, audio requests/volume, and resize state
preservation, plus separate dialogue layering and AIR display without duplicate
corner widgets, Ironhead motion ownership, and bounded native currents. It does
not establish full campaign completion or combat feel.

`node rust-bridge/test_wasm.mjs` runs the actual portable module with no imports,
host-fed original data, memory PE extraction, intro rendering, original PCM,
profile writes, resize and a frame timing sample. It is an adapter smoke test.
Set `CAVERARRIA_SILENT_AUDIO_TEST=1` to check cleared queued/active audio, 200
unplayed effect requests, silent song/profile restoration, and reenabling without
stale effects followed by a fresh original PixTone sound.
Set `CAVERARRIA_UI_VIEWPORT_TEST=1` to check independent world/UI dimensions at
high zoom, actual original weapon LV/XP pixels, complete original dialogue
rendering beyond the world buffer's edge, and restoration of world dimensions.
This uses private TSC fixtures with audio disabled; it does not prove live host
interaction or campaign progression.
Set `CAVERARRIA_AUDIO_CORPUS=artifacts/wasm-audio.pcm` to collect all samples from
three complete original ACCESS loops and verify the musical envelopes, exact
mute behavior and genuine PixTone jump sound. This checks synthesized PCM;
device buffering and audible playback are verified by the Terraria host.

`python3 rust-bridge/audio_loop_probe.py --output artifacts/native-audio.wav`
records actual CPAL output into a private temporary Pulse sink for three complete
original ACCESS loops, saves stereo PCM as WAV, and compares complete repeated
compositions. The original song bytes and timing are recorded alongside waveform
correlations. Existing live clients and default device routing remain untouched.
