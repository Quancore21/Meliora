# Changelog

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
