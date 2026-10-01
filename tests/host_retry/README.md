Run `bash tests/host_retry/run.sh` from the checkout. It requires the .NET SDK and
the installed tModLoader located by `scripts/find-tml.py`; it uses tModLoader's
bundled .NET 8 runtime. Build outputs go to `runtime/host-retry-probe`.

This probe compiles the actual mod source and calls `CampaignRuntime.ApplySnapshot`
and `Retry` with an inert engine and synthetic scene snapshots. It reproduces
Terraria respawning before the native checkpoint reload, checks exact checkpoint
HP and death flags, and checks that subsequent snapshots, ordinary transfers,
hidden-but-alive drowning/rescue scenes, and non-game scenes do not restore HP.
Checkpoint restoration also clears Terraria's separated body-part positions,
velocities, rotations and death fade on both native restart and explicit R paths.
It also exercises the real `CampaignPlayer.ProcessTriggers` hook with a dead
player and a running respawn timer: fresh restart-menu selection/confirmation,
button release, and rejection of remote/living players on that capture path.
Capsule checks cover automatic permanent upgrades, replay protection, independent
campaigns, old alpha migration, and player save/load persistence.
It creates a temporary save path and removes it afterward; it does not open a
client, inject controls, or access real player/world/campaign saves.

These checks cover host input capture and snapshot reconciliation. A real gameplay death and retry
is still needed to verify the native script/input path end to end.
