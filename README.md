# Caverarria

**A playable alpha: your Terraria character in Cave Story's original world.**
Cave Story's maps, dialogue, enemies, bosses, story items, music and saves run in
**doukutsu-rs**. Terraria supplies your character's appearance, movement,
equipment and attacks.

Get the alpha from [GitHub Releases](https://github.com/iskamag/caverarria/releases).
The GitHub ZIP includes
`Caverarria.tmod` and a fresh campaign world. [Installation and player notes](docs/ALPHA.md)
cover installation and the known alpha limitations.

Enable the mod, select a character in **Single Player**, and create a world with
seed **`caverarria`**, or enter the bundled **Caverarria — The Island** world.
First entry downloads and verifies the original English freeware data. Each
world keeps independent campaign progress; Save & Exit and re-enter to resume.
The mod embeds its portable engine and managed runner, so players need no
separate engine installation.

Use Terraria movement and item attacks. **E** interacts or advances dialogue,
**R** retries the campaign save, **I** opens Terraria's inventory, **M** opens the
story map once acquired, and **V/C** cycle story weapons. Original save points remain active.
Terraria's **+ / -** and world zoom slider zoom the campaign in sharp pixel
steps. Camera base scale and player
body size are also adjustable in Mod Configuration. One logical Terraria pixel
is one Cave Story pixel: doubled Terraria texture pixels are normalized 2:1,
and the camera magnifies the shared grid by whole numbers. The default body
uses ordinary Terraria collision dimensions and keeps the previous apparent
size. Furniture and block frames are normalized once before camera movement.
Player scaling applies only to the actual campaign avatar; menus and normal
worlds keep their ordinary appearance. Enemies retain their original shields
and vulnerability windows, including the Graveyard keeper's attack phase.
Story items occupy Terraria inventory slots and use their original campaign
actions. Life Capsules immediately grant permanent character health, at ten
Terraria HP per original HP; retrying a checkpoint does not grant the same
upgrade twice. Cave Story guns fire only inside the campaign.

Version 0.2.0 changes the world grid. The 0.1 campaign save and
placed terrain are reset for the new layout. Terraria characters stay intact.

Use ordinary pickaxes to mine campaign terrain and ordinary solid block items
to build. Each campaign block covers a 2×2 group of Terraria tiles; one placement
uses one item, and mining returns one block (original terrain yields stone).
Enable **Place Terraria-sized blocks** to place ordinary 16-pixel Terraria blocks
instead. Each small block can be mined independently; existing blocks keep their
size when the setting changes. Terraria tiles use two texture pixels per logical
campaign pixel at either placement size. Original solid/slope cells must be mined first
before filling them with small blocks.
Terrain edits affect native enemies and bullets as well as Terraria collision.
By default, edits persist across room changes, checkpoint retries and re-entry.
Turn off **Save terrain edits** in Caverarria configuration to keep new changes
only until retry or leaving the world. Previously saved edits stay intact;
enabling the setting saves your current changes. Digging can
bypass authored gates. Ordinary non-solid furniture, including Slice of Cake,
uses its normal placement footprint and interaction; it follows the same save
setting. Containers, tile entities, platforms, falling blocks, hammer reshaping
and explosives are not supported in campaign worlds yet.

Campaign progress lives in
`<tModLoader save folder>/Caverarria/Campaigns/<campaignId>/Profile.dat`.
The ID is stored in the world's `.twld` file: characters entering the same world
share its campaign, and copying a world with its ID also shares that progress.
Original save points, `/caverarria save`, and Save & Exit while alive write the
campaign state. Exiting while dead keeps the previous checkpoint. Retry loads
that checkpoint, or starts a new game if none exists. Your Terraria character
continues to use its ordinary `.plr`/`.tplr` files; permanent capsule upgrades
are recorded there. Campaign and character saves are separate files.

Open **Settings → Mod Configuration → Caverarria** to adjust camera/avatar
size and separate Cave Story music/effect volumes, or mute just the campaign.
Assign **Cave Story: hold to skip cutscene** in Terraria's Controls settings,
then hold that key to use the original engine's cutscene skip behavior. **X** is
the suggested default; tModLoader leaves new mod keybinds unassigned until you
configure them or use their reset-to-default button.
While playing a Cave Story world, **Reset campaign... → Confirm reset** starts
that world's campaign over. It moves the old checkpoint and terrain edit files to timestamped
`.bak` files beside them before starting fresh. Story progress, terrain, story items and weapons
restart; permanent Life Capsule health stays on your character. Other worlds'
checkpoints are untouched. Reset is unavailable from the main menu or a normal
Terraria world.

## Alpha scope

Linux checks cover normal world entry with a selected character, preservation
of appearance/equipment and outside health, the introduction and dialogue,
movement/jumping, Start Point → First Cave, save/re-entry and original audio.
Short actual-client samples reached 60 Hz after the renderer and drawing fixes.

**Full campaign completion, boss progression and every ending remain unverified.**
Multiplayer is not supported. Building currently supports full solid blocks. Windows/macOS, other
character/world modes and combinations with other mods need playtesting.
Please [report problems](https://github.com/iskamag/caverarria/issues) with the
stage, action, controls, error, versions, platform and enabled mods.

The full-game verification target and editing roadmap are in
[INTEGRATION.md](docs/INTEGRATION.md). The runtime choice and scoped measurements
are in [managed-runtime.md](docs/managed-runtime.md).

## Build from source

Developers need installed Terraria/tModLoader, a Rust toolchain with
`wasm32-unknown-unknown`, and a .NET SDK.

```sh
python3 scripts/setup.py
python3 scripts/install.py
```

Set `TMODLOADER_PATH` if installation detection cannot find tModLoader.
Setup fetches the pinned upstream engine and applies the checked-in integration
patch. The engine builds as a zero-import WASM module and runs through
WebAssembly for .NET's WASM-to-CIL compiler on tModLoader's .NET runtime.
Original executable resources are parsed as data; no custom native engine is
loaded. The [upstream revisions](docs/upstream.json) and dependency license
inventory accompany the build.

`python3 scripts/install.py --profile /path/to/tModLoader` installs into a custom
save folder. `--include-data` copies already downloaded data for an offline first
entry. Reinstallation preserves existing characters, worlds and enabled mods.
`./play.sh` is an optional launcher with an isolated profile.

## Verification and release packaging

```sh
python3 -m unittest discover -s tests/campaign -p 'test_*.py' -v
python3 tests/managed_wasm/run_core_probe.py
python3 scripts/package-licenses.py --offline --check
python3 scripts/package-release.py --version 0.1.0-alpha.1
```

Campaign static tests and standalone engine diagnostics are separate from a
hosted playthrough. [terraria_driver.py](tests/terraria_driver.py) records real
Terraria controls and flags diagnostic commands separately. Room-loading tests
or directly executing an ending script do not prove completion.

The release packager reads the final `runtime/build/Caverarria.tmod` and fresh
world template. It includes installation notes, alpha limitations, credits and
SHA256 manifests, with no downloaded game data or personal saves.

## Credits

- Cave Story by Studio Pixel; English freeware translation by Aeon Genesis.
- Icon artwork: original Cave Story sprites by Studio Pixel and Terraria's Guide
  by Re-Logic, composed on a shared pixel grid.
- [doukutsu-rs](https://github.com/doukutsu-rs/doukutsu-rs): campaign runtime.
- [tModLoader](https://github.com/tModLoader/tModLoader): Terraria mod host.
- [WebAssembly for .NET](https://github.com/RyanLamansky/dotnet-webassembly): managed
  WASM-to-CIL compiler, packaged with its Apache-2.0 license.
- [NXEngine-evo](https://github.com/nxengine/nxengine-evo): independent reference for
  the original file formats.

Exact upstream notices and dependency licenses travel inside the mod. Freeware
game data is downloaded separately on first use; the icon and item thumbnails
include original artwork credited above.
