# Host terrain contracts

`bash tests/host_terrain/run.sh` builds the production C# mod sources against the
installed tModLoader assemblies and runs 73 synthetic checks in a private temporary
save directory. It checks native-cell mapping and bounds, script control locks,
block-swap/hammer/explosion policy, legacy and subcell metadata, partial collision masks, ordinary-sized placement
bounds and native pixel grid rendering boundaries, nearest attachable placement targeting within
the selected native cell and ordinary item range, the placed-block metadata round trip (including
same coordinates in different stages), atomic writes, persistence enable/disable and retry metadata baseline restoration,
fresh-game cleanup, and
unmodified ordinary-world hook behavior.

These checks do not establish real pickaxe input, Terraria placement consumption,
item drops, rendered block art, NPC/bullet collision, or a hosted save/re-entry.
Those require an isolated actual client. The portable guest terrain tests cover
the native collision override and guest persistence separately.
