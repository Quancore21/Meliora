# Contributing to Meliora

Meliora is an unofficial Nintendo Switch emulator for iPhone and iPad, built on Ryujinx, MeloNX and MeloVertex. Contributions are welcome: bug reports, device logs, documentation fixes, code and testing. This document explains what a useful contribution looks like and how changes are reviewed.

Please read it before opening a large pull request. It is short, and it saves time for everyone.

## Quick links

- [Code style](docs/coding-guidelines/coding-style.md) — C# style used by the core.
- [Pull request guide](docs/workflow/pr-guide.md) — review rules and merge process.
- [Building](Compile.md) — GitHub Actions, macOS and Windows core validation.
- [Testing](docs/TESTING.md) — device test matrix and log templates.
- [Architecture](docs/ARCHITECTURE.md) — how the app and core fit together.
- [Performance model](docs/PERFORMANCE.md) — controller constants and calibration.
- [Releasing](docs/RELEASING.md) — versioning and release verification.

## Reporting issues

### Search first

Check the [open issues](https://github.com/Quancore21/Meliora/issues) before filing a new one. If an issue already exists, add your log and device details there instead of opening a duplicate.

### Use the bug report template

A useful report contains:

- **What happened** and **what you expected**, in one or two sentences.
- **Reproduction steps**, as short as possible. For a game problem, the scene, route and save file if you can share one.
- **Device and OS:** exact model (for example iPhone 17, `iPhone18,3`), iOS version, RAM if known.
- **Meliora version:** the IPA file name or the version shown in the app.
- **Game and title ID**, plus update/DLC versions.
- **Settings:** docked mode, aspect ratio, resolution scale, VSync, MacroHLE, shader cache, texture recompression, and any *Advanced Options* toggles you changed.
- **The log** from the run, especially the `[Meliora] mem`, `[Meliora] adaptive` and `[Meliora] gpu` lines around the event. Include the whole log when possible; a single line rarely explains a decision.
- **Whether the run is test A, B, C, D or E** from [TESTING.md](docs/TESTING.md), and whether the device was warm or cold (`thermal=`).

Do not paste keys, firmware, personal paths or copyrighted game files. The in-app log should not contain keys, but check before attaching it.

### Performance reports

Performance reports need evidence, not adjectives. "It stutters" cannot be compared with a baseline. Say what the FPS was, what the median and `gpuBusy` were, how long the session ran and what the thermal state was. If you claim a regression, attach a log from the previous release on the same route.

Reports that cannot be reproduced with a log, device, version and settings will be closed as incomplete. That is not a judgement of the report; it is the only way to compare releases honestly.

### Security reports

Do **not** open a public issue for a vulnerability. Use [SECURITY.md](SECURITY.md).

### What not to report

- Requests for games, keys or firmware. They are closed without discussion.
- Problems caused by modified builds, injected libraries, other emulators or a broken sideloader.
- Game compatibility comparisons with desktop Ryujinx or other iOS emulators; the rendering path and memory limits are different.
- "Game X does not work" without a log, device, version and settings.

## Feature requests

Open an issue describing the problem, not only the proposed solution. Explain who benefits and how it fits Meliora's scope: iOS-specific performance, stability, diagnostics and the app experience.

Meliora does not maintain a game compatibility list, per-game profiles or a separate emulator core. Compatibility work belongs upstream. A request that duplicates upstream work, or that requires maintaining a game database, is likely to be declined.

## Contributing code

### Before you start

- For anything non-trivial, open an issue first so the direction can be agreed before you invest time.
- Keep changes focused. A bug fix should not include a formatting sweep, a dependency bump and a new feature.
- Do not commit code you did not write unless its licence is compatible and it is attributed correctly. See [Copying code](#copying-code).

### Workflow

1. Create or find an issue for the work. Trivial fixes can skip this step.
2. Fork the repository and create a branch off `main`.
3. Make and commit your changes.
4. Build and test:
   - **Core (Windows or Linux):**
     ```powershell
     dotnet build src/Ryujinx.Headless.SDL2/Ryujinx.Headless.SDL2.csproj -c Release --no-incremental
     ```
     A clean tree builds with **0 errors and 49 warnings**. Compare with that baseline instead of expecting zero warnings.
   - **Full iOS build:** push the branch and let the `Build iOS IPA` workflow run, or follow [Compile.md](Compile.md) on a Mac.
   - **Performance changes:** test on a real device with the routine in [TESTING.md](docs/TESTING.md). CI cannot measure FPS, thermal behaviour or memory pressure.
5. Open a pull request against `main`. Describe the problem, the change, the tests you ran, and any behaviour you could not verify.
6. Address review feedback with new commits; do not rewrite published history just to hide review changes.
7. If CI fails, check the failing job before asking for a review. The `build-logs` artifact is the first place to look.

### Performance changes

Meliora's releases are calibrated from logs. A performance change must include:

- A before/after log from the same game, route, settings, IPA variant and a similar thermal state.
- One variable changed at a time. If you change the floor and the busy threshold in one PR, the log cannot tell which one mattered.
- The rationale: what the old behaviour did, what the new behaviour should do, and what evidence supports it.
- Updates to [docs/PERFORMANCE.md](docs/PERFORMANCE.md) and [docs/TESTING.md](docs/TESTING.md) when a constant, log format or acceptance criterion changes.

Do not present an unverified threshold as a measured improvement. "Expected to reduce time at the floor" is honest; "fixes the floor" without a device run is not.

### Documentation changes

- Documents are in English and describe the code on `main`.
- Check for stale version numbers, release names and claims when touching a document that mentions a release.
- Performance statements must be checkable from a log. Link to the source of a constant instead of duplicating it without a reference.
- Do not invent licence terms, support commitments or compatibility guarantees.

### What we will not merge

- Changes that alter licensing files, copyright headers or attribution without a compelling legal reason.
- Keys, firmware, game files or tools for obtaining them.
- Large unrelated rewrites, drive-by formatting, or a new dependency without justification.
- A compatibility list or per-game tuning database.
- Performance claims without a reproducible log.
- Changes that silently make a documented setting do nothing; if a setting is reserved, say so in the UI and the docs.

## Commit messages

Format:

```text
Summarize the change in 50 characters or less

Explain why the change is needed and what it affects. Wrap lines at
72 characters or less. Mention the evidence (a log, a test, a device)
if the change depends on it.

Fix #42
```

Guidelines:

- Use the imperative mood: "Fix texture cache floor", not "Fixed" or "Fixes".
- Keep the summary specific. "Fix crash" is not useful; "Fix trim when cache is below floor" is.
- One logical change per commit where practical.
- Release commits are titled with the version only, for example `0.0.5`; the body carries the changelog entry.
- Documentation batches may use `docs` or `housekeeping` as the title.

## Review process

- Every pull request needs a maintainer review and a green CI run before merge.
- This is a volunteer project with a single maintainer; review times vary. A polite bump after a week is fine.
- Use draft pull requests for work in progress. Mark the PR ready when you want a review.
- Resolve review conversations only after the feedback is addressed or both sides agree.
- Squash merges are the default. Keep the PR title meaningful; it becomes the merge commit.
- If a PR becomes inactive or no longer applies, it may be closed. You can reopen it if you want to continue.

## Copying code

Meliora contains code under AGPL-3.0, the MeloNX License and MIT. If you copy code from another project:

- The source licence must be compatible with the file it lands in.
- Keep the original copyright and licence headers intact.
- Add the attribution to [NOTICE.md](NOTICE.md) when required.
- Say where the code came from in the PR description.

Do not copy code from projects with incompatible licences, and do not remove attribution to make a file look original.

## Licensing

By contributing, you agree that your contribution is licensed under the same terms as the file it modifies. Meliora is distributed under AGPL-3.0; [LICENSE.txt](LICENSE.txt) also preserves the previous MeloNX License and the Ryujinx MIT notice. See [LICENSE.txt](LICENSE.txt) and [NOTICE.md](NOTICE.md) for the exact terms and third-party attributions.

If you believe a licensing or attribution problem exists, open an issue instead of changing the files yourself.

## Questions

If something in this document is unclear, open an issue with the `question` label or ask in the issue you are already discussing. Do not ask the MeloNX, MeloVertex or Ryujinx teams for support with Meliora; this is an unofficial fork.
