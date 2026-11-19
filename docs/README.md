# Meliora documentation

This directory contains the documentation for Meliora, an unofficial Nintendo Switch emulator for iPhone and iPad. The project is a fork of MeloVertex, MeloNX and Ryujinx; the documents below describe what this fork changes, how it is built and how its performance claims are tested.

**Current release: 0.0.5.** The adaptive thresholds are calibrated from the first 0.0.4 field log; device validation is in progress. See [PERFORMANCE.md](PERFORMANCE.md) for the current values and [TESTING.md](TESTING.md) for the validation routine.

## Start here

| I want to... | Read |
| --- | --- |
| Install Meliora and run a game | [README](../README.md), then [TROUBLESHOOTING.md](TROUBLESHOOTING.md) |
| Understand why a game is slow or the image is soft | [PERFORMANCE.md](PERFORMANCE.md), then [TESTING.md](TESTING.md) |
| Report a reproducible performance problem | [TESTING.md](TESTING.md#attaching-logs-to-an-issue) |
| Build the app or the core | [Compile.md](../Compile.md) |
| Understand how the app and the core fit together | [ARCHITECTURE.md](ARCHITECTURE.md) |
| Contribute code or documentation | [CONTRIBUTING.md](../CONTRIBUTING.md), [coding style](coding-guidelines/coding-style.md), [PR guide](workflow/pr-guide.md) |
| Publish a release | [RELEASING.md](RELEASING.md) |
| Check what changed between releases | [CHANGELOG.md](../CHANGELOG.md) |
| Know what is licensed and why | [NOTICE.md](../NOTICE.md), [LICENSE.txt](../LICENSE.txt) |

## Document map

### User documentation

- [README](../README.md) — what Meliora is, requirements, installation, settings, environment variables and diagnostics.
- [TROUBLESHOOTING.md](TROUBLESHOOTING.md) — installation, JIT, entitlements, crashes, memory pressure, audio and input.
- [FAQ.md](FAQ.md) — short answers to the questions that come up most often.
- [TESTING.md](TESTING.md) — how to reproduce the release baselines and attach useful logs.

### Performance and testing

- [PERFORMANCE.md](PERFORMANCE.md) — the adaptive render-scale controller, the memory governor, their constants, calibration history and log interpretation.
- [TESTING.md](TESTING.md) — test matrix (short session, long session, Target 30, closed Unified Memory experiment), reference baselines, acceptance criteria and reporting template.

### Architecture and development

- [ARCHITECTURE.md](ARCHITECTURE.md) — the Swift app layer, the C# core, the render loop, the memory governor, frame pacing, thread QoS and the Swift↔C bridge.
- [Compile.md](../Compile.md) — building with GitHub Actions, building on macOS, validating the C# core on Windows, prebuilt library checksums.
- [CONTRIBUTING.md](../CONTRIBUTING.md) — issues, pull requests, commit conventions and licensing.
- [coding-guidelines/coding-style.md](coding-guidelines/coding-style.md) — C# style used by the core.
- [workflow/pr-guide.md](workflow/pr-guide.md) — the pull-request workflow for this repository.

### Release management

- [RELEASING.md](RELEASING.md) — versioning, release checklist, CI, assets, checksums, known issues and the tag-build caveat.
- [CHANGELOG.md](../CHANGELOG.md) — release history with the field evidence behind each change.
- [NOTICE.md](../NOTICE.md) — provenance and third-party attributions.
- [SECURITY.md](../SECURITY.md) — private vulnerability reporting.

## Documentation conventions

- Documents are written in English, in the present tense, and describe the code as it is on `main`.
- Performance statements must be checkable from a game log. If a number is not backed by a log or a reproducible measurement, it does not belong in a release note.
- Thresholds and constants live in code (`AdaptivePerformance.cs`, `MemoryGovernor.cs`, `AutoDeleteCache.cs`, `SurfaceFlinger.cs`, `MelioraGovernor.swift`); documents summarise them and link to the source. When a constant changes, update [PERFORMANCE.md](PERFORMANCE.md) and [TESTING.md](TESTING.md) in the same change.
- Known limitations are documented next to the feature, not hidden in a separate file. The release notes repeat them for people who never open the docs.
- Some documents under `docs/coding-guidelines` and `docs/workflow` were inherited from Ryujinx. They were adapted where they referred to the upstream organisation; the C# style itself is unchanged and still applies to the core.
- The project does not maintain a game compatibility list. Compatibility is inherited from the emulator core and is not guaranteed by Meliora.
