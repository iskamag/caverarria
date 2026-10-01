Run `bash tests/host_retry/run.sh` from the checkout. It requires the .NET SDK and
the installed tModLoader located by `scripts/find-tml.py`; it uses tModLoader's
bundled .NET 8 runtime. Build outputs go to `runtime/host-retry-probe`.

This probe compiles the actual mod source and calls `CampaignRuntime.ApplySnapshot`
and `Retry` with an inert engine and synthetic scene snapshots. It reproduces
Terraria respawning before the native checkpoint reload, checks exact checkpoint
HP and death flags, and checks that subsequent snapshots, ordinary transfers,
hidden-but-alive drowning/rescue scenes, and non-game scenes do not restore HP.
It creates a temporary save path and removes it afterward; it does not open a
client, inject controls, or access real player/world/campaign saves.

These checks cover host snapshot reconciliation. A real gameplay death and retry
is still needed to verify the native script/input path end to end.
