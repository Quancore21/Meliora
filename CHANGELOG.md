# Changelog

## 0.0.6 (2026-11-20)

0.0.6 fixes and the Liquid Glass app icon. Build 25 (2026-11-19) field-tested the logging fix and passed the 10-minute Test B with no false step down. The final build 29 (2026-11-20) was then smoke-tested on device: the 30 FPS target path ran for the first time and held 29–30 FPS with no false step down, and the idle-trim fix held with zero repeated trims. No down-step occurred in any run, so snap-to-floor and the three-decimal step log are still code-verified only.

### Fixed

- **Duplicate render-target resize at the floor.** A down-step from 0.95x produced 0.9025x; the next step landed on 0.9000x and logged `0.90x -> 0.90x`. The 0.25% change recreated the host textures and views again for no visible benefit. The controller now snaps to the floor when the next step would land within 0.01 of it, and step lines print three decimals.
- **Broken `[Meliora]` log lines.** The Swift governor wrote through buffered `print`, and `LogCapture` split each pipe chunk independently, so lines could be merged or lose their timestamp; a chunk with no C#-formatted line was also dropped from the exported log entirely. `stdout`/`stderr` are now line-buffered before the first app write, the governor writes complete timestamped lines, `LogCapture` keeps the incomplete tail of each pipe until the newline arrives, and only reassembled lines are written to the on-disk log. Every complete chunk is exported.
- **Up-steps during memory pressure.** A step up is now blocked while `os_proc_available_memory()` is below 384 MB, with a throttled `step up blocked` line. This does not lower the peak footprint; it prevents the controller from making a bad situation worse.
- **Idle emergency trims.** The Swift governor requested an emergency trim every 10 seconds while free memory stayed below 3/4 of the headroom. At the 96 MiB texture-cache floor the core's emergency path halved a cache already at 94–95 MiB and clamped the result back to 96 MiB, so the request could not shrink anything but still ran `GC.Collect(2)` and logged. The proactive path now skips the request while `texCache` is at the floor. A real iOS memory warning still reaches the core, whose emergency floor is now 48 MiB (half the normal floor), so an actual warning can still free the cache.

### Added

- **Liquid Glass app icon.** `Meliora.icon` (Icon Composer) is now the primary icon; `ASSETCATALOG_COMPILER_APPICON_NAME` is `Meliora` in Archive, Debug and Release. The existing alternate icons in `Assets.xcassets` remain available, and the icon picker groups the new default under Meliora instead of the previous author.

### Field result (build 25, 2026-11-19)

Super Mario Odyssey, reference iPhone 17 (iOS 27.2.1, 8 GB), `DedicatedMemory`:

| Metric | Value |
| --- | --- |
| Length | 10:06 (23:12:36–23:22:42) |
| Peak footprint | 5779 MB |
| Minimum available | 358 MB |
| Texture cache | 192 MiB peak, then 95 MiB |
| Emergency / low-memory trims | 18 / 16 |
| iOS memory warnings | 2 |
| `GPU processing thread is too slow` | 11 |
| FPS median / mean | 56 / 54.6 |
| Adaptive scale | 1.00x for the whole run |
| Thermal | fair → serious at 1:22 |
| Exit | clean (`TerminationRequested` → `ServiceNv Close`) |

This passes the applicable Test B criteria: the run is over 10 minutes, the controller made no false step down, the log has no merged or timestamp-less `[Meliora]` lines, and the session exited cleanly. It did **not** exercise snap-to-floor or the 384 MB up-step guard because the scale never left 1.00x.

### Field result (final build 29, 2026-11-20)

Three short sessions on the same device (SMO 1.3.0, `DedicatedMemory`), covering the new-icon build:

| Session | 30 FPS target | 60 FPS @ 2.00x | 60 FPS @ 1.00x |
| --- | --- | --- | --- |
| Length | ~1:55 | ~0:45 (crashed) | ~3:05 |
| FPS held | 29–30 | 50–54 | 47–62 |
| Adaptive scale | 1.00x throughout | 2.00x throughout (p95 25–50 ms, gpuBusy 74–75%) | 1.00x throughout |
| `budget=` line | 35.0ms (first field log at 30 FPS) | 17.5ms | 17.5ms |
| iOS memory warnings | 2 | 7 | 2 |
| Worst `minAvail` | 517 MB | 260 MB | 463 MB |
| Trims | 0 proactive | 0 (warnings only) | 1 (texCache 102 MiB) |
| Thermal | nominal → fair | fair | fair → serious |
| Exit | clean | crashed | clean |

- **Test C validated.** The 30 FPS path ran for the first time in the field: `adaptive: start 1.00x, floor 0.90x, target 30 fps`, `budget=35.0ms`, 29–30 FPS held, and no false step down despite p95 spikes to 70.7 ms.
- **Idle-trim fix confirmed.** In the 1.00x/60 FPS run the texture cache sat at 92–96 MiB for two minutes while `minAvail` stayed at 470–590 MB, below the ~575 MB proactive threshold. Old builds requested an emergency trim every 10 seconds here; the fixed build requested nothing. The one trim that fired had `texCache 102 MiB` above the floor and trimmed it.
- **Emergency trim floor observed.** After iOS memory warnings during level loads the cache dropped below the 96 MiB floor and the governor stopped there: 78–79 MiB in the 30 FPS run (47 MiB during the load itself), 76 MiB in the 1.00x/60 FPS run, 94 MiB in the 2.00x run.
- **The controller also held 1.00x against sustained slow frames** (`p95` 21–48 ms vs the 17.5 ms budget) by detecting `slow frames but GPU idle (gpuBusy=34%), not touching scale (CPU-bound?)` — the median+gpuBusy gate behaving as designed.
- **2.00x is not viable on 8 GB.** The 2.00x session crashed after ~45 s with seven iOS memory warnings and `minAvail` at 260 MB, no clean exit line. The log ends before the controller could step down, so the new floor-crossing branches (snap-to-floor, three-decimal step logs, 384 MB up-step guard) remain exercised only by code review.
- The Liquid Glass icon renders as the app icon (the IPA primary icon is `Meliora`, verified in the build-29 Info.plist).

### UnifiedMemory test (closed)

A separate 0.0.5 build with `MELIORA_UNIFIED_MEMORY=1` ran for 14:27 on the same device. It peaked at 5725 MB against 5779 MB on the 0.0.6 reference (a 54 MB difference, within noise); the 0.0.5 `[Meliora] mem` lines report 405 MB minimum available, but the last `LogShrink` measured 323 MiB because the tail of the 0.0.5 log was still in the unsent 4 KB buffer. It also logged 61 emergency and 52 low-memory trims (against 18 and 16), 27 `GPU processing thread is too slow` warnings (against 11), and a median `gpuBusy` of 46% against 30% in the equal 9.5-minute window. The device was hotter and the run longer, so FPS and trim counts are not a clean pair; the footprint result is enough to reject the experiment. No paired 0.0.5 control was run because the old route is no longer reproducible. The branch is not merged and the override stays off.

### Still open

- Snap-to-floor, the three-decimal step log and the 384 MB up-step guard have not fired in any field log, because no session stepped down. Verified in code review only.
- The memory peak at large scene transitions; the texture cache is not the cause. Render scales above 1.00x make it worse fast: 2.00x crashed from memory pressure in under a minute on the reference device.
- The `MELIORA_UNIFIED_MEMORY=1` experiment is closed: the 14:27 test run showed no footprint benefit and higher GPU-thread load, so the branch is not merged and the override stays off.

## 0.0.5 (Adaptive Engine rebalance)

Built from the first 0.0.4 field log (17-minute Super Mario Odyssey session on iPhone 17, iOS 27.2). The controller stepped down to its x0.85 floor about 6 minutes in and never climbed back, so most of the session rendered softer than 0.0.3 while the frame rate was already at target; the same log ended abruptly at ~5.4–6 GB, with no termination marker, so an iOS kill is likely but unconfirmed. This release fixes what the log exposed.

### Changed

- **Adaptive render scale is now median-gated.** The decision window still evaluates 96 present-to-present intervals every ~1.6 s, but the sustained median frame time is the gate, not the 95th percentile: single slow frames (shader compiles, allocations) are not fixable by lowering resolution, and the GPU busy fraction is polluted by the same stalls. In the field log a step down fired while the game held a median of 16.7–17.2 ms (a locked 60 FPS) because a compile-spike p95 tripped the gate.
  - A step down now requires the median above the target (+5%) for 3 consecutive windows (~5 s) **and** a GPU thread busy fraction ≥75%: x0.95, floor x0.90 of the starting scale (was 0.85 — the field log showed 0.85 still GPU-bound in the heaviest scenes while visibly soft).
  - The climb back is no longer hostage to hitches: 5 clean windows (~8 s) with an unloaded GPU thread (<50% busy), one step per 8 s, up to the starting scale — always the ceiling. CPU-bound slow windows and stall spikes no longer reset the climb; in 0.0.4 a single spike reset a 16-second streak, so the controller never returned to 1.00x once it had stepped down.
  - New `MELIORA_FLOOR` override (absolute scale, e.g. `0.75`) sets the floor explicitly for experiments.
- **Texture cache emergency floor: 192 → 96 MiB.** `MinGovernedTextureSizeCapacity` formed a hard floor in `MemoryGovernor.GetTextureCacheLimit`, so every emergency trim with the cache below 192 MiB was a guaranteed no-op (`texture cache 130 MiB -> 130 MiB` in the field log). The game runs fine with the cache as low as ~47 MiB, so 96 MiB keeps a safety margin without blocking trims.
- **Proactive memory trim fires earlier**: 3/4 of the headroom (~575 MB on an 8 GB iPhone) instead of 1/2 (~383 MB). The field log reacted only at the very end, when the last shrink measured 265 MB available.
- **Honest memory telemetry.** The 10-second `[Meliora] mem` line now reports `minAvail=` (the worst `os_proc_available_memory` over the window — transient dips were invisible in the instantaneous slice, which showed 1142 MB where the trim check saw 335 MB) and `texCache=` (current texture cache size, exported from the core), so the 0.0.5 logs show what actually holds the memory.

### Field results (2026-11-18/19)

Three SMO sessions on the reference iPhone 17:

| Run | Length | Peak footprint | Minimum available | Outcome |
| --- | --- | --- | --- | --- |
| #1 | 4:33 | 6029 MB | 113 MB | 14 emergency + 9 proactive trims; no termination marker |
| #2 | 5:39 | 5.57 GB | 575 MB | normal exit |
| #3 | 6:11 | 5.52 GB | 621 MB | normal exit |

No false step downs; #1 climbed from 0.90x to 1.00x in 56 s and #3 in 94 s, while 0.0.4 never climbed from 0.85x. The runs are 4.5–6.1 minutes, so the 10–15 minute Test B remains pending.

Known issues found: the `0.90x -> 0.90x` duplicate is a real 0.9025 → 0.9000 resize; Swift `[Meliora]` lines have no timestamp and can be merged; memory pressure at the kingdom transition remains, with the texture cache at only 84–88 MiB at the 6.03 GB peak. The first two are fixed in Unreleased; the memory guard is mitigation, not a cure.

### Notes

- Thresholds are constants at the top of `AdaptivePerformance.cs`. The three 0.0.5 field runs left 0.90x and the +5% gate unchanged; a 10–15 minute run is still needed before treating them as settled.
- A separate test build with `MELIORA_UNIFIED_MEMORY=1` was prepared to measure how much of the footprint is host/device buffer duplication. It is not part of this release; the experiment is now closed (see Unreleased).

## 0.0.4 (Meliora Adaptive Engine)

The first closed-loop performance controller, built on the 0.0.3 baseline logs: Super Mario Odyssey on a pre-heated iPhone holds ~56-57 FPS average but dips to 32-45 FPS in heavy scenes with `ServiceNv Wait: GPU processing thread is too slow` warnings — GPU-bound under thermal throttling. This release attacks exactly that.

### Added

- **Adaptive render scale (`MELIORA_ADAPTIVE`, default ON).** New `AdaptivePerformance` controller hooked into the render loop (`WindowBase`). Every presented frame contributes its present-to-present interval to a 96-sample ring buffer; every ~1.6 s the controller evaluates the 95th percentile frame time against the pacing budget (target FPS, or the game's own median when it is lower — a game that steadily runs 30 FPS is not considered overloaded).
  - Steps down only when frames are slow **and** the GPU thread is saturated (≥70% busy, measured directly around `ProcessFrame`), after 3 consecutive failing windows (~5 s), at most one step per 5 s: x0.95 → x0.90, floor x0.85 of the scale the game started with (never below 0.5x absolute).
  - Steps back up after ~16 s of green windows, one step per 10 s, up to the starting scale — which is always the ceiling. Only `GraphicsConfig.ResScale` is written; no profile is touched.
  - Slow frames with an idle GPU thread are logged as CPU-bound and deliberately ignored: lowering resolution cannot fix those.
  - Warmup: the first ~5 s (shader cache load, JIT) are skipped so the controller never trips on startup storms.
  - Every decision is logged as `[Meliora] adaptive: ...` with p95/median/budget/gpuBusy/scale.
  - All controller state is reset at every game launch (`ResetSession`): a second game in the same app run gets its own warmup and scale capture, and the scale it starts with is the new ceiling — nothing leaks over from the previous session.
- **Runtime frame pacing.** New `MelioraFramePacing` (single source of truth replacing the hard-coded 60): `MELIORA_TARGET_FPS` sets the composition target (15-240, default 60; the *Target 30 FPS* toggle sets 30). The target is re-read at every game launch, so the toggle applies to the next game without restarting the app. `MELIORA_SMOOTH=1` (*Smooth Pacing*) lets the composer follow a changed target at runtime; the toggle itself is not wired to live settings changes yet — the env is applied per game launch (planned for 0.0.5).
- **Adaptive compositor spin.** `SurfaceFlinger` now tunes its spin threshold from the measured wake lateness of the nanosleep path (25% EMA per frame), clamped between 0.2 ms and the old 2 ms cap, instead of a fixed 2 ms guess. `MELIORA_SPIN_US` pins it for experiments.
- **QoS for shader compilation.** New `ThreadQoS.RunWithRole` + `QoSRole.ShaderCompile` (`UserInitiated`): background shader module compilation (`Shader`) and background pipeline creation (`ShaderCollection.CreateBackground*Pipeline`) now run boosted and restore the default class afterwards, so the class cannot leak onto pool threads. The GPU thread stalls until a pipeline is ready, so compilation no longer competes with it from the E-core scheduling class.
- **Effective GPU configuration log.** `[Meliora] gpu: ...` (once per session): renderer name, Vulkan version, memory type, TBDR flag, and the MoltenVK options actually in effect (read back from the dylib). Replaces the idea of an argument-buffers toggle — the device log proved Metal 3 argument buffers are already active, so there is nothing to toggle.
- **Unified memory override (opt-in).** `MELIORA_UNIFIED_MEMORY=1` opts MoltenVK into the `UnifiedMemory` system memory type on Apple GPUs (default off; the effective choice is visible in the `[Meliora] gpu:` line).

### Fixed

- **Texture recompression toggle was dead.** `enableTextureRecompression` never produced the `--enable-texture-recompression` argument (it was commented out), so the per-game switch did nothing. The argument is now passed and the per-game toggle is live again.
- **Idle MTKView display link.** The emulation `MTKView` has no delegate (the emulator presents through the CAMetalLayer itself), so its display link only burned wakeups — the view is now paused.

### Changed

- New settings toggles in Advanced Options: *Adaptive Resolution* (`melioraAdaptive`, ON), *Target 30 FPS* (`melioraTarget30`, OFF), *Smooth Pacing* (`melioraSmooth`, OFF). All are applied at app start and re-applied per game launch.

## 0.0.3 (thread scheduling + experimental options)

Performance pass aimed at base iPhones (2 performance + 4 efficiency cores): explicit CPU thread scheduling instead of QOS_CLASS_DEFAULT everywhere, faster shader cache loading, experimental MoltenVK toggles, and min/avg FPS in the periodic log.

### Added

- **Thread QoS scheduling (`MELIORA_QOS=1`, on by default).** New `ThreadQoS` helper (`Ryujinx.Common.SystemInterop`) tags emulator threads with Darwin QoS classes via `pthread_set_qos_class_self_np` on iOS/macOS:
  - `GPU.MainThread` and the first guest thread of a process (the game's main thread) get `UserInteractive (0x21)` — at most `hw.perflevel0.physicalcpu` threads ever hold that class, further requests downgrade to `UserInitiated`.
  - The backend render loop that drives MoltenVK gets `UserInitiated (0x19)` on 2-P-core devices, `UserInteractive` when there are more than 2 performance cores; all other guest/HLE kernel threads, the SurfaceFlinger composer and the audio manager thread run at `UserInitiated`.
  - Background work (GPU counter queues, `AsyncWorkQueue` disk cache writer, background JIT translation) is pushed to `Utility (0x11)` so it stops stealing performance cores.
  - Without this, the scheduler regularly ran background jobs on the 2 fast cores while the render loop or the game's main thread was bounced to the efficiency cores, showing up as hard framerate drops.
  - SurfaceFlinger no longer sets `ThreadPriority.AboveNormal` while QoS is active: .NET's managed priority calls `pthread_setschedparam` on Darwin, which locks the thread policy and makes `pthread_set_qos_class_self_np` fail with `EINVAL` afterwards.
- **Shader cache loading.** The 8 parallel `ParallelDiskCacheLoader` translation threads now run at `UserInitiated (0x19)` instead of the default class, so shader cache loading stays fast without stealing the interactive slots.
- **Experimental settings card** (Advanced Options) with three toggles:
  - *Thread QoS Scheduling* (`melioraThreadQoS`, default ON) — sets `MELIORA_QOS`.
  - *MVK: Prefill Metal Command Buffers* (`melioraMvkPrefillCmdBuffers`, default OFF) — sets `MVK_CONFIG_PREFILL_METAL_COMMAND_BUFFERS` to `1`/`0`; also mirrored into `vkSetMoltenVKConfigurationMVK` in `MVKInitialization.Initialize()` so it applies even after MoltenVK has already loaded.
  - *MVK: Shader Compression* (`melioraMvkShaderCompression`, default OFF) — sets `MVK_CONFIG_SHADER_COMPRESSION_ALGORITHM` to `4` (zlib)/`0` (none).
  - All three are applied at app start (`MeloNXApp.setupEnvironment`) and re-applied per game launch (`LaunchGameHandler.configureEnvironmentVariables`).
- **FPS telemetry.** The 10-second `[Meliora] mem ...` governor log now also reports `avgFps` and `minFps` sampled every 0.5 s over the window, to make stutter visible in logs.

## 0.0.2 (JIT launch fix)

Fixes the launch hang on iOS 26/27 (TXM) devices with StikDebug 3.2.x: the game stuck on the `Loading ...` overlay (FPS 0.00) because JIT was never correctly enabled.

### Fixed

- **StikDebug URL (TXM devices).** `stikjit://enable-jit` now matches MeloNX 2.7 exactly: on TXM devices (iOS 26/27) only `?bundle-id=` is sent — StikDebug 3.2.2 must run its **built-in** JIT26 script, and the previously appended `script-name=MeloVertex&script-data=...` broke the attach. The custom JIT26 script is still uploaded via `script-data` on non-TXM devices (percent-encoded base64, as upstream; `script-name=MeloNX` only in the LiveContainer case).
- **TXM detection on iOS 27.x.** `ProcessInfo.hasTXM` now first reads the IOKit device tree (`IODeviceTree:/chosen/memory-map` contains `TXM`), the same check MeloNX 2.7 / StikDebug perform, falling back to the old `/System/Volumes/Preboot` check. On iOS 26+, when both checks are inconclusive because the sandbox blocks the directory listings, TXM is assumed present instead of misdetecting the device as non-TXM (`HAS_TXM=0`), which previously pushed the core onto a raw `mmap(PROT_EXEC)` path that cannot work on TXM devices.
- **No JIT allocation before StikDebug attaches.** `initialize_dualmapped()` is no longer called at cold start or in `isJITEnabled()` while the debugger is not attached; a failed `BreakGetJITMapping` there used to poison the session. The dual-mapped cache is now allocated on the launch path, after JIT has been enabled.
- **Breakpoint JIT mapping fallback (C#).** `BreakGetJITMapping` now also falls back to the pub entry point when `brk #0x69` leaves `x0` holding the requested size, a 32-bit value, or a `0x690000e0`/`0xe0000069` marker with different upper bits, instead of passing a bogus address into `vm_remap`.
- **Swallowed crash alerts.** `presentAlert` walks to the top-most presented view controller, so "Meliora Crashed!" dialogs are no longer dropped behind the full-screen `Loading ...` overlay (which used to spin forever with FPS 0.00).
- **Pause during loading.** The scene-phase handler no longer pauses emulation on `.inactive` while the Loading overlay is up — StikDebug's Picture-in-Picture window and notification banners briefly deactivate the scene before the first frame, which used to pause the game before it even started.

### Updated

- `BreakpointJIT.framework` and `libMoltenVK.dylib` replaced with the binaries from the known-working MeloNX 2.7 build; `distribution/ios/prebuilt.sha256` updated and now also covers `BreakpointJIT`.

## 0.0.1 (first Meliora release)

Meliora starts from the MeloVertex 2.4.5 source. This release is about memory behaviour, stability and visibility; it does not change emulation accuracy.

### Added

- **Memory Governor** (Settings > Graphics, on by default). The app reports how much memory iOS still lets it use, and the texture cache shrinks early to keep a safety margin instead of using a fixed budget until the app is terminated. A memory warning from iOS triggers an immediate trim and a heap compaction. When there is plenty of free memory the cache may use the larger budget that 12 GB devices get, so 8 GB devices are no longer capped at 2 GB.
- **Persistent pipeline cache.** The Vulkan pipeline cache (shaders compiled by the driver, here MoltenVK) is saved per game and loaded on the next launch. It is saved when the game stops, when the app goes to the background and every 10 minutes. The file has a checksum and is written atomically; a damaged or rejected file is ignored.
- **Diagnostics.** Every 10 seconds the log gets a `[Meliora] mem ...` line with memory footprint, memory still available, thermal state, low power mode and FPS. The startup log lists which texture compression formats the GPU supports natively. Attach these lines to performance reports.
- In-app "Entitlement" and error dialogs point to the real project repository.

### Fixed (stability pass)

- **Emulator core (C#, NativeAOT).** JIT cache lifetime is safe across stop/relaunch: the dual-mapped JIT cache is reused on TXM devices and properly disposed elsewhere, closing a use-after-free window. Emulation shutdown runs in a `finally` block, so a failed session can no longer leave half-initialized statics behind, and the native side is fully released before `exit-emulation` fires.
- **Pipeline cache race.** Shader caches are saved by a request to the GPU thread instead of reading `vkGetPipelineCacheData` from an arbitrary thread while the governor may be trimming, which could corrupt cache files.
- **Touch input.** Touch indices handed to the native driver stay stable for the lifetime of a touch; lifting one finger no longer shifts every other finger's index. Pending touches are cleared when emulation stops, so the next session starts without phantom touches.
- **Lifecycle/leaks (Swift).** AirPlay connection callbacks and NotificationCenter observers are tokenized and removed on disappear; the FPS/RAM monitors no longer keep themselves alive through their polling tasks; log capture is thread-safe and bounded in size.
- **Launch/exit paths.** `exit-emulation` and first-frame callbacks are guarded against double execution; the security-scoped resource used for game access is released on every exit path; DLC lists returned by the core are always freed.
- **Usability.** If emulation fails to start, the app shows an error instead of failing silently; if JIT has to be enabled through TrollStore/StikDebug, the app no longer tries to start the game without JIT on the same pass; missing Metal support is reported clearly.
- **Controller setup in the library** no longer duplicates notification handlers on every visit, and deleting a game works from security-scoped folders and removes its per-game cache.

### Changed

- Shader cache is **on by default on devices with about 8 GB of RAM or more**, off on smaller devices. (Existing choices are kept.)
- Project cleanup: new name and bundle ID, removal of upstream desktop CI, Git LFS no longer used, GitHub Actions workflow that builds unsigned IPAs without a Mac.

### Not changed on purpose

- Memory manager mode, JIT handling, URL scheme `melonx://` and the StikDebug script name are as in upstream.

### Known limitations

- This release has not been measured on real devices yet. The numbers in the diagnostic log are meant to guide the next steps.
- The Memory Governor algorithm was checked with a model (heavy games, growing memory use, memory warnings, delayed samples), not on hardware. The code compiles in CI, but behaviour on devices still needs reports.
- Resolution scale cannot be changed while a game runs, so there is no automatic thermal downscaling yet. Thermal state is only logged for now.
- The pipeline cache stores what MoltenVK stores; Metal may still need to compile some shaders on the first run after an update.
