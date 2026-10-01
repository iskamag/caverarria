# Caverarria

The target is a playable tModLoader mod putting a real Terraria player inside the
original Cave Story campaign. Cave Story's authored maps, dialogue, inventory
gates, enemies, boss fights, saves, scripted sequences, and endings remain live.
Normal control, equipment, appearance, and attacks belong to Terraria. World
editing is a later feature.

## Runtime decision

NXEngine-evo and doukutsu-rs were inspected. doukutsu-rs is the chosen runtime:
it has a headless backend, preserves the campaign simulation, and can be stepped
as a Rust library without a separately scheduled game process. Upstream versions
are recorded in `upstream.json`. This is an integration of the existing game
runtime, not a campaign reimplementation from dialogue extracts.

The distributable engine is a zero-import `wasm32-unknown-unknown` module compiled
to .NET CIL by the managed WebAssembly runtime. Original assets and per-world save
files are copied into its virtual filesystem by the mod. Original executable
resources are parsed in WASM memory; no executable is launched. A bounded,
checksum-verified first-use download installs freeware data under tModLoader's
user data folder.

## Ownership

* Terraria updates the player using its real movement and item systems.
* The mod projects original solid tiles into protected Terraria collision tiles.
  One Cave Story pixel corresponds to three Terraria world pixels; an authored
  16 pixel tile occupies three by three Terraria tiles.
* doukutsu-rs owns maps, flags, story items, dialogue, NPC AI, bosses, and scripted
  changes to maps. Its built-in 60 Hz mode matches Terraria's update cadence.
* The external player position is supplied to the engine during ordinary play.
  Story locks, scripted movement, teleports, and special movement modes can
  temporarily return control to the campaign. The next snapshot synchronizes the
  actual Terraria player to the resulting position.
* Enemy hitboxes are represented by Terraria combat proxies. Player attacks
  deliver damage to the native NPC or boss routine; they do not replace the
  native enemy AI. Native damage and death state must propagate to Terraria.
* Rendering retains Cave Story's art, animation, camera, and dialogue. A world
  layer is composed before the Terraria player; a foreground and interface layer
  is composed afterward.
* The original sound manager synthesizes Organya songs and PixTone effects and
  processes the real script, weapon and actor sound events. Campaign playback
  follows Terraria's music and sound sliders. Terraria background music and
  ambience are suppressed while the campaign is active; ordinary Terraria item
  sounds still belong to the host.
  PCM playback uses Terraria's existing FNA device, with short queued buffers and
  main-thread refill notifications; the portable engine has no CPAL audio backend.
* Marked Terraria worlds appear in the ordinary world list and accept the
  selected Terraria character. Each world owns an independent native campaign
  save under `tModLoader/Caverarria/Campaigns/<campaignId>`. Campaign health
  changes the character's effective health inside that world; player-file saves
  retain outside health and permanent maximum health. Normal worlds and existing
  Cave Story saves remain separate. The convenience launcher uses an isolated
  profile.

## Verification contract

Compilation, a room importer, or execution of an ending script do not establish
completion. Required evidence includes an actual tModLoader client with the mod
loaded, genuine Terraria input and physics, cross-runtime combat, story item
acquisition, room transitions, save and retry, and a start-to-ending campaign
trace without warping or setting progress flags. Runtime fixtures and room or
boss coverage can use diagnostic operations, but those results must be labeled
separately from a playthrough.

Coverage must include slopes and platforms, spikes, water and air, currents,
scripted movement and locks, boss vulnerability and component damage, inventory
choices, teleporters, native scripted tile changes, and credits. Any unverified
area remains work to do.

## Roadmap

Editable maps will require persistence of terrain changes, rules for story event
anchors after demolition, native NPC collision reconciliation, and safe handling
of alternate entrances to authored encounters. The initial campaign protects
its collision tiles while still honoring the original script-driven changes.
