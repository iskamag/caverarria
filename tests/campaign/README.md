# Original campaign audit and live-playthrough requirements

Run the data audit from the repository root:

```sh
python3 tests/campaign/inspect_campaign.py --json /tmp/caverarria-campaign.json
python3 -m unittest discover -s tests/campaign -p 'test_*.py' -v
```

The inventory reads the freeware executable's actual stage table, decrypts the
original TSC event scripts, validates all PXM/PXE/PXA assets, and resolves stage
transfers against their destination events. The downloaded English freeware
contains 95 stages, 98 ordinary event scripts, and 1,826 unique events. Credit.tsc
uses a separate credit-script language and is preserved in the source hashes.

These checks establish that every requisite asset parses and authored stage
transfers resolve. They do **not** establish that the Terraria avatar can play it. Native
engine tests that warp to rooms, invoke events, inject items or set flags are
adapter diagnostics and must remain separate from completion evidence.

## Source details that affect integration

- The original stage table names stage 59 `lounge`, whereas the archive names
  its files `Lounge`. Asset lookup must reproduce Windows case insensitivity.
- CentW, EggX, EggX2, Gard, Ring2 and Sand contain duplicate event definitions.
  Both open-source engines keep the first definition. Replacing it with the
  later definition changes progression.
- Little event 500 and River event 110 contain malformed numeric fields. The
  inventory reports them and models doukutsu-rs's deliberately permissive
  byte-based numeric reader. Twelve dangling branch/NPC references also exist
  in the original data. They are reported separately from broken stage transfers.
- Cave Story tile collision boxes are centered on `(tile_x * 16, tile_y * 16)`.
  Pixel positions in PXE and TSC use the same coordinate space. A Terraria host
  that puts collision blocks at those coordinates as their **left edges** shifts
  the terrain by half an original tile.
- NPC table flags are combined with PXE flags before interaction/death tests.
  PXE alone is not a complete inventory of which actors can trigger an event.
- Script commands control the player: `MOV`, `MYB`, `MYD`, `UNI`, `HMC`, `SMC`,
  `KEY`, `PRI` and transfers have to be reflected by the hosted Terraria player.
  `KEY` locks input while actors continue; `PRI` freezes the simulation. Under
  `KEY`, damage/contact must follow the original protection rules.
- Combat must preserve original actor/room identity, weak points, invulnerability,
  projectiles, actor-spawned child entities, stage-boss components, and original
  death callbacks. Applying damage without native death callbacks loses gates.
- Currents, drowning, conveyors, slopes, moving platforms, spikes, and destructible
  blocks change movement across the authored routes. Air Tank and the Booster
  equipment bits need their real behavior in the host.

## Early live-input route

`early_route.json` records target actor coordinates and observable gates from
Start Point to Egg Corridor. Event IDs identify expected original behavior;
they are never instructions to invoke an event from a test.

The first mandatory combat gate is the First Cave door enemy, original NPC type
59 at `(55 * 16, 9 * 16)`. Its death sets PXE flag 301. Simply granting Polar Star
or walking to the door cannot open Mimiga Village. After the door is defeated,
the radio event Shelter 502 transfers directly into Mimiga Village event 302;
that scene moves the player to tile `(13, 22)`.

Silver Locket is Pool 300, the Toroko encounter starts through Barr 290/291 and
the native angry Toroko behavior, and Arthur's Key is Cemet 300. Balrog in the
Shack is optional because the original conversation permits declining the fight.
The test should still include a real fight to exercise the combat bridge.

`navigation.py` finds geometric corridors for the hosted avatar, including the
original slope shapes and tile offsets. It accepts a live snapshot so script
tile changes participate in replanning. A path is a navigation suggestion;
running physics and the player's actual inputs must prove each jump.

`physics_navigation.py` adds grounded support intervals and a jump model for the
actual three-cell Terraria projection. It checks the whole avatar against walls,
clips exposed floors under ceilings, walks across stairs, and searches distinct
arrival positions and velocities. Launch approaches include acceleration and
braking. A geometric waypoint above a ledge is no longer enough to request jump.
The Start Point regression reproduces the premature ceiling collision from
center `(160,129)` and finds a later launch near the island's left edge.
Spike contact is separate from physical collision. Tile attributes `0x42` and
`0x62` use the original fixed four-pixel player sensor against a four-by-three
tile sensor. Stationary NPC type 211 uses its actual original bounds against
the fixed two-pixel central player sensor. Original-map predictions load those
actors from PXE/NPC data; live snapshots use currently active actors. Supports
exclude spike contact positions, and walking approaches and swept jump/drop
arcs reject contact rather than treating spikes as supporting walls.

The first real Cave death was NPC211 at `(496,224)`, with damage five and bounds
six on each side. The recorded fall from center `y=214.95834` to `216.15` crossed
its original contact threshold `y>216`. First Cave contains no tile spikes, so
checking tile attributes alone would miss that actor. The regression reproduces
that sensor crossing and tests latched braking on the nearby ledge. Other moving
enemies, falling spikes and combat remain the caller's live responsibilities.

Generate a reviewable prediction without touching the client:

```sh
python3 tests/campaign/physics_navigation.py --map Start --start 160 129 \
  --goal 160 48 --json /tmp/start-support-route.json --svg /tmp/start-support-route.svg
```

Use `--recording path/to/actual.jsonl.gz` to estimate acceleration, ground friction,
running speed, held-jump velocity, and air gravity from observed 60 Hz host frames. Idle
gaps, locked scripts, water, boosters, and knockback are excluded. The fallback
profile uses the latest host source values; the jump hold limit remains inferred
until a current unobstructed arc has been recorded. Character equipment can
change movement, so a profile from another character or build is provisional.
An open gzip recording can be fitted using its complete JSON prefix; the profile
provenance marks that prefix explicitly. The actual Start traversal in
`campaign-normal-60hz.jsonl.gz` measured jump speed `2.3950002`, gravity
`0.10833333`, acceleration `0.03333330`, and ground friction `0.06666670` in Cave
Story pixels per host frame. It held jump for four and eight frames, so the
sixteen-frame maximum remains inferred. These are motion measurements, not
campaign completion evidence.

`NavigationController(goal, profile).buttons(state)` returns only ordinary
`left`, `right`, and `jump` controls with short frame counts. The caller owns the
live driver and records/executes those suggestions. The helper contains no
client, event invocation, position setter, or native commands. Its dry model
declines water/current/Booster arcs; moving NPC platforms, combat, and scripted
transfers still require live handling. Its offline controller test proves only
that the controller reaches the exit **in its model**. Arrival requires a
grounded player within five pixels with horizontal speed below `0.1`. On the
final support the controller releases movement at the estimated stopping
distance and keeps releasing until stopped, allowing the caller to interact
without coasting past the actor or briefly re-accelerating near a ledge.

`playthrough.py` provides `RecordingTerraria`, a strict wrapper around the live
Terraria driver. It records full observed states and normal controls, rejects
engine debug arguments, and checks an evidence hash chain. Audit a recording
with `python3 tests/campaign/playthrough.py /tmp/run.jsonl.gz --ending normal`.
The audit fails when the authored route or full terminal ending is absent.
It also requires native combat consequences for the route's story actors and
an observed HP decrease for each requisite stage boss. Record short firing inputs
during fights, including state before and after hits. Actor removals while firing
are labeled separately because scripts can also remove actors. These observations
must be assessed together with the original death/progression gates.
The accepted retry control is the host's ordinary R/load action. A retry may
return to an earlier observed stage; other stage changes must resolve to original
TSC transfers. The hash chain detects altered records, and is not a signature
that independently authenticates the running client.

`build_story_routes.py` validates 237 later-game observations against the original
scripts and generates `bad_route.json`, `normal_route.json`, and `best_route.json`.
Each route starts after the 23 steps in `early_route.json`. Static actor positions
are targets for planning; moving actors must be approached at their live position.
The best route contains the exact conversation choices, original inventory use,
and mandatory combat gates. These specifications do not count as completed runs.

## All three endings

| Route | Original trigger | Completion evidence |
| --- | --- | --- |
| Bad | Choose Yes in EggX2 500, which sets flag 960; then speak to Kazuma in Oside 400, reaching 401 | Oside 401 plays the escape and displays `- The End -`, then reaches an infinite `WAI9999` hold. It uses neither `CRE` nor `XX1`. Observe the ending text/event in the live host. |
| Normal | Defeat Undead Core, escape Balcony, run Island 100 (`XX1 0`), then Fall 100 | Island destruction cinematic, Kazuma rescue, transition to stage 0 event 100 and original credits. |
| Best | Preserve Curly, restore her memory, receive Iron Bond and Booster v2.0, enter Sacred Ground, defeat Heavy Press and every Ballos phase | Island 110 (`XX1 1`), Ballo2 500, Fall 120 (sets flags 2000 and 1460), then stage 0 event 100 and the original best-ending credits. |

Blcny1 event 199 contains `CRE` **after** an `END`; this unused debug event must
not count as any ending.
The completed normal/best credits create the native `THANK YOU` actor 360 in
stage 0 and enter an infinite `WAI9999` hold in event 1100/1200 respectively.
Upstream deliberately never decrements 9999, so waiting for script `Ended`
would reject every genuine completion. Starting `CRE` alone is still insufficient.

The best route requires the following original sequence:

1. Do not take Booster v0.8 from the wounded Professor in MazeB 501. That event
   sets flag 834, suppressing the Tow Rope actor in Almond. This is an irreversible
   branch of the ordinary campaign, not a bit to repair from a test.
2. Before starting the Core fight, collect Tow Rope from Almond 240 at tile
   `(71, 25)`. The actor is hidden after flag 834 and unavailable once the fight
   starts. After the fight and Curly's rescue, use the rope on her through 307.
   This sets flags 835 and 836.
3. Enter the Waterway Cabin (Pixel). Rest with Curly via 250/251 to set flag 1440;
   inspect the computer (200/202) and notebook (221/222) to learn the draining
   procedure (flag 1441). Use the bed again to drain her through 253 (flag 1442),
   then talk again through 254 (flag 1443). When asked whether to leave her,
   choose **No**, branching to 256: flags 836 and 1444 become set and 1440 clears.
   Pass the Waterway's downstream trigger only after this recovery. River 220/221
   sets irreversible flag 851 if Curly is still undrained; Ironhead victory then
   refuses to place her in Plantation. On the successful branch, Stream 1003
   clears carrying flag 836 and sets Plantation appearance flag 1042.
4. After the Core, return to Arthur's House and receive Booster v2.0 in Pens1 651
   (item 23). Its acquisition and equipment are separate actions.
5. Find Curly in Plantation at tile `(110, 103)` and learn about her amnesia from
   the nearby actor. Follow the original Ma Pignon conversation chain in Storage
   (Mapi), inspect Mushroom Badge to set flag 1563, win the real Ma Pignon fight,
   and obtain item 34 through Mapi 500.
6. Feed Ma Pignon to Curly via Cent 320/322 (removes item 34 and sets flag 1045).
   Talk to her again through 324 to receive Iron Bond (item 39, flag 1046).
7. Undead Core death event Ring3 1000 checks item 23 and flag 1046; event 1002
   sets flag 1393, which unlocks the Prefab House on the collapsing Balcony.
8. Enter Prefa2, descend into Hell1, attach Curly (Hell1 500), traverse Hell2 and
   Hell3, defeat Heavy Press, enter Ballo1 and defeat Ballos's human and native
   multi-part forms. Ballo1 1000/1001 lead to Island 110 only after native deaths.

## Required end-to-end evidence

A completion run starts from a fresh campaign save and drives the tModLoader
client with ordinary movement, aim, inventory selection, interaction and dialogue
buttons. Record requested controls and observed host/native state, stage changes,
combat hits and deaths, item/equipment changes, story events, saves/reloads and
terminal ending frames. `debugCommandsUsed` must remain zero through that run.

Use separate fresh runs, or real in-game saves before the relevant forks, for
the three endings. Verify save and reload in the live host while preserving the
world, original flags, items, weapons/equipment, and the real Terraria player's
inventory. A native save test alone does not cover the hosted player's save.

Required fights include both event-numbered ordinary actors (Igor, Curly,
Balrog, Toroko, Puu Black, Misery, Doctor, Ma Pignon and Ballos's human form) and
native stage bosses (Omega, Balfrog, Monster X, Core, Ironhead, Sisters, Undead
Core, Heavy Press and Ballos). Some are optional on a given ending route; the
integration must still retain their behavior and exercise each through gameplay
or explicitly labeled diagnostic coverage.

Do not accept event invocation, warping, injected flags/items, direct native
damage calls, or an isolated rendering screenshot as end-to-end completion.
World editing remains a roadmap feature; script-driven tile changes are already
part of the original campaign and must work now.

The skip-Booster jump in Labyrinth B is a concrete movement calibration gate.
Its left launch ledge at row 14 spans tiles 4 through 6 and the landing ledge spans
14 through 16. At scale 3 and a 20-pixel Terraria avatar width, the equal-height
gap between valid center positions is about 118.7 Cave Story pixels. Prove it
through held jump and directional input in the live client. Geometric clearance
alone does not establish the necessary airtime; the original engine reduces
gravity while jump is held.
