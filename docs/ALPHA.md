# Caverarria playable alpha

Bring a Terraria character into Cave Story's original world, with its maps,
dialogue, enemies, story items, music and saves. 

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

See the [official tModLoader usage guide](https://github.com/tModLoader/tModLoader/wiki/Basic-tModLoader-Usage-Guide).

On first entry, Caverarria downloads and checks the original English freeware
archive. Allow that download to finish. Original game data stays under
`tModLoader/Caverarria`; each world keeps its story progress under
`Caverarria/Campaigns/<campaignId>`.

## Credits

Cave Story: **Studio Pixel**. English freeware translation: **Aeon Genesis**.
[doukutsu-rs](https://github.com/doukutsu-rs/doukutsu-rs) runs the original campaign;
[tModLoader](https://github.com/tModLoader/tModLoader) hosts the Terraria player;
[WebAssembly for .NET](https://github.com/RyanLamansky/dotnet-webassembly) runs the
portable core. NXEngine-evo was used as a file-format reference.
