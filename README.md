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
**+ / -** zoom the camera in/out in sharp pixel steps. Camera zoom and player
appearance size are also adjustable in Mod Configuration; the default draws
your character 50% larger while keeping the movement/collision box unchanged.
Story items occupy Terraria inventory slots and use their original campaign
actions. Life Capsules immediately grant permanent character health, at ten
Terraria HP per original HP; retrying a checkpoint does not grant the same
upgrade twice. Cave Story guns fire only inside the campaign.

Campaign progress lives in
`<tModLoader save folder>/Caverarria/Campaigns/<campaignId>/Profile.dat`.
The ID is stored in the world's `.twld` file: characters entering the same world
share its campaign, and copying a world with its ID also shares that progress.
Original save points, `/caverarria save`, and Save & Exit while alive write the
campaign state. Exiting while dead keeps the previous checkpoint. Retry loads
that checkpoint, or starts a new game if none exists. Your Terraria character
continues to use its ordinary `.plr`/`.tplr` files; permanent capsule upgrades
are recorded there. Campaign and character saves are separate files.

## Alpha scope

Linux checks cover normal world entry with a selected character, preservation
of appearance/equipment and outside health, the introduction and dialogue,
movement/jumping, Start Point → First Cave, save/re-entry and original audio.
Short actual-client samples reached 60 Hz after the renderer and drawing fixes.

**Full campaign completion, boss progression and every ending remain unverified.**
Multiplayer and world editing are not supported. Windows/macOS, other
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
- [doukutsu-rs](https://github.com/doukutsu-rs/doukutsu-rs): campaign runtime.
- [tModLoader](https://github.com/tModLoader/tModLoader): Terraria mod host.
- [WebAssembly for .NET](https://github.com/RyanLamansky/dotnet-webassembly): managed
  WASM-to-CIL compiler, packaged with its Apache-2.0 license.
- [NXEngine-evo](https://github.com/nxengine/nxengine-evo): independent reference for
  the original file formats.

Exact upstream notices and dependency licenses travel inside the mod. Freeware
game data is downloaded separately on first use.
