# Portable engine packaging

Caverarria's managed runtime dependency is Ryan Lamansky's
[WebAssembly for .NET](https://github.com/RyanLamansky/dotnet-webassembly), NuGet
**WebAssembly 2.1.0**, licensed under Apache-2.0. Its packaged net8.0 DLL compiles
WASM to CIL using the existing .NET JIT; it has no package dependencies or
custom native engine library. The full upstream license is included at
`mod/Caverarria/lib/WebAssembly.LICENSE` and travels with the mod.

This runner was selected to meet the portable managed-library requirement.
`WasmEngine` calls `Compile.FromBinary<CaveWasmExports>` with an empty import
table. The compiler emits CLR methods, then the normal .NET 8 JIT produces
machine code. Its linear memory uses the existing CLR `NativeMemory` allocator.
Scalar memory accesses retain WASM bounds checks; the compiler marks their
helpers for inlining. `CompilerConfiguration` exposes delegate bindings, with
no optional optimizing IR pass. Dynamic code/JIT support is required. WACS was
used only in the comparison probe and is not packaged. There is no controlled
native-versus-WASM performance ratio in the current evidence.

`python3 scripts/setup-managed-runtime.py` downloads the pinned NuGet package,
verifies its SHA256 and the two selected members, then writes the net8.0 DLL
and license to `mod/Caverarria/lib/`. `--offline` requires the verified cache.
This is a build/setup step. The finished mod embeds the DLL so players need no
NuGet download or separate WASM runtime installation.

For the in-game **Develop Mods → Build** compiler, launch tModLoader with
`-unsafe true`; the managed adapter uses pointer access for bounded UTF-8 reads.
The SDK project enables the same option with `AllowUnsafeBlocks`. Standard
namespace imports live in `GlobalUsings.cs` because the in-game compiler does
not generate the SDK's implicit usings. Develop Mods also requires a .NET 8 SDK
on the launch process's PATH; these are developer requirements, not player setup.

| Artifact | SHA256 |
| --- | --- |
| WebAssembly 2.1.0 nupkg | `f1f893988f13a58957eb7d6361ff085f84954aa5ed7739a21db1bcd2a1fe9238` |
| lib/net8.0/WebAssembly.dll | `7904c9db84ed069bc735ae33b86add27e4680dd3e3cc2d74f8bbbc4a6d936fb1` |
| LICENSE/LICENSE | `b9fd7c9a677750c930f827b8ff6e3fad93bac91f04ca54df2e3cca68971dcef2` |

The isolated execution probe, commands and limitations are in
[tests/managed_wasm/README.md](../tests/managed_wasm/README.md), with published
machine-readable results in [evidence/](../tests/managed_wasm/evidence/).
It executes a current Rust
wasm32-unknown-unknown module in tModLoader's actual .NET 8 host and tests bulk
memory, indirect calls, float arithmetic, host imports, memory growth and
mutable globals. Its synthetic raster timings are separate from real engine
or campaign completion evidence.

`python3 tests/managed_wasm/run_core_probe.py` additionally executes the
real Rust core and links the mod's exact managed adapter on tModLoader's .NET
8.0.0 host. The module has zero declared imports. The original unmodified 399
freeware files and adjacent `Doukutsu.exe` are enough: the adapter extracts PE
resources inside WASM memory. The probe checks original graphics/PCM and a real
`Profile.dat` disk save followed by disposal, constructor reload and retry.
The observed introductory scene engine work stays below 16.67 ms per frame;
Terraria integration and full campaign completion are separate verification.
See [core-evidence.json](../tests/managed_wasm/evidence/core-evidence.json) and
[adapter-evidence.json](../tests/managed_wasm/evidence/adapter-evidence.json).
These latest saved reports identify the later silent-audio fix module
`bba853ac8ba13e832c4660a818b5d259ee1514b60426b4e8bdc3bc63b61faee7`.

## Verified performance

The engine module used for the recorded optimized measurements is SHA256
`fbc3e74d8e9876c7c27f4d21f4f89a0d041133d67a501e555d7ebc31875cad03`.
At the package audit, the source asset, Rust build output, optimized probe module,
built mod, and installed default-profile mod all contained this same module and
the DLL pinned above. Later builds can change the module; use their build manifest
and the hash recorded with each measurement to identify the tested artifact.
The runner stayed unchanged during the renderer and host drawing fixes.

[performance-baseline.json](../tests/managed_wasm/evidence/performance-baseline.json) records the older
`023bbc730403cd49d7eb13e23b70edc939ed2ddd0d6714256a27cbad08a6656a` module.
[performance-optimized.json](../tests/managed_wasm/evidence/performance-optimized.json) records the optimized module and matching adapter
source hash. Both use 100 warmup and 300 measured frames per scene. At 426×240:

| Scene | Engine work before | Engine work after | After p95 |
| --- | ---: | ---: | ---: |
| Start | 6.44 ms | 3.00 ms | 3.89 ms |
| Shelter | 6.89 ms | 3.24 ms | 3.82 ms |
| Shelter dialogue | 6.82 ms | 2.94 ms | 3.61 ms |

Engine work includes a normal tick with external player fields, software
drawing, JSON parsing, three RGBA copies, 800 stereo PCM frames, and save-revision
polling. These are diagnostic scene fixtures, not campaign completion. They
exclude Terraria simulation, GPU uploads and the audio device. Send still
allocates about 41–50 KiB per frame. Pixel count affects cost; the 640×360 Shelter
sample had scheduling noise, with 6.60 ms mean and 12.73 ms p95.

The actual Linux tModLoader client at 1280×720 under Xvfb measured 59.99 campaign
updates and 47,991 submitted audio frames per second in the optimized three-second
window. Its engine averaged 2.30 ms, drawing 1.67 ms, image uploads 0.24 ms and
audio refill 0.53 ms. The earlier client window measured 26.59 calls per second,
5.01 ms engine and 19.55 ms drawing. See
[performance-client-before.json](../tests/managed_wasm/evidence/performance-client-before.json),
[performance-client-after.json](../tests/managed_wasm/evidence/performance-client-after.json) and
[portable-performance-verification.json](../tests/managed_wasm/evidence/portable-performance-verification.json). Original audio loop capture
is in [terraria-fna-audio-optimized-verification.json](../tests/managed_wasm/evidence/terraria-fna-audio-optimized-verification.json).

Use the recorded optimized evidence when assessing these historical figures.
The client-after artifact's `performance.update` counter is inconsistent with
the measured update cadence and is excluded from these figures. The short client
sample establishes this Linux scene's cadence; boss fights, a full campaign,
other enabled mod combinations, and Windows/macOS remain separate checks.

Reproduce the standalone comparison against the packaged module:

```sh
python3 tests/managed_wasm/run_performance_probe.py --module mod/Caverarria/Assets/Engine/caverarria_bridge.wasm --frames 300
python3 tests/managed_wasm/run_core_probe.py --module mod/Caverarria/Assets/Engine/caverarria_bridge.wasm
```

These commands build the isolated harness and use tModLoader's bundled .NET 8
host. Generated files stay under `runtime/managed-wasm-probe/`; the published
sources and historical evidence are under `tests/managed_wasm/`. They do not
launch or control the graphical client. A historical baseline module is optional
and must be supplied separately with `--module`; it is not bundled with tests.

## Distribution notices

Run `python3 scripts/package-licenses.py` before packaging. It reads the locked
Cargo graph for `wasm32-unknown-unknown`, with default features disabled and
`portable` enabled. `--offline --check` verifies the already generated files
without modifying the mod. Optional CPAL, ImGui, SDL, native windowing, WASI and
JavaScript runner packages are rejected if they enter that graph.

`mod/Caverarria/ThirdPartyInventory.json` records the 66 third-party package
versions, selected features, lockfile checksums, source provenance and license
file hashes. `ThirdPartyNotices.txt` indexes 126 complete original license and
notice files under `licenses/rust/`, including doukutsu-rs's unchanged MIT notice,
encoding_rs's BSD notice and unicode-ident's Unicode license. The graph includes
build-time proc macros; its presence does not claim every package is embedded in
the module. Rust target distribution notices are preserved separately because
the precompiled standard library is outside Cargo's dependency graph.
Registry license files and VCS metadata are compared with the original crate
archives after validating their Cargo.lock checksums.

The erasable, rc-box and pelite-macros registry packages omit their workspace
license files. The script retrieves their exact recorded upstream revisions,
checks pinned SHA256 values and caches the texts for offline regeneration. A
missing license for any other third-party package fails packaging. The existing
`lib/WebAssembly.LICENSE` remains intact. Caverarria-owned integration code is
MIT licensed; the packager copies the root `LICENSE` into the mod as
`LICENSE.txt`. Third-party notices contain no freeware game assets.
