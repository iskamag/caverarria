# Managed WASM diagnostics

These sources execute the real portable Cave Story core and the mod's exact
`WasmEngine.cs` adapter on tModLoader's bundled .NET 8 host. The project references
the mod's pinned **WebAssembly 2.1.0** DLL directly; it installs no other runtime.
The standalone Rust feature probe is separate from actual engine performance.

From the repository root, prepare the pinned DLL and original freeware data:

```sh
python3 scripts/setup-managed-runtime.py
python3 scripts/setup-data.py
python3 -m unittest discover -s tests/managed_wasm -p 'test_*.py' -v
python3 tests/managed_wasm/run_probe.py
python3 tests/managed_wasm/run_core_probe.py
python3 tests/managed_wasm/run_performance_probe.py --frames 300
```

An installed tModLoader and .NET SDK are required. The feature probe also requires
Rust's `wasm32-unknown-unknown` standard library. The real-engine probes default
to `mod/Caverarria/Assets/Engine/caverarria_bridge.wasm`; build a replacement with
`python3 scripts/build-wasm.py` if needed. `--dotnet`, `--module` and `--data` select
other local inputs. A `--data` directory needs the adjacent original `Doukutsu.exe`
as inert resource input. For untouched data in a separate directory, run
`scripts/setup-data.py --destination runtime/probe-freeware`, then supply
`--data runtime/probe-freeware/data`.

The scripts build only this isolated harness and run bounded subprocesses. They
never launch or control a Terraria client. Generated assemblies, intermediate
files, the small Rust module, reports, saves and package caches stay beneath
`runtime/managed-wasm-probe/`. Published historical reports remain in
[evidence/](evidence/); normal reruns do not overwrite them.

`Program.cs` checks host imports without WASI, f32/f64 arithmetic, indirect calls,
overlapping memory copy, memory fill/growth, mutable globals and multiple returns.
It also generates and copies a synthetic RGBA frame. These timings measure the
small fixture rather than Cave Story or Terraria.

`CoreProbe.cs` calls the real engine ABI directly. `AdapterProbe.cs` links the
mod's actual managed adapter and interface. Both verify original stages,
three rendered layers and nonzero original 48 kHz stereo PCM. The adapter writes
an original `Profile.dat` to an isolated directory, disposes the engine, reopens
it, and checks saved stage, health, equipment, flags, items and weapons. Ordinary
native controls drive the short introduction; no warp/event/flag shortcuts are
used by these two probes. Zero guest imports and the 256 MiB private memory limit
are checked using the same validator as the portable build.

`PerformanceProbe.cs` separates `Send`, three `CopyPixels` calls and `ReadAudio`
using 100 warmup and 100–500 measured frames per scene. Start, Shelter and held
Shelter dialogue are isolated with explicit debug warp/event commands at
320×240, 426×240 and 640×360. Mean, percentiles, allocations, collections and
working set are reported. This is diagnostic fixture evidence, not a campaign
playthrough. It excludes Terraria simulation, GPU uploads and the audio device.

Recorded results identify the tested module by SHA256. The latest saved
core/adapter reports used module
`bba853ac8ba13e832c4660a818b5d259ee1514b60426b4e8bdc3bc63b61faee7`
on .NET 8.0.0. Earlier before/after performance reports used baseline
`023bbc730403cd49d7eb13e23b70edc939ed2ddd0d6714256a27cbad08a6656a`
and optimized `fbc3e74d8e9876c7c27f4d21f4f89a0d041133d67a501e555d7ebc31875cad03`.
At 426×240, measured total engine work fell from 6.44/6.89/6.82 ms to
3.00/3.24/2.94 ms for Start/Shelter/dialogue. The 640×360 Shelter sample had
scheduling noise. Check the recorded hashes before comparing a later build.

The historical baseline module is **not bundled with these tests**. A new
before/after comparison requires separately obtaining the desired baseline and
passing it with `--module /path/to/baseline.wasm`; the same runner and fixture
settings can then test the current packaged module. The historical feature
report also includes the WACS 0.16.14 comparison. WACS source, binaries and
dependencies are not required or included in the published harness.

The selected runner compiles WASM to CIL and uses the existing CLR JIT. Its
`UnmanagedMemory` uses the CLR allocator; no custom native WASM library is loaded.
Memory addresses must be reacquired after calls that may grow guest memory.
The harness disposes runtime instances to release that memory. It does not
establish full campaign completion, Windows/macOS behavior, or a controlled
native-versus-WASM performance ratio.
