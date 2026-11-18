# Testing a Meliora release

Every performance claim in the changelog is meant to be verifiable from a game log. This guide describes how to produce those numbers, using the same routine the releases are calibrated with (Super Mario Odyssey 1.3.0 on an iPhone 17, iOS 27).

## Before you start

- Install the IPA from the release you want to test and enable JIT (see the README).
- Keep graphics settings identical between runs you want to compare — the comparison is only as honest as the settings:

| Setting | Value used for the reference baselines |
| --- | --- |
| Docked mode | Off |
| Aspect ratio | Stretched |
| Resolution scale | 1.0 |
| VSync | On |
| MacroHLE | Off |
| Shader Cache | On |

- In *Advanced Options* keep the toggles at their released defaults, unless a test explicitly targets one toggle.
- Start warm: the reference baselines all started with `thermal=serious`, so a cold-phone run is not comparable to them. The `thermal=` field in the `[Meliora] mem` log tells you which state you have.

### Reference baseline (0.0.3, SMO 1.3.0, iPhone 17, warm phone, 3 sessions)

| Metric | Value |
| --- | --- |
| Average FPS | 56.5 / 57.5 / 56.4 |
| Minimum FPS | 45 / 32 / 32 |
| `GPU processing thread is too slow` warnings | 2 / 6 / 3 |
| Memory footprint over ~1 min | 2.4 → 3.7 GB |

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

## Test B — long session, controller behaviour

Play for 10–15 minutes through heavy scenes. The `[Meliora] adaptive:` lines show what the controller saw and did:

- `start <scale>, floor <scale>, target <fps>` — session start: the scale the game launched with and the floor the controller may step down to (x0.90 of the starting scale by default).
- `p95=..ms median=..ms budget=..ms gpuBusy=..% scale=..` — periodic status (~every 10 s). The **median** is the decision signal (the sustained rate); `p95` shows spike severity, `gpuBusy` is how busy the GPU thread was.
- `step down median=..ms budget=..ms ... scale=A -> B` — the render scale was lowered after a sustained median slowdown with a saturated GPU thread. The picture becomes slightly softer; that is the feature working, not a bug. Spikes alone never trigger a step down.
- `step up median=..ms ... scale=A -> B` — the load passed; the scale climbs back towards the starting value, which is always the ceiling.
- `slow frames but GPU idle ...` — sustained slowness was detected but the GPU was not the bottleneck (CPU or the game's own pacing), so resolution is deliberately untouched.
- `at floor ...` — the controller sits at its floor and the load is still heavy. This line in a log is the most useful one for calibration.

The first seconds of a session are warmup (shader cache load, JIT); the controller does not make decisions there.

## Test C — Target 30 FPS

For games that natively target 30 FPS, enable *Target 30 FPS* and restart the game — the pacing target is applied per game launch, not mid-session. Compare with the same game without the toggle.

## What is not testable yet

Check the [changelog](../CHANGELOG.md) "Changed" notes for the current release: toggles that are described as reserved or experimental (for example *Smooth Pacing* in 0.0.4) are honest no-ops until they are wired, and env-only options (`MELIORA_UNIFIED_MEMORY`, `MELIORA_FLOOR`) are for deliberate experiments, not routine testing.

## Attaching logs to an issue

Export the log from the in-app log view and include the `[Meliora] mem`, `[Meliora] adaptive` and `[Meliora] gpu` lines. State the device model, iOS version, game and the settings you used. Without these lines, performance reports cannot be reproduced.
