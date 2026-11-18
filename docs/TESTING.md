# Testing a Meliora release

Every performance claim in the changelog is meant to be verifiable from a game log. This guide describes how to produce those numbers with the same routine the releases are calibrated with, and what counts as a pass for 0.0.5.

Testing is manual and runs on a real device. CI builds and packages the app; it cannot measure FPS, thermal behaviour or memory pressure.

## Before you start

- Install the IPA from the release you want to test and enable JIT (see the [README](../README.md)).
- Use the same IPA variant (standard or extended-VA) for every run in a comparison. The entitlement changes the memory limit, so mixing variants invalidates the comparison.
- Keep graphics settings identical between runs:

| Setting | Value used for the reference baselines |
| --- | --- |
| Docked mode | Off |
| Aspect ratio | Stretched |
| Resolution scale | 1.0 |
| VSync | On |
| MacroHLE | Off |
| Shader Cache | On |

- In *Advanced Options*, keep the toggles at their released defaults unless a test explicitly targets one toggle.
- Start warm. The reference baselines all started with `thermal=serious`; a cold-phone run is not comparable to them. The `thermal=` field in the `[Meliora] mem` line tells you which state you are in.
- Let the first minutes pass. Shader compilation and pipeline creation cause hitches that are not steady state.
- Do not share keys or firmware in a log. The in-app log should not contain them, but check before attaching a file.

## Reference baselines

### 0.0.3 (SMO 1.3.0, iPhone 17, iOS 27, warm, three ~2-minute sessions)

| Metric | Value |
| --- | --- |
| Average FPS | 56.5 / 57.5 / 56.4 |
| Minimum FPS | 45 / 32 / 32 |
| `GPU processing thread is too slow` warnings | 2 / 6 / 3 |
| Memory footprint over ~1 min | 2.4 → 3.7 GB |

### 0.0.4 (SMO 1.3.0, iPhone 17, iOS 27.2, one 17-minute session)

| Observation | Value |
| --- | --- |
| First false step down | median 16.7–17.2 ms (locked 60 FPS), triggered by a p95 spike |
| Floor reached | x0.85 about 6 minutes in |
| Up-steps | none for the remaining 11 minutes |
| Heavy-scene median | 19.7–27.6 ms, `gpuBusy` 74–95% |
| `GPU processing thread is too slow` warnings | 56 (~3.3/min) |
| Footprint | 2.9 → 5.4 GB |
| Free memory | 988 → 265 MB |
| Emergency trims | 7, all no-ops (`texture cache 130 MiB -> 130 MiB`) |
| Outcome | iOS killed the process at ~6 GB |

0.0.5 is calibrated against this log. The expected improvements are listed in the acceptance criteria below; they are not claimed as measured until a 0.0.5 run confirms them.

## Test matrix

| Test | Length | Purpose |
| --- | --- | --- |
| A — short session | ~2 minutes | Compare average/min FPS, GPU warnings and memory growth with the previous release. |
| B — long session | 10–15 minutes | Check the controller's decisions: false step downs, climb-back, time at the floor. |
| C — Target 30 | 2–5 minutes | Check pacing and the adaptive budget at 30 FPS. |
| D — Unified Memory experiment | 10–15 minutes | Compare footprint and FPS with the standard build. |
| E — optional floor experiment | 10–15 minutes | Compare a different `MELIORA_FLOOR` value on the same route. |

## Test A — short session, comparable to an older release

Play the same game the same way for ~2 minutes (menu → into gameplay). Then compare the log with the release you are upgrading from:

| Metric | Where to look |
| --- | --- |
| Average FPS | `avgFps=` in the `[Meliora] mem` lines |
| Worst drops | `minFps=` in the `[Meliora] mem` lines |
| GPU thread saturation | `GPU processing thread is too slow` warnings |
| Memory growth | `footprint=` in the `[Meliora] mem` lines |
| Worst free memory | `minAvail=` in the `[Meliora] mem` lines (minimum over the 10 s window) |
| Texture cache size | `texCache=` in the `[Meliora] mem` lines |

A test A run is too short to judge the controller; it may still be in warmup or may not have seen a heavy scene. Use test B for decisions.

## Test B — long session, controller behaviour

Play for 10–15 minutes through heavy scenes. The `[Meliora] adaptive:` lines show what the controller saw and did.

### What to check

- **Start line.** `start <scale>, floor <scale>, target <fps>` should show the scale you launched with, a floor of 0.90× that scale (or your `MELIORA_FLOOR`), and the expected target.
- **No false step downs.** A step down while the median is below the budget (17.5 ms at 60 FPS) is a bug. In particular, a step down at a median of 16.7–17.2 ms is exactly what 0.0.5 was meant to prevent.
- **Correct step-down evidence.** Each `step down` should be preceded by sustained slow windows with `gpuBusy ≥ 75%`; the step line itself prints both numbers.
- **Climb-back.** After the heavy scene ends, a `step up` should appear within a reasonable time (five clean windows plus the 8-second cooldown). The final scale should return to the starting scale if the load allows it.
- **Time at the floor.** If the controller reaches 0.90×, it should not stay there for the rest of the session when the load has passed. Compare the time at 0.90× with 0.0.4's 11 minutes at 0.85× on the same route.
- **CPU-bound windows.** `slow frames but GPU idle` means the controller correctly left the scale alone. It should not be followed by a step down.
- **Memory.** `minAvail=` and `texCache=` should be present. If a trim happened, the core log should show the cache actually shrinking. A cache parked at 96 MiB means the governor has done what it can; the remaining pressure is outside the texture cache.
- **Stability.** The process should survive the session. If iOS kills it, attach the last mem lines and note how long it ran.

### Acceptance criteria for 0.0.5

The release is considered validated when a 10–15 minute run on the reference route shows:

| Criterion | Pass condition |
| --- | --- |
| False step downs | None while median ≤ budget. |
| Step-down evidence | Every step has `gpuBusy ≥ 75%` and a sustained median above budget. |
| Climb-back | If the controller stepped down, at least one `step up` after a heavy scene, ending at the starting scale when the load allows. |
| Floor exposure | Less time at the floor than 0.0.4 on the same route. |
| GPU warnings | No more warnings per 2-minute session than the 0.0.3 baseline (2/6/3). |
| Memory telemetry | `minAvail=` and `texCache=` present; trims visible if they fire. |
| Stability | No unexplained crash or jetsam kill. |

If a criterion fails, keep the log and open an issue with the relevant lines. Do not change multiple thresholds at once; a log with two changes cannot tell you which one mattered.

## Test C — Target 30 FPS

For games that natively target 30 FPS, enable *Target 30 FPS* and restart the game — the pacing target is applied per game launch, not mid-session. Compare with the same game without the toggle.

Check:

- The start line reports `target 30 fps`.
- The adaptive budget is 35 ms, not 17.5 ms.
- Pacing feels steadier and the device runs cooler, if the game is actually 30 FPS-bound.
- A 60 FPS game will feel choppier with the toggle on; that is expected.

## Test D — Unified Memory experiment

This test uses a separate build with `MELIORA_UNIFIED_MEMORY=1`; it is not the release build. Do not mix its results with test A/B.

Run the same route as test B and compare:

| Metric | Where to look | Target |
| --- | --- | --- |
| Peak footprint | `footprint=` in the mem lines | Below 5.0–5.2 GB |
| FPS in heavy scenes | `avgFps=` / `minFps=` and the adaptive medians | No regression versus the standard build |
| Effective memory type | `memoryType=` in the `[Meliora] gpu:` line | `UnifiedMemory` |
| Stability | Session length and crash/jetsam | No worse than the standard build |

If footprint does not improve, or FPS regresses, the experiment is rejected and the default path stays. Record both logs; the comparison is the deliverable.

## Test E — optional floor experiment

For testing a different floor without a code change, use an absolute `MELIORA_FLOOR` value, for example `0.80`. Run the same route as test B with and without it and compare:

- time at the floor,
- FPS in the heaviest scene,
- picture softness (subjective, but record it),
- whether the controller still climbs back.

This is an experiment, not a release setting. A value that helps one game may hurt another.

## Log reference

The full interpretation is in [PERFORMANCE.md](PERFORMANCE.md#reading-the-log). The lines you will see:

```text
[Meliora] adaptive: start 1.00x, floor 0.90x, target 60 fps
[Meliora] adaptive: p95=..ms median=..ms budget=..ms gpuBusy=..% scale=..x
[Meliora] adaptive: step down median=..ms budget=..ms gpuBusy=..% scale=A -> B
[Meliora] adaptive: step up median=..ms budget=..ms gpuBusy=..% scale=A -> B
[Meliora] adaptive: slow frames but GPU idle (gpuBusy=..%), not touching scale (CPU-bound?)
[Meliora] adaptive: at floor (0.90x) median=..ms budget=..ms gpuBusy=..%
[Meliora] mem footprint=..MB minAvail=..MB texCache=..MiB thermal=.. lowPower=.. fps=.. avgFps=.. minFps=..
[Meliora] gpu: ... memoryType=... tbdr=... mvk: ...
[Meliora] qos: enabled=... perfCores=... applied=...
[Meliora] governor on/off, physical ... MB, headroom ... MB
[Meliora] low memory (... MB available), trimming caches
[Meliora] thermal state changed: ...
[Meliora] iOS memory warning received
```

The periodic adaptive status is emitted every six windows (~10 s). A step line replaces the periodic line for that window. Warmup produces no decisions.

## What is not testable yet

- *Smooth Pacing* is reserved. The core can follow a changed target, but the app does not re-apply settings mid-session, so the switch is a no-op in 0.0.5.
- Unified Memory is an experiment, not a release feature.
- There are no per-game profiles or per-game adaptive tuning.
- Compatibility is inherited from the emulator core; Meliora does not maintain a game compatibility list.
- CI does not run a performance test. A green build means the app compiles and the IPAs are structurally valid, not that a game runs well.

## Attaching logs to an issue

Export the log from the in-app Logs view and include:

1. Device model and iOS version.
2. Meliora version / IPA file name.
3. Game name and title ID.
4. Graphics settings and whether the run was warm or cold (`thermal=`).
5. The `[Meliora] mem`, `[Meliora] adaptive` and `[Meliora] gpu` lines, ideally the whole log around the event.
6. What you expected, what happened, and the shortest steps that reproduce it.
7. For a step-down or memory problem: the periodic lines before and after the event.

State clearly whether the run is test A, B, C, D or E. A report without these lines cannot be compared with the baselines.
