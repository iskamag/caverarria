Run `bash tests/host_combat/run.sh`. This compiles the production host source and
checks zero-local-HP multipart boss targeting, native symmetric horizontal
bullet extents, vertical bounds and closed vulnerability phases. It does not
exercise actual host projectiles or prove campaign boss completion.

Also checks host/native camera alignment at three screen sizes (including odd
dimensions), at every supported integer pixel scale from 2 through 6. These
are coordinate checks; live rendering remains a separate verification.
