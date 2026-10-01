# Caverarria playable alpha

Bring a Terraria character into Cave Story's original world, with its maps,
dialogue, enemies, story items, music and saves. This alpha is ready for early
play and feedback. A full playthrough and every ending have not been verified.

## Install from Steam Workshop

Subscribe to [**Caverarria (Alpha)** on Steam Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3811267098). Start tModLoader, open
**Workshop → Manage Mods**, enable Caverarria and return to reload the mods.
Choose **Single Player**, select a character, create a world with the seed
**`caverarria`**, and enter it from the ordinary world list.

## Install the GitHub ZIP

You need Terraria and tModLoader. The download includes the mod and a fresh
campaign world; players need no Rust toolchain or .NET SDK.

1. Close tModLoader and extract the ZIP.
2. Put `Caverarria.tmod` in your tModLoader save folder's `Mods` directory.
3. Optionally copy **both** `Worlds/Caverarria.wld` and `Worlds/Caverarria.twld`
   into its `Worlds` directory. Keep the two files together. Preserve an existing
   campaign world instead of replacing it; create another with seed `caverarria`.
4. Start tModLoader, enable Caverarria in **Workshop → Manage Mods**, and reload.
5. Choose **Single Player**, select a character and enter **Caverarria — The Island**.
   The included template is a Classic world. You can also create a world with
   seed `caverarria` instead of copying the template.

Default tModLoader save folders:

| Platform | Save folder |
| --- | --- |
| Windows | `%USERPROFILE%\Documents\My Games\Terraria\tModLoader` |
| Linux | `~/.local/share/Terraria/tModLoader` |
| macOS | `~/Library/Application Support/Terraria/tModLoader` |
| Steam Flatpak on Linux | `~/.var/app/com.valvesoftware.Steam/.local/share/Terraria/tModLoader` |

Custom save directories, cloud storage and older tModLoader branches can use
other folders. See the [official tModLoader usage guide](https://github.com/tModLoader/tModLoader/wiki/Basic-tModLoader-Usage-Guide).
The alpha has been tested on Linux with tModLoader 2026.7.3.0.

On first entry, Caverarria downloads and checks the original English freeware
archive. Allow that download to finish. Original game data stays under
`tModLoader/Caverarria`; each world keeps its story progress under
`Caverarria/Campaigns/<campaignId>`. The ZIP contains no freeware data or player
save. The engine and managed runner are already inside the mod.

## Play

Use Terraria movement, jumping, equipment and item attacks.

| Key | Action |
| --- | --- |
| **E** | Interact or advance Cave Story dialogue |
| **R** | Retry from the campaign save |
| **I** | Original story inventory |
| **M** | Original story map, once acquired |
| **V / C** | Cycle story weapons |

Keys can be rebound in tModLoader controls. Original save points work;
`/caverarria save` also saves campaign progress. Save & Exit, then choose the same
character and world to return. Campaign health is temporary inside the world;
the integration preserves the character's outside health, permanent maximum
health, appearance and equipment when leaving.

## Known alpha limitations

- **Single player.** Multiplayer is not supported.
- Linux menu entry, selected-character preservation, introduction/dialogue,
  movement and jumping, Start Point → First Cave, save/re-entry, original audio
  and short 60 Hz performance samples have been exercised.
- Later progression, boss fights and all endings still need complete hosted
  playthroughs. A blocked route or interaction is an alpha bug to report.
- Windows, macOS, other character/world modes and combinations with other mods
  have not received the same play checks. Performance can vary with resolution,
  hardware and enabled mods.
- Authored terrain is protected. World editing is planned.

## Report a problem

Use [GitHub issues](https://github.com/iskamag/caverarria/issues). Include the
alpha and tModLoader versions, platform, enabled mods, stage/map, control or
item used, steps to reproduce, expected result and actual result. Add the error
and a relevant `tModLoader-Logs/client.log` excerpt if available. A screenshot or
short clip can help with movement, collision or audio problems.

## Credits

Cave Story: **Studio Pixel**. English freeware translation: **Aeon Genesis**.
[doukutsu-rs](https://github.com/doukutsu-rs/doukutsu-rs) runs the original campaign;
[tModLoader](https://github.com/tModLoader/tModLoader) hosts the Terraria player;
[WebAssembly for .NET](https://github.com/RyanLamansky/dotnet-webassembly) runs the
portable core. NXEngine-evo was used as a file-format reference.

The mod contains `ThirdPartyNotices.txt`, `ThirdPartyInventory.json`, the original
upstream license texts and the managed runner's Apache-2.0 license. Original
freeware data is downloaded separately rather than redistributed in this ZIP.
