<p align="center">
  <img src="src/MeloNX/MeloNX/Assets/Assets.xcassets/AppIcon.appiconset/nxgradientpng.png" alt="Meliora" width="120">
</p>

<h1 align="center">Meliora</h1>

<p align="center">
  A Nintendo Switch emulator for iPhone and iPad, focused on stability and measurable performance.<br>
  Based on <a href="https://github.com/VertexSelection/MeloVertex">MeloVertex</a>, which is based on MeloNX, which is based on Ryujinx.
</p>

<p align="center">
  <a href="https://github.com/Quancore21/Meliora/releases/latest"><img src="https://img.shields.io/github/v/release/Quancore21/Meliora?label=release" alt="Latest release"></a>
  <a href="LICENSE.txt"><img src="https://img.shields.io/badge/license-AGPL--3.0-blue" alt="License: AGPL-3.0"></a>
  <img src="https://img.shields.io/badge/platform-iOS%20%7C%20iPadOS-lightgrey" alt="Platform: iOS and iPadOS">
  <a href="https://github.com/Quancore21/Meliora/actions/workflows/ios.yml"><img src="https://img.shields.io/github/actions/workflow/status/Quancore21/Meliora/ios.yml?label=build" alt="Build status"></a>
</p>

**Current release: [0.0.5](https://github.com/Quancore21/Meliora/releases/tag/v0.0.5)** — an Adaptive Engine rebalance built from the first 0.0.4 field log. Three 0.0.5 field sessions (4.5–6.1 minutes) confirmed the median gate, the 0.90x floor and climb-back; a 10–15 minute Test B is still pending, and memory pressure at large scene transitions remains a known issue. The [release notes](https://github.com/Quancore21/Meliora/releases/tag/v0.0.5) explain what is known and what is still open. The separate `MELIORA_UNIFIED_MEMORY=1` experiment is not enabled in release builds.

> **Unofficial project.** Meliora is an independent fork. It is **not affiliated with** the MeloNX team, the MeloVertex author or the Ryujinx project. Please do not ask them for support with Meliora.

## Contents

- [About Meliora](#about-meliora)
- [Project status](#project-status)
- [Requirements](#requirements)
- [Install](#install)
- [First run](#first-run)
- [Your own games only](#your-own-games-only)
- [What Meliora adds](#what-meliora-adds)
- [Settings](#settings)
- [Environment variables](#environment-variables)
- [Diagnostics](#diagnostics)
- [Performance notes](#performance-notes)
- [Troubleshooting](#troubleshooting)
- [Build it yourself](#build-it-yourself)
- [Documentation](#documentation)
- [Contributing](#contributing)
- [Credits](#credits)
- [License](#license)

## About Meliora

Meliora is a fork of the iOS port of Ryujinx. It keeps the emulator core and the Swift app, and focuses on the parts that are specific to running games on a phone: a closed-loop render-scale controller, a memory governor that reacts before iOS kills the process, Darwin thread scheduling, and frame pacing that follows the real device instead of a fixed guess.

The name is Latin for "better". The goal is not a new emulator: it is to keep what works, remove what does not, and make each release a little better than the last one. It builds on other people's work and says so openly — see [Credits](#credits).

## Project status

- **0.0.5 is the current release.** It reworks the adaptive controller (median gate, x0.90 floor, working climb-back), fixes the texture-cache trim floor, and adds honest memory telemetry. See the [changelog](CHANGELOG.md).
- **Device validation is in progress.** The 0.0.5 thresholds come from one 17-minute 0.0.4 session on an iPhone 17 (iOS 27.2) and were checked against three 0.0.5 sessions of 4.5–6.1 minutes. The controller behaved as designed and climbed back from 0.90x, but a 10–15 minute Test B is still pending. The release notes are updated as results arrive.
- **Known 0.0.5 issues.** A step that lands on the floor can log `0.90x -> 0.90x` and recreate render targets for a 0.25% change; Swift `[Meliora]` lines in exported logs may be merged and lack timestamps; memory pressure at large scene transitions is still reproducible. The first two are fixed in the unreleased 0.0.6 tree.
- **Compatibility is inherited.** Meliora does not maintain its own game compatibility list and does not claim that a game that runs on MeloNX or Ryujinx runs here. The emulator core is unchanged in 0.0.5; only the performance plumbing around it changed.
- **Unified Memory is an experiment.** `MELIORA_UNIFIED_MEMORY=1` opts into the unified-memory path on MoltenVK. It is not enabled in release builds; a separate test build is used to measure footprint and FPS before it is considered for a release.

## Requirements

- **iOS or iPadOS 18.1 or newer.** Current builds are tested on iOS 27.2; older versions are not validated.
- **JIT is required.** Meliora cannot run without it. On iOS 26 and newer, [StikDebug](https://github.com/StephenDev0/StikDebug) is the supported way to enable it.
- **The Increased Memory Limit entitlement is required on all devices.** The app warns if it is missing. Sideloaders usually add it when signing; if not, add it to the App ID and reinstall.
- **Recommended hardware** (inherited from upstream documentation, not re-measured for Meliora): iPhone 15 Pro or newer, iPad Pro 5th generation or newer, or iPad Air 5 or newer.
- **Extended Virtual Addressing** is only needed on devices with little RAM (iPhones under 4 GB, iPads under 8 GB). It requires a paid developer account or TrollStore, so a separate IPA is provided for it.
- **Your own games, keys and firmware.** Meliora ships with none and will never help you obtain them.

## Install

1. Download an IPA from [Releases](https://github.com/Quancore21/Meliora/releases):
   - `Meliora-<version>.ipa` — Increased Memory Limit. Use this one unless you know you need the other.
   - `Meliora-<version>-extended-va.ipa` — adds Extended Virtual Addressing for low-RAM devices.
2. The IPA is **unsigned**. Sign and install it with your own Apple ID using a sideloader such as AltStore, SideStore, Sideloadly or Plume Impactor.
3. Open Meliora and add your **keys and firmware** with the in-app file picker.
4. Enable **JIT** (StikDebug), then add your games and play.

If Meliora reports the memory entitlement as missing, check your sideloader's documentation for how to add *Increased Memory Limit* to the App ID, then reinstall. See [docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md) if installation or JIT fails.

## First run

Before judging performance, check the following:

- JIT is active; without it the app refuses to run games.
- The Increased Memory Limit entitlement is present.
- Keys and firmware are installed.
- The game is a dump of your own copy, not a converted or trimmed file of unknown origin.
- Let the first minutes pass. Shader compilation and pipeline creation cause hitches that are not representative of steady-state performance.
- Keep the phone out of direct sun and compare runs at a similar thermal state. The `thermal=` field in the `[Meliora] mem` line records what the device reported.

## Your own games only

Meliora ships with **no** games, keys or firmware, and will never help you obtain them. Use files dumped from your own Switch, which needs to run custom firmware for dumping:

- Hacking guide: <https://nh-server.github.io/switch-guide/>
- Dumping [keys](https://git.ryujinx.app/ryubing/ryujinx/-/wikis/Dumping/Keys), [games, updates and DLC](https://git.ryujinx.app/ryubing/ryujinx/-/wikis/Dumping/Games,-Updates-&-DLC) and [firmware](https://git.ryujinx.app/ryubing/ryujinx/-/wikis/Dumping/Firmware)

Issues asking for games, keys or firmware are closed without discussion.

## What Meliora adds

Everything below is on by default unless noted. The performance-related claims are backed by the game logs cited in [docs/PERFORMANCE.md](docs/PERFORMANCE.md); features that are reserved or experimental are labelled as such.

- **Meliora Adaptive Engine.** A closed-loop controller that watches real present-to-present frame times while a game runs. Its gate is the sustained frame rate (the median), not individual spikes: only when the game steadily runs below target *and* the GPU thread is saturated does it lower the internal render scale, in small steps down to 90% of what the game started with, and it climbs back home after the load passes. CPU-bound slowdowns are detected and deliberately left alone, because lowering resolution cannot fix them. The design and its calibration history are in [docs/PERFORMANCE.md](docs/PERFORMANCE.md).
- **Memory Governor.** The app reports how much memory iOS still allows, and the texture caches shrink early to keep a safety margin instead of running at a fixed budget until iOS terminates the process. The 10-second log line reports the worst free memory over the window and the current texture-cache size.
- **Thread QoS scheduling.** Emulator threads get explicit Darwin QoS classes, so the render loop and the game's main thread keep the performance cores while background work (cache writes, counter queries, background JIT) is pushed to the efficiency cores. Shader and pipeline compilation is kept off the efficiency cores, so it does not stall the GPU thread behind unrelated background work.
- **Frame pacing that adapts.** The compositor's spin timing follows the measured wake lateness of the OS sleep instead of a fixed guess, and the composition target can be set to 30 FPS for games that want it. *Smooth Pacing* is reserved for live target changes and is not wired end-to-end yet.
- **Persistent pipeline cache.** MoltenVK's compiled pipelines are saved per game (on exit, on background, every 10 minutes) and reloaded on the next launch.
- **Honest diagnostics.** The log carries `[Meliora] mem ...`, `[Meliora] adaptive ...`, `[Meliora] gpu ...` and `[Meliora] qos ...` lines, so every performance claim can be checked against a log. The exact formats are documented below.

## Settings

Advanced Options, defaults as shipped:

| Setting | Default | Effect |
| --- | --- | --- |
| Adaptive Resolution | ON | Closed-loop render scale under sustained GPU load. Floor x0.90 of the starting scale. |
| Target 30 FPS | OFF | Pace the compositor at 30 instead of 60. Applied at the next game launch. |
| Smooth Pacing | OFF | Reserved for live pacing changes. The core can follow a changed target, but the app does not re-apply settings mid-session, so this switch is a no-op in 0.0.5. |
| Thread QoS Scheduling | ON | Darwin QoS classes for emulator threads. |
| Memory Governor | ON | Early texture-cache trimming from real memory pressure. |
| MVK: Prefill Metal Command Buffers | OFF | May lower command encoding overhead; depends on the game and device. |
| MVK: Shader Compression | OFF | zlib-compresses the MoltenVK pipeline cache. |

Settings that affect emulation are applied at the next game launch, not mid-session. Restart the game after changing one; the app does not re-apply them to a running session.

## Environment variables

These exist for experiments and A/B testing. Most are not exposed in the UI; setting them through a sideloader or a launch environment is your responsibility.

| Variable | Default | Meaning |
| --- | --- | --- |
| `MELIORA_ADAPTIVE` | `1` | `0` disables the adaptive controller for the session. |
| `MELIORA_TARGET_FPS` | `60` | Composition target, clamped to 15–240. Re-read at every game launch. |
| `MELIORA_FLOOR` | unset | Absolute floor for the adaptive scale, 0.5–1.0 (for example `0.75`). Unset uses 0.90× the starting scale. |
| `MELIORA_QOS` | `1` | `0` disables Darwin QoS assignment. |
| `MELIORA_SPIN_US` | unset | Pins the compositor spin tail in microseconds instead of the adaptive 0.2–2 ms. |
| `MELIORA_SMOOTH` | `0` | Reserved live-pacing flag. Effectively a no-op in 0.0.5 because the app only applies settings at launch. |
| `MELIORA_UNIFIED_MEMORY` | `0` | `1` opts into the unified-memory path on MoltenVK. Experimental; not used by release builds. |
| `MVK_CONFIG_PREFILL_METAL_COMMAND_BUFFERS` | `0` | `1` mirrors the *MVK: Prefill Metal Command Buffers* setting. |
| `MVK_CONFIG_SHADER_COMPRESSION_ALGORITHM` | `0` | `4` selects zlib for the pipeline cache; `0` disables compression. |
| `MTL_HUD_ENABLED` | `0` | `1` shows the native Metal HUD with true GPU frame times. |
| `MELIORA_DOTNET` | unset | Build-time only: pins the `dotnet` binary used by `distribution/ios/xc-compile.sh`. |

## Diagnostics

Meliora writes its diagnostics to the emulator log, which is available from the in-app Logs view. The lines that matter for performance reports:

| Line | When | What it shows |
| --- | --- | --- |
| `[Meliora] adaptive: start <scale>, floor <scale>, target <fps>` | Once per session | The starting render scale, the floor the controller may reach, and the pacing target. |
| `[Meliora] adaptive: p95=..ms median=..ms budget=..ms gpuBusy=..% scale=..` | Every ~10 s | Periodic status. **median** is the decision signal; `p95` shows spike severity; `gpuBusy` is the GPU thread busy fraction. |
| `[Meliora] adaptive: step down median=..ms ... scale=A -> B` | On a decision | The scale was lowered after three sustained, GPU-bound slow windows. |
| `[Meliora] adaptive: step up median=..ms ... scale=A -> B` | On a decision | The load passed and the scale climbed back towards the starting value. |
| `[Meliora] adaptive: slow frames but GPU idle ...` | At most every 10 s | Sustained slowness without a busy GPU thread; resolution is deliberately untouched. |
| `[Meliora] adaptive: at floor ...` | Periodic while at the floor | The controller is at its floor and the load is still heavy. |
| `[Meliora] mem footprint=..MB minAvail=..MB texCache=..MiB thermal=.. lowPower=.. fps=.. avgFps=.. minFps=..` | Every 10 s | Footprint, **worst** free memory over the window, current texture-cache size, thermal state, low-power mode, instantaneous FPS and window average/minimum. |
| `[Meliora] gpu: ... memoryType=... tbdr=... mvk: ...` | Once per session | Effective renderer, GPU, memory type and MoltenVK options. |
| `[Meliora] qos: enabled=... perfCores=... applied=...` | First use of each thread role | Which QoS class each emulator role actually received. |
| `[Meliora] governor ...`, `thermal state changed`, `low memory ...`, `iOS memory warning received` | As events happen | Governor configuration, thermal transitions and trims. |

`minAvail=` is the minimum over the 10-second window, not an instantaneous value. A single `os_proc_available_memory` sample can look much healthier than the trim check that ran a moment later; the window minimum is the honest number. `texCache=` is the total texture cache size reported by the core.

In 0.0.5 and earlier, the Swift governor's `[Meliora]` lines have no timestamp and can be split or merged with C# lines in the exported log. Use the nearest C# timestamp and treat sub-minute timing as approximate; 0.0.6 adds timestamps and line-atomic writes.

When reporting a problem, attach the `[Meliora]` lines from the run, the device model, the iOS version, the game and title ID, the IPA version, and the graphics settings you used. Without those, a performance report cannot be reproduced. The reporting template lives in [docs/TESTING.md](docs/TESTING.md).

## Performance notes

- **Picture first.** 0.0.5 stops at x0.90, not x0.85. In the 0.0.4 field log, x0.85 was still GPU-bound in the heaviest scenes while visibly soft, so lowering further bought little. If you prefer a fixed image, turn *Adaptive Resolution* off.
- **The controller only writes the render scale.** It allocates nothing, touches no profile and does not change any other graphics setting. The starting scale is always the ceiling.
- **Single-frame hitches remain.** Shader compiles, pipeline creation and allocations cause 0–5 FPS spikes that lowering resolution cannot fix. The controller deliberately does not react to them.
- **Long sessions can still hit memory pressure.** In the 0.0.5 transition run the footprint peaked at 6.03 GB with 113 MB available, while the texture cache was only 84–88 MiB and 14 emergency trims mostly could not free more. The 96 MiB floor is not the cause; buffers, the managed heap and driver allocations dominate. The `minAvail=` / `texCache=` telemetry shows where the pressure is.
- **Thermal state matters.** A cold-phone run is not comparable to a warmed-up one. The 0.0.3 and 0.0.4 baselines started at `thermal=serious`; the 0.0.5 #1 run started at `thermal=fair` and reached `serious` during the transition. Record the thermal state when comparing releases.
- **Do not compare different routes or settings.** Use the same game, the same scene, the same graphics settings and a similar thermal state. [docs/TESTING.md](docs/TESTING.md) describes the routine used for the release baselines.

## Troubleshooting

| Symptom | First thing to check |
| --- | --- |
| App refuses to launch a game | JIT is not enabled, or keys/firmware are missing. |
| "Increased Memory Limit" warning | Add the entitlement to the App ID in your sideloader and reinstall. |
| Install fails or the app does not open | Re-sign with a different bundle ID; check the certificate and trust settings. |
| Game boots to a black screen or crashes early | Try default graphics settings, verify the dump, and attach the log to an issue. |
| Stutter in the first minutes | Shader compilation; let the pipeline cache warm up and compare steady-state performance. |
| Long session ends abruptly or is killed by iOS | Memory pressure; attach the last `[Meliora] mem` lines and note the session length and whether a termination marker is present. |
| Audio or controller problems | Check the SDL2/Bluetooth setup first; these are app-level issues, not adaptive-engine issues. |

The full guide, including JIT, entitlements, logging and what not to report, is in [docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md).

## Build it yourself

**Without a Mac:** fork this repository and run the **Build iOS IPA** workflow in the *Actions* tab (it also runs on every push to `main`). It builds the emulator core and the app on a GitHub macOS runner and uploads two unsigned IPAs as the `meliora-ipa` artifact. Pushing a tag such as `v0.0.5` publishes the IPAs as a GitHub Release; tags with a suffix (for example `v0.1.0-beta1`) are marked as pre-releases.

**On a Mac:** see [Compile.md](Compile.md) for Xcode, NativeAOT and signing details.

**On Windows:** the Swift app and the iOS NativeAOT publish need macOS, but the C# core can be compiled and validated with the .NET 8 SDK:

```powershell
dotnet build src/Ryujinx.Headless.SDL2/Ryujinx.Headless.SDL2.csproj -c Release --no-incremental
```

A clean tree builds with 0 errors. Compare the warning count with the current baseline instead of expecting zero warnings.

## Documentation

| Document | Audience | Contents |
| --- | --- | --- |
| [docs/README.md](docs/README.md) | Everyone | Documentation index and conventions. |
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | Developers | App/core layers, render loop, memory governor, frame pacing, QoS, Swift↔C bridge. |
| [docs/PERFORMANCE.md](docs/PERFORMANCE.md) | Developers and testers | Adaptive controller specification, memory governor, calibration history, log interpretation. |
| [docs/TESTING.md](docs/TESTING.md) | Testers | Test matrix, reference baselines, acceptance criteria, log attachment template. |
| [docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md) | Users | Install, JIT, entitlements, crashes, memory, audio and input. |
| [docs/FAQ.md](docs/FAQ.md) | Users | Short answers to common questions. |
| [docs/RELEASING.md](docs/RELEASING.md) | Maintainers | Versioning, release checklist, CI, assets, checksums and known issues. |
| [Compile.md](Compile.md) | Developers | Building on macOS and validating the core on Windows. |
| [CONTRIBUTING.md](CONTRIBUTING.md) | Contributors | Issues, pull requests, commit conventions and licensing. |
| [SECURITY.md](SECURITY.md) | Everyone | How to report a vulnerability privately. |
| [CHANGELOG.md](CHANGELOG.md) | Everyone | Release history and the reasoning behind each change. |
| [NOTICE.md](NOTICE.md) | Everyone | Project provenance and third-party attributions. |
| [docs/coding-guidelines/coding-style.md](docs/coding-guidelines/coding-style.md) | C# contributors | C# style used by the core. |
| [docs/workflow/pr-guide.md](docs/workflow/pr-guide.md) | Contributors | Pull-request workflow for this repository. |

## Contributing

Issues and pull requests are welcome. Read [CONTRIBUTING.md](CONTRIBUTING.md) first: it explains what a useful report contains, how to build and test a change, and which upstream rules still apply. Meliora is an unofficial fork; do not report its problems to the MeloNX or Ryujinx teams.

## Credits

Meliora exists because of other people's work:

- **Ryujinx** and all its contributors: the Nintendo Switch emulator everything here is built on.
- **MeloNX** by Stossy11 and contributors: the iOS port of Ryujinx (Swift app, JIT and memory handling, Metal/MoltenVK integration).
- **MeloVertex** by VertexSelection: the direct upstream of this fork. According to its README it fixes the keyboard and a first-setup hang and adds Tomodachi Life support. Meliora continues from its 2.4.5 source.

Third-party components (SDL2, MoltenVK, FFmpeg libraries, libteakra, Swift packages) and their licenses are listed in [NOTICE.md](NOTICE.md).

## License

Meliora is distributed under **AGPL-3.0**; the full text is in [LICENSE.txt](LICENSE.txt), which also preserves the previous MeloNX License and the Ryujinx MIT notice. Third-party components and their licenses are listed in [NOTICE.md](NOTICE.md).

---

Maintained by [@Quancore21](https://github.com/Quancore21).
