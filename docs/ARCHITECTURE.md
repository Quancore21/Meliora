# Architecture

Meliora is a SwiftUI app around a NativeAOT-compiled C# emulator core. The app owns installation, settings, JIT, input, the Metal view and the memory/thermal policy; the core owns the Switch CPU/GPU emulation, the Vulkan renderer, the emulated compositor and the adaptive controller.

This document describes how the pieces fit together and where the Meliora-specific logic lives. It is a map, not a replacement for reading the code: file paths and function names are included so the description can be verified.

## At a glance

```
┌─────────────────────────────────────────────────────────────────────┐
│ Swift app (src/MeloNX/MeloNX)                                       │
│  SwiftUI views · settings · file pickers · JIT (StikDebug)          │
│  MetalView / CAMetalLayer · controllers · in-app log view           │
│  MelioraGovernor: 0.5 s timer, memory/thermal telemetry, trims      │
└───────────────────────────────┬─────────────────────────────────────┘
                                │ C ABI (@_silgen_name ↔ UnmanagedCallersOnly)
┌───────────────────────────────▼─────────────────────────────────────┐
│ C# emulator core (src/Ryujinx.*), shipped as                       │
│ Ryujinx.Headless.SDL2.dylib (NativeAOT, ios-arm64)                  │
│  Ryujinx.Headless.SDL2: entrypoint, exports, render loop            │
│  Ryujinx.HLE: Switch kernel/services, SurfaceFlinger, frame pacing  │
│  Ryujinx.Graphics.Gpu/Vulkan: GPU emulation, caches, memory governor│
│  Ryujinx.Common: thread QoS, logging, configuration                 │
└───────────────────────────────┬─────────────────────────────────────┘
                                │ Vulkan
┌───────────────────────────────▼─────────────────────────────────────┐
│ MoltenVK (libMoltenVK.dylib) → Metal → Apple GPU                    │
└─────────────────────────────────────────────────────────────────────┘
```

SDL2 provides input, audio and window plumbing. FFmpeg libraries provide video decoding where the emulated title uses it.

## Repository map

| Path | Role |
| --- | --- |
| `src/MeloNX/MeloNX` | Swift app: UI, settings, game library, JIT setup, Metal view, log view, `MelioraGovernor`. |
| `src/MeloNX/MeloNX/App/Core/Ryujinx` | Swift side of the core bridge (`RyujinxBridge.swift`), launch/lifecycle (`Ryujinx.swift`), memory governor, Metal HUD. |
| `src/Ryujinx.Headless.SDL2` | Native entrypoint, `[UnmanagedCallersOnly]` exports, `WindowBase` render loop, `AdaptivePerformance`, `MelioraFramePacing` consumers. |
| `src/Ryujinx.HLE` | Switch HLE kernel/services, including `SurfaceFlinger` and `MelioraFramePacing`. |
| `src/Ryujinx.Graphics.Gpu` | GPU emulation, `MemoryGovernor`, texture caches (`Image/AutoDeleteCache.cs`), buffer backing. |
| `src/Ryujinx.Graphics.Vulkan` | Vulkan renderer, MoltenVK initialization, pipeline cache storage, GPU diagnostics. |
| `src/Ryujinx.Common` | Logging, configuration, `ThreadQoS`, shared interop. |
| `distribution/ios` | NativeAOT publish helpers (`xc-compile.sh`, `get_dotnet.sh`) and prebuilt library checksums. |
| `src/MeloNX/MeloNX/Dependencies` | Prebuilt MoltenVK, FFmpeg and SDL2 binaries. |
| `.github/workflows/ios.yml` | macOS CI: build core, build app, package and verify IPAs, publish release on tags. |
| `docs` | This documentation. |

## Layers

### Swift app

The app is the only place that knows about iOS-specific lifecycle: signing, JIT, file access, the Metal layer, touch/controller input, background tasks and the in-app log. It does not emulate the Switch and does not own the render loop.

The app applies settings by writing environment variables before the core reads them. `MeloNXApp.setupEnvironment()` runs once before MoltenVK first initializes; `LaunchGameHandler.configureEnvironmentVariables()` re-applies per-game settings at every launch. This split exists because MoltenVK reads parts of its configuration only when its dylib loads, while `MELIORA_*` values are read by the core at game start.

### C ABI bridge

The core is compiled as a dylib and exposes C entry points with `[UnmanagedCallersOnly(EntryPoint = "...")]` in `src/Ryujinx.Headless.SDL2/Program.cs`. The Swift side declares the same symbols with `@_silgen_name` in `RyujinxBridge.swift`.

Meliora-specific exports:

| Export | Direction | Purpose |
| --- | --- | --- |
| `meliora_memory_configure(enabled, headroomBytes)` | Swift → core | Enables the governor and sets the free-memory headroom. |
| `meliora_memory_report(availableBytes)` | Swift → core | Reports `os_proc_available_memory()` so the caches can shrink. |
| `meliora_memory_trim()` | Swift → core | Requests an emergency trim (also triggered by an iOS memory warning). |
| `meliora_tex_cache_bytes()` | core → Swift | Returns the last known texture-cache size for telemetry. |
| `meliora_save_caches(force)` | Swift → core | Queues a pipeline-cache save; the render thread performs it safely. |

General lifecycle exports (`pause_emulation`, `stop_emulation`, `get_game_info`, controller functions and so on) follow the same pattern. The bridge deliberately avoids passing Swift objects into the core; values are copied into native structs and strings are marshalled explicitly.

### C# core

`Program.cs` parses the command line, creates the emulation context and the window, and calls into `WindowBase`. The core is a normal Ryujinx build with Meliora hooks; compatibility work still lives upstream.

The render loop is in `WindowBase.RunLoop`. It runs on the core's render thread and is the only caller of `AdaptivePerformance.OnIterationEnd`.

### Graphics stack

The GPU emulation targets Vulkan. On iOS, MoltenVK translates Vulkan to Metal. `MVKInitialization` applies the Meliora-controlled MoltenVK options (prefill command buffers, resume-lost-device) and reads the effective configuration back for the `[Meliora] gpu:` log line.

The memory type is normally `DedicatedMemory` on MoltenVK because the shared-memory probe does not report unified memory. `MELIORA_UNIFIED_MEMORY=1` overrides that decision for experiments; the choice is visible in the `memoryType=` field of the GPU log line. The override was tested and rejected: it did not reduce the peak footprint and kept the GPU thread busier. Release builds keep `DedicatedMemory`.

## Startup and game launch

1. The app starts and calls `MeloNXApp.setupEnvironment()`: environment variables, JIT setup, `RyujinxBridge.initialize()`.
2. The user launches a game. `LaunchGameHandler` re-applies per-game environment variables, including `MELIORA_QOS`, `MELIORA_ADAPTIVE`, `MELIORA_TARGET_FPS`, `MELIORA_SMOOTH`, the MoltenVK options and the dual-mapped JIT flag.
3. `Ryujinx.start(with:)` starts `MelioraGovernor` and calls the native `main_ryujinx_sdl` entrypoint.
4. The core initializes the renderer and enters `WindowBase.RunLoop`, which calls `AdaptivePerformance.ResetSession()` once for the new game.
5. On exit or a native stop callback, the app stops the governor, releases the Metal layer and the touch state, and the core tears down the renderer and caches.

Settings are applied per launch. The *Target 30 FPS* toggle therefore affects the next game, not the running one. *Smooth Pacing* is reserved for live changes and is not wired end-to-end in 0.0.5.

## Render loop

Simplified from `WindowBase.RunLoop`:

```
ResetSession()
loop while active:
    wait for pause
    if WaitFifo():
        start = now
        ProcessFrame()                 // emulate one guest frame
        fifoBusyTicks = now - start    // GPU-thread busy proxy
    while ConsumeFrameAvailable():
        PresentFrame()                 // present to the Metal layer
        presented++
    PipelineCacheStorage.ProcessPendingSave()
    AdaptivePerformance.OnIterationEnd(fifoBusyTicks, presented)
    update pacing/status
```

`AdaptivePerformance` uses present-to-present intervals as the frame time the user actually sees, and `fifoBusyTicks` as a proxy for GPU-thread saturation. The proxy is not Metal GPU utilization: it measures how much of the iteration the GPU thread spent processing FIFO commands. That is why the controller requires both a sustained slow median and a high busy fraction before stepping down, and why CPU-bound slowness is left alone.

## Adaptive render scale

The controller is a static class in `src/Ryujinx.Headless.SDL2/AdaptivePerformance.cs`. Its full specification and constants are in [PERFORMANCE.md](PERFORMANCE.md); the architecture summary is:

- A 96-sample window of present-to-present intervals is evaluated every time it fills (~1.6 s at 60 FPS).
- The decision signal is the median, not the 95th percentile. A step down needs three consecutive windows with `median > 1000 / targetHz × 1.05` and `gpuBusy ≥ 75%`.
- A step down multiplies the scale by 0.95; the floor is 0.90× the scale the game started with, never below 0.5× absolute. `MELIORA_FLOOR` can set an absolute floor.
- A step up happens after five clean windows (median at target, `gpuBusy < 50%`), at most once per 8 seconds. CPU-bound slow windows and stall spikes do not reset the up-streak.
- The only state written to the emulator is `GraphicsConfig.ResScale`; no profile is touched and the controller allocates nothing.

The controller is reset per game, so the starting scale and warmup belong to that game and not to a previous session in the same app process.

## Memory governor

The memory path has two sides.

### Swift side (`MelioraGovernor.swift`)

- A 0.5-second timer on a utility queue samples `os_proc_available_memory()`.
- The headroom is 10% of physical memory, clamped to 512 MiB–1 GiB. It is sent to the core with `meliora_memory_configure`.
- While the governor is enabled, every positive sample is forwarded with `meliora_memory_report`.
- A proactive trim fires when available memory drops below 3/4 of the headroom (~575 MB on an 8 GB iPhone), with a 10-second cooldown, and only while the texture cache is above its 96 MiB normal floor. At the floor the request is skipped because it cannot shrink the cache. The 0.0.4 field log showed the process sitting below 900 MB free for a minute before the kill, so the threshold moved earlier.
- An iOS memory warning triggers an immediate trim without the cache-size gate.
- Every 10 seconds the app logs footprint, the worst availability over the window, texture-cache size, thermal state, low-power mode and FPS.
- Every 10 minutes, and when the app enters the background, the app asks the core to save the pipeline cache.

### Core side (`MemoryGovernor.cs`, `AutoDeleteCache.cs`)

- `MemoryGovernor.Configure` enables the governor and clamps the headroom to 256 MiB–2 GiB.
- `GetTextureCacheLimit` computes the deficit `headroom - available + (currentCacheSize - sizeAtSample)` and returns a limit that gives the deficit back, clamped by the cache's own floor and ceiling.
- `RequestEmergencyTrim` marks a trim request and schedules a non-blocking optimized GC. The next cache check halves the cache, but never below half of the normal floor (48 MiB when the normal floor is 96 MiB).
- `AutoDeleteCache` sets the unconstrained budget from the GPU's reported memory (50%, clamped 512 MiB-4 GiB), applies a 30% process budget on devices with 8 GiB or less of .NET-visible memory, and uses a normal emergency floor of at most 96 MiB. The 96 MiB value replaced 192 MiB in 0.0.5 because trims below 192 MiB were guaranteed no-ops in the field log. A real memory warning may now target 48 MiB.
- `GetTextureCacheLimit` also stores the current cache size for `meliora_tex_cache_bytes`, which is what `texCache=` reports.

The governor does not try to account for every allocation. It controls the caches it owns and reports what it sees; the rest of the footprint (buffers, the managed heap, MoltenVK/Metal allocations) is outside its direct control.

## Frame pacing

`MelioraFramePacing` is the single source of truth for the composition target:

- `MELIORA_TARGET_FPS`, default 60, clamped 15–240.
- Re-read at every game launch by `SurfaceFlinger`'s constructor, so the *Target 30 FPS* toggle applies without restarting the app.
- `TargetHz` is used by `SurfaceFlinger`, the render-loop status update and the adaptive controller's budget; `TicksPerFrame` is the matching duration used for pacing arithmetic.

`SurfaceFlinger` runs its own composer thread. Its spin tail adapts to the measured wake lateness, between 0.2 ms and 2 ms. `MELIORA_SPIN_US` pins the spin for experiments. `MELIORA_SMOOTH` lets the composer follow a changed target live, but the app does not re-apply settings mid-session, so the *Smooth Pacing* switch is a no-op in 0.0.5.

## Thread QoS

`ThreadQoS` assigns Darwin QoS classes by thread role:

| Role | Class |
| --- | --- |
| `GpuMain`, `GameMainThread` | UserInteractive |
| `RenderLoop` | UserInteractive when more than two performance cores, otherwise UserInitiated |
| `GuestThread`, `Composer`, `Audio`, `DiskCacheLoader`, `ShaderCompile` | UserInitiated |
| `CounterQueue`, `AsyncWork`, `BackgroundTranslate` | Utility |

The number of performance cores is read from `hw.perflevel0.physicalcpu`. UserInteractive slots are capped at that number; excess requests are downgraded to UserInitiated. QoS is applied inside each thread's entrypoint, not from the creating thread, and short-lived pool work restores the default class afterwards. `MELIORA_QOS=0` disables the whole mechanism. `ResetSession` clears the per-session slots and the game-main-thread claim.

## Pipeline cache

The Vulkan pipeline cache is stored per game under the app data directory (`cache/vulkan-pipelines`), keyed by the input path. Saves are requested from the Swift side and executed by the render thread, because `vkGetPipelineCacheData` must be externally synchronized with pipeline creation. The cache is saved on shutdown, on background, every 10 minutes, and when the cache is unregistered.

## Diagnostics

The core writes plain-text lines to the emulator log; there is no network telemetry. The formats and their meaning are documented in [PERFORMANCE.md](PERFORMANCE.md#reading-the-log) and summarised in the [README](../README.md#diagnostics). Logging must never break emulation: optional diagnostics are wrapped so a failure to read a value cannot take down the renderer.

## Build and packaging

The core is published for `ios-arm64` with .NET 8 NativeAOT by `distribution/ios/xc-compile.sh`; Xcode builds the Swift app and embeds the dylib and the prebuilt MoltenVK/FFmpeg/SDL2 binaries. The CI workflow performs both steps, packages two unsigned IPAs (standard and extended-VA), verifies bundle ID, architectures, libraries and entitlements, and uploads them. See [Compile.md](../Compile.md) and [RELEASING.md](RELEASING.md).

## Invariants

These are the rules the Meliora-specific code is expected to keep:

- The adaptive controller writes only `GraphicsConfig.ResScale`. It does not change graphics settings, profiles, clocks or thermals.
- The controller allocates nothing and does no per-frame logging.
- A step down requires a sustained median slowdown *and* a busy GPU thread; a spike alone is never enough.
- The starting scale is the ceiling; the floor is never above the starting scale.
- Platform callbacks into the core are safe from any thread; work that must run on the render thread is queued (`PipelineCacheStorage`).
- Logging never throws into the render loop.
- Settings that depend on environment variables are applied at game launch; changing them mid-session does not silently take effect.
- Compatibility work stays in the upstream core; Meliora does not maintain a separate game list.
