# Building Meliora

## Option A: GitHub Actions (no Mac needed)

1. Fork or push this repository to GitHub.
2. Open the **Actions** tab, choose **Build iOS IPA** and click **Run workflow** (it also runs on every push to `main`).
3. When it finishes, download the `meliora-ipa` artifact. It contains two **unsigned** IPAs (standard and `-extended-va`).
4. Sign and install an IPA with your own Apple ID (AltStore, SideStore, Sideloadly, Plume Impactor).

Pushing a tag such as `v0.0.5` also publishes the IPAs as a GitHub Release. Tags containing a hyphen (for example `v0.1.0-beta1`) are marked as pre-releases; plain `vX.Y.Z` tags, including `0.x`, are stable releases.

Docs-only changes (`**/*.md`, `docs/**`, issue templates) do not trigger the workflow. Run it manually from the *Actions* tab if you need a fresh artifact after a documentation change. See [docs/RELEASING.md](docs/RELEASING.md) for the full release checklist and the tag-build caveat.

What the workflow does, in order:

1. Checks that the prebuilt libraries are real binaries and match `distribution/ios/prebuilt.sha256`.
2. Builds the emulator core with .NET 8 NativeAOT (`distribution/ios/xc-compile.sh`).
3. Builds the Xcode project unsigned, with Swift packages pinned to `Package.resolved`.
4. Packages two IPAs, embeds the entitlements with `ldid` and verifies the result (bundle ID, architectures, required libraries, entitlements).

Build logs are always uploaded as the `build-logs` artifact, which is the first thing to look at when a run fails.

### Changing a prebuilt library on purpose

`libMoltenVK.dylib`, `libavcodec.dylib` and `libavutil.dylib` live in `src/MeloNX/MeloNX/Dependencies/Dynamic Libraries/` as ordinary files (not Git LFS). The workflow refuses to build if they differ from `distribution/ios/prebuilt.sha256`. After replacing one deliberately, regenerate the file:

```sh
D="src/MeloNX/MeloNX/Dependencies/Dynamic Libraries"
shasum -a 256 "$D/libMoltenVK.dylib" "$D/libavcodec.dylib" "$D/libavutil.dylib" > distribution/ios/prebuilt.sha256
```

Run this from the repository root and commit the updated file together with the new library.

The workflow fails if a pointer file or a mismatched binary is committed. It also checks that the core exports the Meliora C entry points (`meliora_memory_configure`, `meliora_memory_report`, `meliora_memory_trim`, `meliora_tex_cache_bytes`, `meliora_save_caches`) and that both IPAs have the expected bundle ID, architectures, libraries and entitlements.

## Validating the C# core on Windows

The Swift app and the iOS NativeAOT publish need macOS, but the core can be compiled and checked with the .NET 8 SDK on Windows or Linux:

```powershell
dotnet build src/Ryujinx.Headless.SDL2/Ryujinx.Headless.SDL2.csproj -c Release --no-incremental
```

A clean tree builds with **0 errors and 49 warnings**. Compare the warning count with that baseline instead of expecting zero warnings. This validates the C# code, not the app, the IPA or device performance. After a core change, also run the relevant device test from [docs/TESTING.md](docs/TESTING.md) before claiming a performance effect.

## Option B: on a Mac

### Prerequisites

- macOS with **Xcode 26** (the project uses iOS 26 SDK features)
- **.NET 8 SDK**: <https://dotnet.microsoft.com/en-us/download/dotnet/8.0>

### Steps

```sh
git clone <your fork URL> Meliora
cd Meliora
sudo xcode-select --switch /Applications/Xcode.app/Contents/Developer
open src/MeloNX/MeloNX.xcodeproj
```

1. In Xcode select the **MeloNX** target, then **Signing & Capabilities**.
2. Change the **Bundle Identifier** to something unique to you, for example `com.<your-name>.Meliora`, and pick your **Team** (free or paid).
3. Connect your iPhone or iPad and select it as the run destination. Do **not** use a Simulator.
4. Press **Run**. The first build is slow: the *Ryujinx* target runs `dotnet publish` (NativeAOT) for the emulator core.
5. To use JIT, enable it as described in the README.

### Troubleshooting

- **"Undefined symbols" on the first build:** in the MeloNX target's *General* tab find `Ryujinx.Headless.SDL2.dylib` and set it to **Embed & Sign**, then build again.
- **".NET not found":** install the .NET 8 SDK, or point `MELIORA_DOTNET` at the `dotnet` binary you want to use:
  `MELIORA_DOTNET=/path/to/dotnet xcodebuild ...`
- **Core build fails after switching branches:** run `dotnet clean` and `dotnet restore` in the repository root, then build again.
- **The app builds but a game will not launch:** this is usually JIT, the memory entitlement or keys/firmware, not the build. See [docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md).
- **A tag build replaced your release assets:** the release job uploads with `--clobber`. See the tag-build caveat in [docs/RELEASING.md](docs/RELEASING.md) before creating a release manually and pushing a tag for the same version.
