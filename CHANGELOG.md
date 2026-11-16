# Changelog

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
