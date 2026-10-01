Run `bash tests/host_combat/run.sh`. This compiles the production host source and
checks guest-owned proxy lifetimes and actor identity across host/guest slot reuse,
zero-local-HP multipart boss targeting, native symmetric horizontal
bullet extents, vertical bounds and closed vulnerability phases. It does not
exercise actual host projectiles or prove campaign boss completion.

Also checks host/native camera alignment at three screen sizes (including odd
dimensions), at every supported integer pixel scale from 2 through 6. These
are coordinate checks; live rendering remains a separate verification.

Production player draw hooks are exercised against a synthetic campaign avatar,
menu rendering with a stale engine, previews sharing the avatar's slot number,
exit/restoration and noncampaign worlds. The checks assert draw-cache scale
changes only for the active world avatar. Vanilla world zoom at 100%, 150% and
200% is checked against the campaign's whole-pixel camera scaling.
