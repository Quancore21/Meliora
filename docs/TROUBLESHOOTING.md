# Troubleshooting

This guide covers the problems that are specific to running Meliora on iPhone and iPad. If a step does not help, collect the log described at the end and open an issue with it.

## First checks

Before changing settings, confirm:

1. **The IPA matches your device.** Use `Meliora-<version>.ipa` unless you specifically need Extended Virtual Addressing; the extended-VA build is for low-RAM devices and paid accounts or TrollStore.
2. **JIT is enabled.** Meliora cannot run games without it. On iOS 26 and newer, use [StikDebug](https://github.com/StephenDev0/StikDebug).
3. **The Increased Memory Limit entitlement is present.** The app warns when it is missing. Your sideloader must add it to the App ID when signing.
4. **Keys and firmware are installed** and the game is a dump of your own copy.
5. **You are on a supported iOS version.** The app targets iOS/iPadOS 18.1 or newer; current builds are tested on iOS 27.2.

## Installation and signing

### The IPA is unsigned

Every Meliora IPA is unsigned. Sign it with your own Apple ID using AltStore, SideStore, Sideloadly, Plume Impactor or a paid developer account. Meliora does not provide certificates or provisioning profiles.

### "Unable to Install" or the app disappears

- Free Apple developer accounts expire after seven days; re-sign and reinstall.
- A bundle ID conflict with another installed app can block installation. Change the bundle ID in your sideloader or remove the conflicting app.
- If the app installs but does not open, trust the developer certificate in iOS Settings → General → VPN & Device Management, then try again.
- If you changed the IPA or the signing identity, remove the old app first. Reinstalling over a differently signed copy can fail.

### "Increased Memory Limit is missing"

The entitlement is required on every device. Add `com.apple.developer.kernel.increased-memory-limit` to the App ID in your sideloader, re-sign and reinstall. If your sideloader has a preset for Meliora, use it. The app deliberately warns instead of silently running with less memory.

### Should I use the extended-VA IPA?

Only if you need it:

- iPhones with less than 4 GB RAM, or iPads with less than 8 GB RAM.
- A paid developer account or TrollStore, because the entitlement requires it.

On a device with enough memory, the standard IPA is the safer choice. Do not compare performance between the two variants; the memory limit is different.

## JIT

JIT is mandatory. The app starts, but a game will not boot correctly without it.

- On iOS 26 and newer, [StikDebug](https://github.com/StephenDev0/StikDebug) is the supported route.
- Enable JIT for Meliora before launching a game. If the JIT attach fails, fix that first; changing emulator settings will not help.
- The app may use dual-mapped JIT (`DUAL_MAPPED_JIT`) on supported iOS versions. This is app-level setup; if it fails, try the *Dual Mapped JIT* toggle in the app's settings, or follow StikDebug's troubleshooting.
- A debugger attached from Xcode is the other supported route for developers.
- JIT problems are not Meliora-specific. Report StikDebug failures to StikDebug, not to the MeloNX or Ryujinx teams.

## Keys, firmware and games

- Meliora ships with no keys, firmware or games.
- Add your `prod.keys`, `title.keys` and firmware through the in-app file picker.
- Use a complete dump of your own game. Converted, trimmed or downloaded files are unsupported and often fail in ways that look like emulator bugs.
- Updates and DLC must match the base game's title ID and region.
- Issues asking for games, keys or firmware are closed.

## The game does not boot

Try, in order:

1. Confirm JIT and the memory entitlement.
2. Confirm keys/firmware and that the game appears with the correct title ID.
3. Return graphics settings to the released defaults: docked off, stretched aspect, resolution scale 1.0, VSync on, MacroHLE off, shader cache on.
4. Disable *Texture Recompression* if you enabled it and the game crashes early.
5. Try another game to separate a title-specific problem from a setup problem.
6. Attach the log to an issue. The last lines before the failure usually identify whether the problem is keys, firmware, JIT, memory or the GPU.

A black screen with audio can be a graphics-path problem; a freeze before any audio is more often keys, firmware or JIT. Neither is conclusive without the log.

## Performance and stutter

### The first minutes are not representative

Shader compilation and pipeline creation cause hitches on first play. The pipeline cache warms up over a session; a cold start can be much choppier than the same route later. Do not judge a release from the first minute.

### The image became soft

*Adaptive Resolution* lowers the render scale when the game is sustained-GPU-bound. The floor is x0.90 of the starting scale in 0.0.5, so the softening is smaller than in 0.0.4 but still visible in heavy scenes. If you prefer a fixed image, turn the toggle off; you trade the softened image for heavier-scene dips.

The controller does not react to single-frame hitches or CPU-bound slowness. If the picture is soft while the frame rate is stable, check the log: a `step down` should have a median above budget and `gpuBusy ≥ 75%`. If it does not, that is a bug; attach the lines.

### The game is still slow at the floor

`at floor` means the game is still GPU-bound at the lowest allowed scale. Options:

- Turn *Adaptive Resolution* off if you prefer a stable image and accept the dips.
- Try *Target 30 FPS* only for games that natively target 30; it makes 60 FPS games choppier.
- Try the MVK toggles one at a time, with the same route and thermal state. Do not change several at once.
- Check `thermal=` in the mem line. A hot device throttles; no setting fixes physics.

### GPU warnings

`GPU processing thread is too slow` means the GPU thread missed its deadline. A few warnings per session are normal under thermal throttling; a large increase is worth reporting. Compare rates per minute, not raw counts over different session lengths.

### CPU-bound slowness

`slow frames but GPU idle` means the controller saw sustained slowness but the GPU thread was not saturated. Lowering resolution cannot fix a CPU-bound scene, so the controller leaves the scale alone. Report the line if the game is unplayable; it is evidence, not a bug by itself.

## Memory pressure and long sessions

iOS can terminate the app when the process footprint approaches the device limit. The `[Meliora] mem` lines show the trend:

- `footprint=` is the process footprint.
- `minAvail=` is the worst free memory in the 10-second window.
- `texCache=` is the texture-cache size.

What the governor can do:

- Shrink the texture caches when free memory falls below the headroom.
- Trim earlier (3/4 of the headroom in 0.0.5).
- Run a non-blocking GC after an emergency trim.

What it cannot do:

- Reclaim buffers, the managed heap or MoltenVK/Metal allocations directly.
- Change the device's memory limit.

Practical steps:

- Restart the app between long sessions; a fresh process starts with a smaller footprint.
- Close other memory-hungry apps.
- Lower the render resolution or disable expensive settings if the session is long.
- If the app is killed, attach the last mem lines and the session length. A kill at the ~6 GB limit with a small `texCache=` means the pressure is outside the texture cache; that is exactly the case the telemetry is meant to expose.

Unified Memory (`MELIORA_UNIFIED_MEMORY=1`) is an experiment, not a release setting. Do not enable it for routine play.

## Audio

- Check the device volume, silent mode and Bluetooth output first.
- Try a wired output or the built-in speaker to isolate the route.
- Audio problems that appear only in one game are usually title-specific; attach the log and the game name.
- Audio problems that appear in every game are app-level; include the iOS version and output route.

## Controllers

- Pair Bluetooth controllers in iOS Settings before launching the game.
- If a controller is not detected, disconnect and pair it again, then restart the app.
- The app may expose a virtual controller; check the input settings if touch controls do not appear.
- Attach the log and state the controller model and connection type. Input issues are hard to diagnose without knowing the route.

## Crashes, freezes and jetsam

1. Note what you were doing and how long the session had run.
2. Export the log from the in-app Logs view.
3. Include the last `[Meliora] mem` and `[Meliora] adaptive` lines before the event.
4. State the device, iOS version, game, title ID, IPA version and settings.
5. If iOS killed the app, say so; a jetsam kill looks different from a crash.
6. Open an issue with the log attached. Do not paste keys, firmware or personal paths.

## Reinstalling and app data

Reinstalling or re-signing can remove the app's container, including saves, keys and firmware. Back up anything you cannot re-add before deleting the app. Meliora does not provide a cloud save service.

## Where to get help

- Documentation: [README](../README.md), [FAQ](FAQ.md), [TESTING](TESTING.md), [PERFORMANCE](PERFORMANCE.md).
- Bugs and performance reports: [GitHub Issues](https://github.com/Quancore21/Meliora/issues). Use the bug report template and attach the log.
- Security problems: see [SECURITY.md](../SECURITY.md); do not open a public issue for a vulnerability.
- Meliora is an unofficial fork. Do not ask the MeloNX, MeloVertex or Ryujinx teams for support with Meliora.

## What not to report

- Requests for games, keys or firmware.
- Problems caused by modified builds, sideloader injections or other emulators.
- "Game X does not work" without a log, device, version and settings.
- Compatibility comparisons with desktop Ryujinx or other iOS emulators; the rendering path and memory limits are different.
