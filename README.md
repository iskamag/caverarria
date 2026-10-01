# Caverarria

Play Cave Story with your Terraria character in tModLoader. Your movement,
equipment and attacks come from Terraria; the original campaign runs in
[doukutsu-rs](https://github.com/doukutsu-rs/doukutsu-rs).

## Play

Download the alpha from [GitHub Releases](https://github.com/iskamag/caverarria/releases)
and follow the [installation notes](docs/ALPHA.md). Enable the mod, select a
character in **Single Player**, then enter the bundled **Caverarria — The Island**
world or create one with seed **`caverarria`**. First entry downloads and verifies
Cave Story's English freeware data; the engine is included in the mod.

Use Terraria movement and item attacks.

| Key | Action |
| --- | --- |
| **E** | Interact / advance dialogue |
| **R** | Retry from the campaign checkpoint |
| **I** | Terraria inventory; right-click story items to use them |
| **M** | Story map, once acquired |
| **V / C** | Cycle Cave Story weapons |

Cutscene skipping and other key bindings are configurable in Controls.
Terraria's world zoom works; **Settings → Mod Configuration → Caverarria** offers
camera/avatar sizing, campaign music/effect volumes and campaign reset.

Original save points, `/caverarria save` and Save & Exit while alive save progress.
Each world has its own campaign; exiting while dead keeps the previous checkpoint.
Cave Story guns work only inside the campaign.

Mine with pickaxes and build with solid blocks or supported non-solid furniture.
Blocks default to campaign size (2×2 Terraria tiles); **Place Terraria-sized blocks**
uses ordinary 16-pixel blocks. **Save terrain edits** controls persistence.
Digging can bypass story gates. Containers, tile entities, platforms, falling
blocks, hammer reshaping and explosives are not supported yet.

## Limitations

Single player only. Linux checks cover early rooms, dialogue, movement,
save/re-entry and audio. **Full campaign completion, bosses and endings remain
unverified**, as do Windows/macOS and combinations with other mods.
[Report bugs](https://github.com/iskamag/caverarria/issues) with versions,
platform, enabled mods and steps to reproduce.

## Build

Requires Terraria/tModLoader, Rust with `wasm32-unknown-unknown`, and a .NET SDK.

```sh
python3 scripts/setup.py
python3 scripts/install.py
```

Use `--profile /path/to/tModLoader` with the installer for a custom save folder.
`./play.sh` launches an isolated profile.

More: [engine/build details](rust-bridge/README.md),
[runtime and measurements](docs/managed-runtime.md),
[integration roadmap](docs/INTEGRATION.md), [campaign tests](tests/campaign/README.md).

## Credits

Cave Story: **Studio Pixel**. English translation: **Aeon Genesis**.
Terraria: **Re-Logic**.
Runtime: [doukutsu-rs](https://github.com/doukutsu-rs/doukutsu-rs).
Host: [tModLoader](https://github.com/tModLoader/tModLoader).
Managed runner: [WebAssembly for .NET](https://github.com/RyanLamansky/dotnet-webassembly).
File-format reference: [NXEngine-evo](https://github.com/nxengine/nxengine-evo).
Dependency notices and licenses are included in the mod.
