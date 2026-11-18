# Releasing Meliora

This document describes how a Meliora release is versioned, built, verified and published. It is written for the maintainer, but it is public so users can see what a release actually guarantees.

The short version: **a release is a verified build with honest notes, not just a green workflow.**

## Versioning

- The app version is `MARKETING_VERSION` in `src/MeloNX/MeloNX.xcodeproj/project.pbxproj`. It appears in several build configurations and must be updated in all of them.
- The build number is `CURRENT_PROJECT_VERSION`. CI overrides it with `GITHUB_RUN_NUMBER`, so a CI-built IPA has a monotonically increasing build number. Local Xcode builds keep the project value.
- Releases use tags of the form `vX.Y.Z` (for example `v0.0.5`).
- A tag containing a hyphen (for example `v0.1.0-beta1`) is published as a pre-release by the workflow. Plain `vX.Y.Z` tags, including `0.x`, are stable releases.
- Published tags are never moved or force-pushed. If a release is wrong, publish a new patch version.
- The release commit title is exactly the version, for example `0.0.5`. Its body carries the changelog entry. Documentation and housekeeping commits use their own titles.

## What CI does

The `Build iOS IPA` workflow (`.github/workflows/ios.yml`) runs on pushes to `main`, on tags matching `v*`, on pull requests, and manually. It:

1. Refuses Git LFS pointer files and verifies the prebuilt MoltenVK/FFmpeg checksums in `distribution/ios/prebuilt.sha256`.
2. Builds the emulator core with .NET 8 NativeAOT for `ios-arm64`.
3. Checks that the core is arm64, is not suspiciously small, and exports the Meliora C entry points (`meliora_memory_configure`, `meliora_memory_report`, `meliora_memory_trim`, `meliora_tex_cache_bytes`, `meliora_save_caches`).
4. Builds the Xcode app unsigned, with Swift packages pinned to `Package.resolved` and the build number set to the workflow run number.
5. Packages two unsigned IPAs, embeds the entitlements with `ldid`, and verifies bundle ID, architectures, required libraries and entitlements.
6. Uploads the `meliora-ipa` artifact (30 days) and `build-logs` (14 days), and writes sizes and SHA-256 hashes to the job summary.

On a `v*` tag, the `release` job publishes a GitHub Release. If the release already exists, it **uploads the IPAs with `--clobber`** instead of failing.

### The tag-build caveat

Creating or pushing a tag starts a **new build**. Its IPA is not byte-identical to the main-branch build you may have verified: the build number is the tag run's number, and the archive is produced from the tagged commit. The release job will either create a new release with generated notes or overwrite the assets of an existing one.

That creates two supported release paths:

- **CI-published release.** Push the tag, let the workflow build and publish, then verify the tag run's IPAs and edit the release notes. The notes start as GitHub's generated notes and must be replaced with the curated version.
- **Manual release from verified main artifacts.** Create the release for the tag yourself, upload the IPAs you already verified, write the curated notes and checksums, then **cancel the tag-triggered workflow** before its release job runs. This is what 0.0.5 did: the release was assembled from the verified main build (run 44362057169, build 23), and the tag run was canceled so it could not clobber the assets. If you use this path, cancel promptly and confirm the assets and digests did not change afterwards.

Never let both paths race. If a tag run finishes after a manual upload, it will replace the assets and invalidate the hashes in the release body.

## Release checklist

### 1. Prepare the commit

- [ ] `MARKETING_VERSION` is the version being released in every configuration in `project.pbxproj`.
- [ ] [CHANGELOG.md](../CHANGELOG.md) has a section for the version, with the evidence and the open questions.
- [ ] The release notes draft exists: highlights, what changed, what is verified, what is not, known issues, install steps and checksums.
- [ ] No unrelated changes are mixed into the release commit.
- [ ] `git status` is clean after the commit.

Release commit title: the version only, for example:

```text
0.0.5
```

### 2. Build and verify

- [ ] Push the release commit to `main` and wait for the `Build iOS IPA` run to succeed.
- [ ] Download the `meliora-ipa` artifact. Do not use an IPA from an older run or from a local build unless you say so.
- [ ] Verify both IPAs:
  - `CFBundleIdentifier` is the expected bundle ID (`com.meliora.app` for official builds).
  - `CFBundleShortVersionString` matches the release and `CFBundleVersion` matches the run.
  - The main binary, `Ryujinx.Headless.SDL2.dylib`, `libMoltenVK.dylib`, `libavcodec.dylib` and `libavutil.dylib` are arm64.
  - `get-task-allow` and `increased-memory-limit` are present; `extended-virtual-addressing` is present only in the `-extended-va` IPA.
  - The core exports the five Meliora entry points.
  - Record the SHA-256 of both IPAs.
- [ ] Compare the warning baseline if the release touches C# code. A clean core build currently reports **0 errors and 49 warnings**; a different count is a prompt to look, not automatically a failure.
- [ ] If the release changes performance behaviour, run the device test plan in [TESTING.md](TESTING.md). If a device run is not available before publishing, the release notes must say which claims are not yet validated. Do not describe unverified thresholds as measured improvements.

### 3. Publish

Choose one path from [The tag-build caveat](#the-tag-build-caveat), not both.

- [ ] Create the tag `vX.Y.Z` on the release commit.
- [ ] Publish the release with a title such as `Meliora vX.Y.Z`.
- [ ] Upload both IPAs: `Meliora-X.Y.Z.ipa` and `Meliora-X.Y.Z-extended-va.ipa`.
- [ ] Write the curated notes. Generated notes alone are not enough.
- [ ] Include the SHA-256 of each asset in the release body.
- [ ] Mark the release as **latest** only if it is the version users should install.
- [ ] If you created the release manually, cancel the tag-triggered workflow before its release job can clobber the assets.
- [ ] If CI published the release, verify the uploaded assets and replace the generated notes with the curated ones.

### 4. After publishing

- [ ] Confirm the release page shows both assets, their sizes, and digests matching the release body.
- [ ] Confirm the tag points at the intended commit (`git rev-parse vX.Y.Z^{commit}`).
- [ ] Confirm the `main` branch still builds; a release does not freeze the repository.
- [ ] Update any documentation that refers to "the current release".
- [ ] Announce only what the notes claim. Link the release, not a direct IPA.

## Release notes template

```markdown
## Meliora X.Y.Z

One or two sentences: what this release is and the evidence behind it.

### What changed

- Change with the user-visible effect.
- Change with the reason, not just the mechanism.

### What is verified

- Device, iOS version, game and route used for validation.
- Which numbers were measured and which were not.

### Known issues

- Unverified thresholds, missing device validation, inherited compatibility limits.
- Anything a user could reasonably hit that is not fixed.

### Install

Unsigned IPA; sign with your own Apple ID. Standard IPA includes Increased Memory
Limit; `-extended-va` adds Extended Virtual Addressing for low-RAM devices.
JIT is required.

### Checksums

| File | SHA-256 |
| --- | --- |
| `Meliora-X.Y.Z.ipa` | `...` |
| `Meliora-X.Y.Z-extended-va.ipa` | `...` |
```

## Hotfixes

- Fix on `main` with a normal commit, bump the patch version, and repeat the checklist.

- Do not move an existing tag or silently replace assets. Users and mirrors cache them; a changed asset with the same name is indistinguishable from a compromised release.
- If an asset must be replaced before anyone could reasonably have downloaded it, update the release body's hashes in the same action and say so in the notes.

## Pre-releases

A tag with a hyphen (`v0.1.0-beta1`) is published as a pre-release. Use pre-releases for experiments that are not ready to be the default download, especially anything involving Unified Memory or a new controller calibration. A pre-release still needs verified assets and honest notes; "pre-release" is not a licence to skip verification.

## Things that are not releases

- A green `main` build is a build, not a release.
- A docs-only commit does not trigger CI (`paths-ignore` covers `**/*.md`, `docs/**` and issue templates). Run the workflow manually if you need a fresh artifact after a docs change.
- A local Xcode build is not a release artifact. It has a different build number and may use different signing settings.
- An IPA rebuilt from a tag after the release was published is a different artifact if its build number or commit differs. Re-verify it and update the hashes, or do not upload it.

## Related documents

- [Compile.md](../Compile.md) — building the core and the app, CI details, prebuilt library checksums.
- [TESTING.md](TESTING.md) — the device test matrix and acceptance criteria.
- [PERFORMANCE.md](PERFORMANCE.md) — the thresholds a release note may cite.
- [CHANGELOG.md](../CHANGELOG.md) — the historical record.
- [SECURITY.md](../SECURITY.md) — private vulnerability reporting.
