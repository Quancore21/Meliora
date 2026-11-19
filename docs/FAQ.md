# Frequently asked questions

Short answers to the questions that come up most often. Detailed explanations are linked where they exist.

## About the project

### What is Meliora?

A Nintendo Switch emulator for iPhone and iPad. It is a fork of [MeloVertex](https://github.com/VertexSelection/MeloVertex), which is based on MeloNX, which is based on [Ryujinx](https://ryujinx.app/). Meliora keeps the emulator core and focuses on the iOS-specific performance plumbing: adaptive render scale, memory governance, thread scheduling and frame pacing.

### Is Meliora affiliated with MeloNX, MeloVertex or Ryujinx?

No. It is an independent, unofficial fork. Do not ask the upstream teams for support with Meliora, and do not report Meliora bugs in their issue trackers.

### Why is it called Meliora?

Latin for "better". The goal is incremental improvement over the upstream it came from, not a new emulator.

### Is Meliora open source?

Yes, under AGPL-3.0. The full text is in [LICENSE.txt](../LICENSE.txt); provenance and third-party attributions are in [NOTICE.md](../NOTICE.md).

### Why is the version 0.0.5?

The project is young and changes quickly. The version is bumped per release; `0.0.x` is intentional, not a mistake. See [CHANGELOG.md](../CHANGELOG.md) for what each release changed.

### Does Meliora have a game compatibility list?

No. Compatibility is inherited from the emulator core. Meliora does not maintain its own list and does not guarantee that a game that runs on MeloNX or desktop Ryujinx runs here. The 0.0.x releases changed performance plumbing, not compatibility.

## Requirements and installation

### Which devices are supported?

iOS and iPadOS 18.1 or newer. Current builds are tested on iOS 27.2 on an iPhone 17; older versions are not validated. Recommended hardware, inherited from upstream documentation and not re-measured for Meliora: iPhone 15 Pro or newer, iPad Pro 5th generation or newer, iPad Air 5 or newer.

### Do I need a Mac?

No. The repository builds unsigned IPAs on GitHub Actions for free. See [Compile.md](../Compile.md). A Mac with Xcode is only needed if you want to build and install locally.

### Do I need a paid Apple developer account?

- **Standard IPA:** no. A free Apple ID works, but the app must be re-signed every seven days.
- **Extended Virtual Addressing (`-extended-va` IPA):** yes, or TrollStore. The entitlement is not available to free accounts.

### Do I need JIT?

Yes. Meliora cannot run games without JIT. On iOS 26 and newer, [StikDebug](https://github.com/StephenDev0/StikDebug) is the supported way to enable it. Without JIT a game will not boot correctly; changing emulator settings will not fix that.

### Why are there two IPAs?

- `Meliora-<version>.ipa` includes *Increased Memory Limit* and works with free accounts. Use this one unless you need the other.
- `Meliora-<version>-extended-va.ipa` adds *Extended Virtual Addressing* for low-RAM devices. It needs a paid account or TrollStore.

Do not compare performance between the two: the memory limit is different.

### The app says the memory entitlement is missing. What do I do?

Your sideloader did not add *Increased Memory Limit* to the App ID. Add `com.apple.developer.kernel.increased-memory-limit` when signing, then reinstall. The app warns instead of silently running with less memory. See [TROUBLESHOOTING.md](TROUBLESHOOTING.md#increased-memory-limit-is-missing).

### Can I install over an older version?

Usually yes, if the bundle ID and signing identity match, but sideloaders differ. Re-signing or deleting the app can remove its container, including saves, keys and firmware. Back up anything you cannot re-add before updating. Meliora has no cloud save service.

### Where are my saves, keys and firmware stored?

In the app's container on the device. They are not uploaded anywhere. Deleting or re-signing the app can delete them. Use the in-app file management and your sideloader's backup features; Meliora does not provide a backup service.

## Games, keys and firmware

### Does Meliora include games, keys or firmware?

No, and it never will. Use files dumped from your own Switch. Issues asking for games, keys or firmware are closed without discussion.

### Where do I get keys/firmware/games?

From your own console. The [README](../README.md#your-own-games-only) links to dumping guides. Meliora cannot help you obtain copyrighted files.

### Can I use a converted, trimmed or downloaded game?

No support. Those files fail in ways that look like emulator bugs and cannot be diagnosed reliably. Use a complete dump of your own copy.

### Does Meliora support mods, cheats or custom firmware files?

The emulator core may load some of them, but Meliora does not test or support them. Reports must reproduce with an unmodified dump and default settings.

## Performance

### Why does the image look soft?

*Adaptive Resolution* lowers the internal render scale when a game is sustained-GPU-bound. In 0.0.5 the floor is x0.90 of the starting scale, so the softening is smaller than 0.0.4's x0.85 but still visible in heavy scenes. Turn the toggle off if you prefer a fixed image and accept the frame-rate dips.

### Why not lower the resolution further to get more FPS?

Because the 0.0.4 field log showed that x0.85 was still GPU-bound in the heaviest scenes while visibly soft: the extra softness bought little. Meliora is picture-first. `MELIORA_FLOOR` exists for experiments, but it is not a release setting.

### Will Adaptive Resolution guarantee 60 FPS?

No. It is a closed-loop controller, not a magic frame-rate lock. A game that is GPU-bound at the x0.90 floor will still miss the target; the `at floor` log line is the honest evidence. It also deliberately ignores single-frame hitches and CPU-bound slowness, because lowering resolution cannot fix those.

### Why is the first minute so choppy?

Shader compilation and pipeline creation. The pipeline cache warms up during play and is saved for the next launch. Do not judge a release from the first minute; compare steady-state runs at a similar thermal state.

### Why did FPS drop after 10–15 minutes?

Almost always thermal throttling or memory pressure. Check `thermal=` and `minAvail=` in the `[Meliora] mem` lines. A hot phone throttles; no setting fixes that. If free memory is collapsing, see the next question.

### Why does iOS kill the app in long sessions?

The process footprint can approach the device limit. The Memory Governor trims texture caches early, but it does not control buffers, the managed heap or MoltenVK/Metal allocations. The `[Meliora] mem` lines show `footprint`, `minAvail` and `texCache`; attach them to an issue if a kill happens. Restarting the app between long sessions starts from a smaller footprint.

### What is the Memory Governor?

A two-part mechanism: the Swift app reports how much memory iOS still allows, and the core shrinks its texture caches before the system kills the process. It is a safety valve for the caches it owns, not a general memory manager. Details are in [PERFORMANCE.md](PERFORMANCE.md#memory-governor).

### What is Unified Memory?

An experiment (`MELIORA_UNIFIED_MEMORY=1`) that opts into the unified-memory path on MoltenVK. It was tested and rejected: it did not reduce the peak footprint and kept the GPU thread busier. It is **not** enabled in release builds; see [PERFORMANCE.md](PERFORMANCE.md#unified-memory-meliora_unified_memory1).

### Does Target 30 FPS make a 60 FPS game smoother?

No, it makes it choppier. Use it only for games that natively target 30 FPS. The setting is applied at the next game launch, not mid-session.

### Does Smooth Pacing work?

Not yet. The core can follow a changed pacing target, but the app only applies settings at launch, so the switch is a no-op in 0.0.5. It is documented as reserved rather than silently doing something unexpected.

### Can I change graphics settings while a game is running?

Most settings are applied at game launch. Changing them mid-session may have no effect or may require a restart. This is deliberate: re-applying settings mid-session can invalidate caches and cause stalls.

### Why is the log full of `[Meliora]` lines?

They are the evidence behind every performance claim. The formats are documented in [PERFORMANCE.md](PERFORMANCE.md#reading-the-log) and [TESTING.md](TESTING.md#log-reference). They are local only; Meliora has no telemetry upload.

## Reporting and contributing

### How do I report a bug?

Open a [GitHub issue](https://github.com/Quancore21/Meliora/issues) with the bug report template. Include the device, iOS version, game and title ID, IPA version, settings, and the `[Meliora]` log lines around the event. See [TESTING.md](TESTING.md#attaching-logs-to-an-issue).

### How do I report a security problem?

Privately, through GitHub's vulnerability reporting. See [SECURITY.md](../SECURITY.md). Do not open a public issue for a vulnerability.

### Can I contribute?

Yes. Read [CONTRIBUTING.md](../CONTRIBUTING.md). Performance changes need a device log before and after; unverified claims are not merged.

### Can I request a game compatibility fix?

You can open an issue, but compatibility work belongs to the emulator core and is not Meliora's focus. A report without a log, device, version and settings cannot be acted on.

### Does Meliora have per-game profiles or a compatibility list?

No. The controller is deliberately game-agnostic, and compatibility is inherited from the emulator core. Per-game tuning and a curated list are maintenance-heavy and would duplicate upstream work; Meliora does not ship either in 0.0.5.

### How can I verify a release IPA?

Download it from the GitHub Releases page, compare its SHA-256 with the value in the release body, and prefer builds produced by this repository's Actions. Do not install IPAs from random sites. Release assets are unsigned; your sideloader signs them.
