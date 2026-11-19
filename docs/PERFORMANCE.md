# Performance model

This document explains the Meliora Adaptive Engine, the memory governor and the evidence behind their current values. It is written for people who want to understand a log, tune an experiment or challenge a release claim. Every number here comes from a game log or from a constant in the code; when the code and this document disagree, the code is right and the document is a bug.

The current values are the 0.0.6 values. They were calibrated from the first long 0.0.4 field log, checked against three 0.0.5 field sessions (4.5–6.1 minutes each), and a 0.0.6 build 25 run passed the 10-minute Test B (10:06). The final build 29 (2026-11-20) then passed a short smoke test: Test C validated at 30 FPS, the idle-trim fix confirmed in the field, the Liquid Glass icon verified in the IPA. No run stepped down, so snap-to-floor and the 384 MB up-step guard remain unexercised in the field. The [release notes](https://github.com/Quancore21/Meliora/releases/tag/v0.0.5) and [CHANGELOG](../CHANGELOG.md) say what is known and what is still open.

## Design principles

1. **Picture first.** Lowering the render scale is a last resort, not a default. 0.0.5 stops at x0.90 of the starting scale; 0.0.4 stopped at x0.85 and the field log showed that the extra softness bought little in the heaviest scenes.
2. **The median is the signal.** A single slow frame is usually a shader compile, a pipeline creation or an allocation. Resolution cannot fix those, and reacting to them makes the picture worse for the rest of the session. A sustained median slowdown is different: it means the steady-state load is above what the device can deliver.
3. **The GPU must be the bottleneck.** The controller steps down only when the GPU thread is also saturated. CPU-bound slowness and the game's own pacing are detected and deliberately left alone.
4. **The controller must be able to climb back.** The 0.0.4 controller needed ten consecutive green windows and reset the streak on every hitch, so in practice it never returned from its floor. 0.0.5 counts clean windows and ignores CPU-bound slow windows and spikes for the climb.
5. **Logs, not adjectives.** Every decision is logged with the median, the budget, the GPU busy fraction and the old/new scale. A claim that cannot be checked in a log is not a performance claim.

## Adaptive render scale

Source: `src/Ryujinx.Headless.SDL2/AdaptivePerformance.cs`. Hook: `WindowBase.RunLoop` calls `AdaptivePerformance.OnIterationEnd(fifoBusyTicks, presented)` once per render-loop iteration and `AdaptivePerformance.ResetSession()` once per game.

### Constants

| Constant | Value | Meaning |
| --- | --- | --- |
| `WindowSize` | 96 | Present-to-present samples per evaluation window (~1.6 s at 60 FPS). |
| `WarmupPresents` | 90 | Frames skipped after the first present. |
| `WarmupMs` | 5000 | Minimum wall-clock warmup. |
| `MedianTolerance` | 1.05 | The sustained median may sit 5% above the target before acting. |
| `FailWindowsToStepDown` | 3 | Three consecutive slow, GPU-bound windows (~5 s). |
| `PassWindowsToStepUp` | 5 | Five clean windows (~8 s) before climbing. |
| `DownCooldownMs` | 5000 | At most one step down per 5 s. |
| `UpCooldownMs` | 8000 | At most one step up per 8 s. |
| `DownStep` | 0.95 | Multiplier per step down. |
| `UpStep` | 1.06 | Multiplier per step up. |
| `FloorFraction` | 0.90 | Floor relative to the starting scale. |
| `AbsoluteFloor` | 0.5 | The scale can never go below 0.5×. |
| `GpuBusyForDownStep` | 0.75 | Busy fraction that counts as GPU-bound. |
| `GpuBusyForUpStep` | 0.50 | The GPU thread must be below this to climb. |
| `GpuIdleForCpuBoundLog` | 0.35 | Below this, a slow window is logged as CPU-bound. |
| `MaxSampleMs` | 100 | Samples above this are clamped; pauses and loads are not frame times. |
| `LogEveryWindows` | 6 | Periodic status line every six windows (~10 s). |

### Window and budget

- The controller records present-to-present intervals. If several frames are presented back-to-back in one iteration (catch-up after a hitch), only the first gap counts as a real frame time.
- A window is evaluated when it holds 96 samples. The first 90 presents and the first five seconds are warmup and make no decisions.
- At 60 FPS the budget is `1000 / 60 × 1.05 = 17.5 ms`. At 30 FPS it is `35 ms`.
- `p95` is still computed for the periodic status line; it is not used for decisions.

### Busy fraction

`fifoBusyTicks` measures how long the iteration spent inside `Device.ProcessFrame()`. The busy fraction is the sum of those ticks divided by the wall-clock time of the evaluated iterations, clamped so a single iteration cannot report more busy time than wall time.

This is a **GPU-thread saturation proxy**, not Metal GPU utilization and not a hardware counter. It is deliberately conservative: a window can be slow with a moderately busy GPU thread and still not trigger a step down, because the evidence is not strong enough. That is the intended picture-first behaviour.

### State machine

For each evaluated window:

```
budget = 1000 / targetHz * 1.05
slow = median > budget
gpuBound = busyFraction >= 0.75

if slow and gpuBound:
    failStreak += 1
    passStreak = 0
else:
    failStreak = 0
    if slow:
        log "slow frames but GPU idle" at most every 10 s
    else if busyFraction < 0.50:
        passStreak += 1

if slow and gpuBound and failStreak >= 3 and cooldown >= 5 s:
    if scale > floor:
        scale = max(floor, scale * 0.95)
        apply
        reset streaks
    else:
        log "at floor" periodically

else if not slow and busyFraction < 0.50 and
        passStreak >= 5 and cooldown >= 8 s and scale < start:
    scale = min(start, scale * 1.06)
    snap to start when within 0.01
    apply
    reset passStreak
```

Two details matter:

- A slow **CPU-bound** window resets `failStreak` instead of pre-charging it. Without that, two CPU-bound windows followed by one GPU-bound spike would step down immediately, contradicting "three consecutive GPU-bound windows".
- A slow CPU-bound window does **not** reset `passStreak`. The climb is about clean windows, and a CPU-bound slow window says nothing about whether the GPU could handle a higher scale. A GPU-bound slow window does reset it.

### Floor

The floor is:

```
floor = max(0.5, startScale * 0.90)                  // default
floor = clamp(MELIORA_FLOOR, 0.5, startScale)        // when MELIORA_FLOOR is set
```

`MELIORA_FLOOR` is an absolute scale, not a fraction. `MELIORA_FLOOR=0.75` means 0.75× regardless of the starting scale. Values outside 0.5–1.0 are ignored and the default fraction is used.

The starting scale is always the ceiling: if the user launched the game at 0.75×, the controller can never climb above 0.75×. It also never lowers a game below 0.5×.

### What the controller does not do

- It does not change any setting other than `GraphicsConfig.ResScale`.
- It does not touch per-game profiles, clocks, thermal policy or MoltenVK options.
- It does not react to single-frame spikes, shader compiles or CPU-bound slowness.
- It does not allocate or perform per-frame logging.
- It does not guarantee 60 FPS. A GPU-bound game at its floor can still miss the target; the `at floor` line is the honest evidence of that.

## Calibration history

### 0.0.3 baseline

Super Mario Odyssey 1.3.0 on an iPhone 17, iOS 27, warm phone (`thermal=serious`), three ~2-minute sessions:

| Metric | Value |
| --- | --- |
| Average FPS | 56.5 / 57.5 / 56.4 |
| Minimum FPS | 45 / 32 / 32 |
| `GPU processing thread is too slow` warnings | 2 / 6 / 3 |
| Footprint over ~1 minute | 2.4 → 3.7 GB |

This established that the device was GPU-bound under thermal throttling and that heavy scenes dipped into the low 30s.

### 0.0.4: what the first long log showed

The 0.0.4 controller used the 95th percentile, a x0.85 floor, ten pass windows and a 10-second up-cooldown. A 17-minute session produced:

- The first false step down fired while the game held a median of 16.7–17.2 ms (a locked 60 FPS); a compile-spike p95 tripped the gate.
- The controller reached its x0.85 floor about six minutes in and stayed there for the remaining eleven minutes. There were no up-steps: a single hitch reset the ten-window streak.
- Heavy scenes had a median of 19.7–27.6 ms with `gpuBusy` 74–95%, so the GPU really was the bottleneck there — but x0.85 still looked visibly soft.
- 56 `GPU processing thread is too slow` warnings (~3.3/min), close to the 0.0.3 rate.
- Footprint grew from 2.9 GB to 5.4 GB. The startup memory view reported about 3.2 GB available; most of the session ran with about 1.3–1.4 GB, and the last `LogShrink` measured 265 MB. Seven emergency trims fired, but 32 of 33 cache shrinks were no-ops (7 MiB freed in total). The session ended abruptly at about 6 GB; the captured log has no termination marker, so an iOS kill is likely but unconfirmed.
- In earlier logs an instantaneous `available=` sample looked healthy (1142 MB) seconds before the trim check measured 335 MB, which is why 0.0.5 logs the window minimum.

### 0.0.5: what changed and what is still open

Changed:

- Median gate with a 5% tolerance; no decision on p95.
- Three consecutive GPU-bound slow windows for a step down; `gpuBusy ≥ 75%`.
- x0.90 floor; x0.95 step.
- Five clean windows for a step up; `gpuBusy < 50%`; 8-second cooldown; CPU-bound windows and spikes do not reset the climb.
- `MELIORA_FLOOR` absolute override.
- Texture-cache emergency floor 192 → 96 MiB.
- Proactive trim at 3/4 of the headroom instead of 1/2.
- `minAvail=` and `texCache=` telemetry.

### 0.0.5 field results (2026-11-18/19)

Three Super Mario Odyssey sessions on the reference iPhone 17 (iOS 27.2.1, 8 GB), all with `memoryType=DedicatedMemory`:

| Run | Length | Scene | Peak footprint | Minimum available | Outcome |
| --- | --- | --- | --- | --- | --- |
| #1 | 4:33 | 2661781375 → 512560049 | 6029 MB | 113 MB | 14 emergency trims + 9 proactive trims; no termination marker |
| #2 | 5:39 | 512560049 | 5.57 GB | 575 MB | normal exit (`TerminationRequested` → `ServiceNv Close`) |
| #3 | 6:11 | 512560049 | 5.52 GB | 621 MB | normal exit |

What the logs confirmed:

- **The controller followed the policy.** No step down happened while the median was inside the budget. Run #1 stepped down at medians of 20.4 / 22.3 / 20.7 ms with `gpuBusy` 78 / 91 / 92%, and climbed back at 17.1 / 16.8 ms with `gpuBusy` 46 / 49% (56 s after reaching the floor). Run #3 stepped down at 27.8 / 23.6 / 21.7 ms (`gpuBusy` 92 / 87 / 80%) and climbed back at 17.0 / 17.0 ms (`gpuBusy` 37 / 35%) after 94 s. In 0.0.4 the controller sat at 0.85x for the remaining eleven minutes.
- **The floor direction is right, but the sample is short.** 0.90x is still GPU-bound in the heaviest scene, and the extra softness of 0.85x did not buy a stable frame rate in 0.0.4. These runs are 4.5–6.1 minutes, not the 10–15 minutes Test B requires.
- **The duplicate step is a real resize.** Run #1 logged `0.90x -> 0.90x`; the actual values were 0.9025 → 0.9000, and the exact `!=` comparison in `TextureManager` recreated the host textures and views for a 0.25% linear change. Fixed in 0.0.6 by snapping to the floor and logging three decimals.
- **Memory pressure is not a texture-cache problem.** At the 6.03 GB peak the cache was 84–88 MiB; the last shrink freed 8 MiB at 146 MB available. Fourteen emergency trims and nine proactive trims mostly could not return memory because the pressure is in buffers, the managed heap and driver allocations, not in the texture cache.
- **Run #1 ended without a termination marker.** `TerminationRequested` and `ServiceNv Close` are absent; the tester reports a manual close. The log does not prove a jetsam kill, so the documentation does not claim one.
- **The 30 FPS toggle was checked, not validated.** A 38-second smoke run selected target 30, budget 35.0 ms, median 33.3 ms, `gpuBusy` 6%, scale 1.00x. The build 29 run (2026-11-20, ~1:55) then validated it: `budget=35.0ms`, 29–30 FPS held, no false step down.
- **The exported log is not reliable enough for fine timing.** Swift-emitted `[Meliora] mem` and low-memory lines have no timestamp and can be merged with C# lines (for example `Gamepad2026-11-17 ...`). The 0.0.6 logging fix adds timestamps, line-atomic writes and a per-pipe remainder buffer.

### 0.0.6 (build 25 Test B; build 29 smoke test)

Build 25 contains the controller and logging changes that are not in the 0.0.5 release build:

- **Snap-to-floor.** A down-step that would land within 0.01 of the floor lands on the floor directly; the step line prints three decimals. This removes the 0.9025 intermediate value and the duplicate host-texture resize.
- **Line-atomic `[Meliora]` logs.** The Swift governor emits `yyyy-MM-dd HH:mm:ss.SSS [Meliora] ...` lines through a single write, `stdout`/`stderr` are line-buffered before the first app write, `LogCapture` keeps the incomplete tail of each pipe until the newline arrives, and the on-disk log receives only reassembled lines.
- **Memory-aware up-step.** A step up is blocked while `os_proc_available_memory()` is below 384 MB, and the block is logged; an unknown value (governor disabled or no sample yet) does not block. This is a guard against climbing into pressure, not a memory saving.

The final build also contains the idle-trim cleanup and the Liquid Glass icon; neither is in build 25.

Field result (2026-11-19, 10:06, Super Mario Odyssey, reference iPhone 17, iOS 27.2.1, 8 GB, `DedicatedMemory`):

| Metric | Value |
| --- | --- |
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

The log contains **zero** merged or timestamp-less `[Meliora]` lines, so the logging fix is field-validated. The run is long enough for Test B and the controller made no false step down, but it never stepped down or up, so snap-to-floor and the 384 MB guard were not exercised. The idle-trim cleanup is a later code change; build 29 then confirmed it in the field (see below).

Build 29 (2026-11-20, same device, three short sessions) covered what build 25 could not:

- **30 FPS target (Test C), ~1:55.** `start 1.00x, floor 0.90x, target 30 fps`, `budget=35.0ms`, 29–30 FPS held, no false step down despite p95 spikes to 70.7 ms. Clean exit.
- **60 FPS @ 1.00x, ~3:05.** Sustained slow frames (p95 21–48 ms vs the 17.5 ms budget) did not trigger a step down: the controller logged `slow frames but GPU idle (gpuBusy=34%), not touching scale (CPU-bound?)`. The idle-trim fix held: two minutes with the cache at 92–96 MiB and `minAvail` 470–590 MB (below the ~575 MB threshold) produced zero repeated trims; the single trim that fired had `texCache 102 MiB`.
- **Emergency trim floor (floor / 2) observed.** After iOS memory warnings during level loads the cache dropped below the 96 MiB floor and stayed there — 76–79 MiB in the 30 FPS and 1.00x runs, 94 MiB at 2.00x — instead of being re-grown immediately. The 30 FPS run also logged the 30 FPS numbers precisely: median 33.3 ms, p95 36.2–37.1 ms (one 70.7 ms spike during the load), gpuBusy 11–13% after the load.
- **60 FPS @ 2.00x, ~0:45, crashed.** Seven iOS memory warnings, `minAvail` 260 MB, no clean exit. Scales above 1.00x are not viable on 8 GB; the run ended before the controller could step down.

Open:

- Snap-to-floor, the three-decimal step log and the 384 MB up-step guard have not fired in any field log, because no session stepped down; they are verified by code review only.
- The memory peak remains a field risk. The memory-aware up-step guard and the idle-trim cleanup avoid bad behaviour, but they do not lower the peak.
- The `MELIORA_UNIFIED_MEMORY=1` experiment is closed (see [Unified Memory](#unified-memory-meliora_unified_memory1)); the branch is not merged and the override stays off.
- The thresholds still come from a small sample; do not generalize them to other games or devices without a log.

## Memory governor

### Swift policy (`MelioraGovernor.swift`)

- Tick: 0.5 s on a utility queue.
- Headroom: `clamp(10% of physical memory, 512 MiB, 1 GiB)`. On an 8 GB iPhone this is 768 MiB.
- While the governor is enabled, every positive tick reports `os_proc_available_memory()` to the core.
- Proactive trim: when available `< headroom × 3/4` (~575 MB on 8 GB) and the last trim was more than 10 s ago, **and** the texture cache is above its 96 MiB normal floor. At the floor the trim cannot shrink anything, so the request is skipped instead of running a no-op GC every 10 seconds. The 0.0.4 threshold was `headroom / 2` (~383 MB), which the field log reached only after the process was already in its death spiral.
- iOS memory warning: immediate trim. This path is not gated by the cache size; a real warning may use the core's lower emergency floor.
- Logging window: 10 s. `minAvail` is the minimum over the window; `texCache` is read from the core.
- Pipeline cache: save request every 10 minutes and on entering the background.

`os_proc_available_memory()` is spiky. A single reading can be much higher than the value the trim check sees one tick later, which is why the log reports the window minimum instead of an instantaneous sample.

### Core policy (`MemoryGovernor.cs`)

`Configure` clamps the headroom to 256 MiB–2 GiB and resets the sample state. `GetTextureCacheLimit` is called by the texture cache with the constrained budget, the unconstrained budget, the current size and the cache floor. When the governor has no sample yet, it returns the constrained budget. Otherwise it computes:

```
deficit = headroom - available + (currentSize - sizeAtSample)
limit = clamp(currentSize - deficit, floor, unconstrainedMax)
```

`sizeAtSample` is captured on the first cache check after a memory report, so textures added or evicted since the sample are accounted for. A negative deficit means there is enough headroom and the cache may use its unconstrained budget.

`RequestEmergencyTrim` sets a flag and schedules a non-blocking optimized GC. On the next cache check, the limit becomes `min(unconstrainedMax, max(currentSize / 2, floor / 2))`: an emergency may shrink the cache below its normal floor, down to half of it (48 MiB when the normal floor is 96 MiB). The GC is deliberately not a blocking compacting collection: freezing emulation for hundreds of milliseconds would be worse than the memory it frees.

### Texture-cache limits

`AutoDeleteCache` computes its budgets from the GPU's reported memory and the process budget:

- `MaxTextureSizeCapacity`: 4 GiB.
- `DefaultTextureSizeCapacity`: 1 GiB when the GPU reports no memory.
- `MinTextureSizeCapacity`: 512 MiB.
- Unconstrained budget: 50% of the GPU's reported maximum, clamped to 512 MiB–4 GiB.
- Process budget on devices with 8 GiB or less of .NET-visible memory: 30% of that memory, clamped to 512 MiB–2 GiB.
- `MinGovernedTextureSizeCapacity`: 96 MiB normal floor (was 192 MiB in 0.0.4); an emergency trim may target 48 MiB.

The 192 MiB floor made emergency trims a no-op in the field log: the cache sat at 47–206 MiB, and `GetTextureCacheLimit` could not return a value below 192 MiB. The log showed `texture cache 130 MiB -> 130 MiB` while the process was already under severe memory pressure. SMO runs with the cache as low as ~47 MiB, so 96 MiB keeps a safety margin without blocking the trim.

The 0.0.5 logs confirm the direction: at the 6.03 GB peak the cache was 84–88 MiB, so the 96 MiB floor was not the limiter. Lowering it further would not address the footprint.

The 0.0.6 build 25 log shows the remaining defect: 18 emergency and 16 low-memory trims fired while the cache sat at 95 MiB, and the old emergency formula clamped every target back to 96 MiB. The cleanup skips the proactive request at the floor and lets a real memory warning target 48 MiB.

### What the governor cannot do

The texture cache is not the whole footprint. The 0.0.4 log's cache was only 47–206 MiB while the process reached 5.4 GB. Buffers, the managed heap, MoltenVK/Metal allocations and driver overhead are outside the governor's direct control. The governor is a safety valve for the caches it owns, not a general-purpose memory manager.

## Frame pacing

`MelioraFramePacing` is the single source of truth for the composition target:

- `MELIORA_TARGET_FPS`, default 60, clamped 15–240.
- Re-read at every game launch by `SurfaceFlinger`.
- Used by the composer, the render-loop status update and the adaptive budget.

The composer's spin tail follows the measured wake lateness between 0.2 ms and 2 ms. `MELIORA_SPIN_US` pins it for an experiment. The *Smooth Pacing* switch is reserved: the core can follow a changed target, but the app only applies settings at launch, so it has no effect in 0.0.5.

## Reading the log

### Adaptive lines

```text
[Meliora] adaptive: start 1.00x, floor 0.90x, target 60 fps
[Meliora] adaptive: p95=22.1ms median=16.8ms budget=17.5ms gpuBusy=41% scale=1.00x
[Meliora] adaptive: step down median=19.9ms budget=17.5ms gpuBusy=83% scale=1.00x -> 0.95x
[Meliora] adaptive: step up median=16.6ms budget=17.5ms gpuBusy=38% scale=0.95x -> 1.00x
[Meliora] adaptive: slow frames but GPU idle (gpuBusy=22%), not touching scale (CPU-bound?)
[Meliora] adaptive: at floor (0.90x) median=21.4ms budget=17.5ms gpuBusy=79%
```

How to read them:

- `median` is the sustained rate. It is the decision signal.
- `p95` is spike severity. A high p95 with a normal median is a hitch, not overload.
- `gpuBusy` is the GPU-thread busy proxy. A step down with `gpuBusy < 75%` should not happen; if it does, attach the log.
- `step up` should appear after a heavy scene ends. Its absence over many minutes at a reduced scale is a calibration problem.
- `at floor` is not a failure by itself. It is the honest statement that the game is still GPU-bound at the lowest allowed scale.

### Memory lines

```text
[Meliora] mem footprint=2914MB minAvail=612MB texCache=168MiB thermal=serious lowPower=false fps=59 avgFps=56.8 minFps=41.0
```

- `footprint` is `phys_footprint` from `task_vm_info`.
- `minAvail` is the worst `os_proc_available_memory()` in the 10-second window.
- `texCache` is the texture cache's total size in MiB. If it sits at 96 MiB while `minAvail` is low, the pressure is outside the texture cache.
- In 0.0.5 and earlier, the Swift governor's `[Meliora] mem` and low-memory lines have no timestamp and can be split or merged with C# output. Use the nearest C# `MeloNX[...]` timestamp and treat sub-minute timing as approximate. The 0.0.6 build 25 log has zero merged or timestamp-less `[Meliora]` lines; 0.0.6 writes complete timestamped lines and reassembles pipe chunks.
- `fps` is the instantaneous value at the log tick; `avgFps` and `minFps` cover the window.
- `thermal` is the iOS thermal state; compare runs at a similar state.

### GPU line

```text
[Meliora] gpu: Apple MTL GPU ..., memoryType=DedicatedMemory, tbdr=True, mvk: argBuffers=1 prefill=0
```

The `memoryType=` field is the effective choice. `UnifiedMemory` in a release build would mean the override is active; release builds ship with it off.

## Experiments

### A/B protocol

1. Use the same game, scene, route, graphics settings and IPA variant.
2. Compare runs at a similar thermal state. Record `thermal=` from the first mem line.
3. Let the first minutes pass; shader compilation is not steady state.
4. For a controller comparison, use a 10–15 minute run through heavy scenes, not a two-minute menu run.
5. Keep the log from both runs and compare medians, steps, time at each scale, GPU warnings and `minAvail` / `texCache`.
6. Do not change two things at once. If you change the floor and the busy threshold, the log cannot tell you which one mattered.

The full routine and acceptance criteria are in [TESTING.md](TESTING.md).

### Unified Memory (`MELIORA_UNIFIED_MEMORY=1`)

**Closed.** This was a separate experiment, not a release feature. It switches the Vulkan memory type to `UnifiedMemory` on MoltenVK, which makes `BufferBackingState` treat buffers as host memory and skip the device-local swap path. The goal was to measure whether host/device duplication is part of the footprint.

The original 2661781375 → 512560049 save is no longer reproducible: the save has progressed past that transition. A paired 0.0.5 control was therefore not run. Instead, the 0.0.5 UnifiedMemory build was compared with the 0.0.6 build 25 `DedicatedMemory` reference on the same device. The runs differ in length (14:27 vs 10:06) and thermal state (the UnifiedMemory run was hotter), so FPS and trim counts are not a clean pair; the footprint result is still usable.

| Metric | UnifiedMemory (0.0.5) | DedicatedMemory (0.0.6) |
| --- | --- | --- |
| Effective memory type | `UnifiedMemory` | `DedicatedMemory` |
| Peak footprint | 5725 MB | 5779 MB |
| `minAvail` from mem lines | 405 MB | 358 MB |
| Last `LogShrink` available | 323 MiB | — |
| Emergency / low-memory trims | 61 / 52 | 18 / 16 |
| `GPU processing thread is too slow` | 27 | 11 |
| Median `gpuBusy` (first 9.5 min) | 46% (peak 96%) | 30% (peak 61%) |
| FPS median / mean | 55 / 52.6 | 56 / 54.6 |
| Adaptive steps | 1.00 → 0.95 → 1.00 | none |
| Exit | clean | clean |

The 323 MiB figure is the last `LogShrink` in the 0.0.5 UnifiedMemory log; the final `[Meliora] mem` lines were lost because the 0.0.5 process still had them in its 4 KB stdout buffer. The UnifiedMemory run does not show a footprint win (the 54 MB peak difference is within run-to-run noise), and it keeps the GPU thread busier: disabling buffer migration puts every buffer in host memory and adds work through MoltenVK. The experiment is rejected, the test branch is not merged, and `MELIORA_UNIFIED_MEMORY` stays off.

### `MELIORA_FLOOR`

For testing a different floor without changing code:

```sh
MELIORA_FLOOR=0.80
```

This is an absolute scale, clamped to 0.5–1.0 and never above the starting scale. It is intended for experiments, not for routine play. Compare the same route with and without it.

### Target 30

For games that natively target 30 FPS, enable *Target 30 FPS* and restart the game. The pacing target is applied at launch. The adaptive budget follows the target: 35 ms at 30 FPS.

## Limitations

- **One device, one game.** The calibration comes from SMO on an iPhone 17: three 0.0.5 sessions of 4.5–6.1 minutes, one 0.0.6 build 25 session of 10:06 and three build 29 smoke sessions (2026-11-20), including a validated 30 FPS run. Other titles, devices and thermal states may need different values.
- **The memory peak is still open.** The 0.0.6 up-step guard and idle-trim cleanup are mitigations; the footprint is dominated by buffers, the managed heap and driver allocations, not the texture cache.
- **Busy fraction is a proxy.** It measures the GPU thread around `ProcessFrame`, not Metal utilization. A game can be GPU-bound in a way this proxy underestimates.
- **The controller cannot fix hitches.** Shader compiles, pipeline creation and allocation stalls are not resolution problems.
- **The controller cannot fix CPU-bound scenes.** Lowering resolution does not help the guest CPU or the game's own pacing.
- **The governor cannot fix all memory pressure.** It controls textures and asks for a GC; buffers and driver allocations are outside its reach.
- **No automated performance tests.** Testing is manual on a real device; CI validates builds, not FPS.
- **iOS thermal behaviour is not deterministic.** Two runs on the same route can differ by several FPS. Compare trends and medians, not single frames.
- **Picture-quality trade-off is real.** x0.90 is less soft than x0.85 but still softer than native. Users who prefer a fixed image should disable adaptive resolution.

## Reporting a problem

Attach the `[Meliora] adaptive` and `[Meliora] mem` lines from the run, the device, iOS version, game and title ID, IPA version and settings. If the problem is a step down that should not have happened, include the periodic lines before and after it; the median and `gpuBusy` values are the evidence. The report template is in [TESTING.md](TESTING.md#attaching-logs-to-an-issue).
