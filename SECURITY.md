# Security policy

Meliora is an emulator for iPhone and iPad. It handles untrusted game files, keys, firmware and save data, and it runs native code on the device. Security reports are taken seriously, but this is a volunteer project with no bug bounty and no guaranteed response time.

## Reporting a vulnerability

**Do not open a public issue for a security problem.**

Use GitHub's private vulnerability reporting:

1. Go to the [Security tab](https://github.com/Quancore21/Meliora/security) of the repository.
2. Choose **Report a vulnerability**.
3. Describe the problem and include the details listed below.

If private reporting is unavailable, open a public issue that only asks the maintainer to contact you, without disclosing the vulnerability. Do not include exploit details, keys or personal data in a public issue.

## What to include

- Meliora version and IPA file name, or the commit you built from.
- Device model and iOS version.
- The affected component: Swift app, emulator core, graphics stack, build scripts or CI.
- A description of the impact and the conditions required to trigger it.
- Reproduction steps or a proof of concept. Keep it minimal; do not attach copyrighted game files.
- Relevant logs with keys, firmware paths and personal information removed.

If the report involves a third-party component (MoltenVK, SDL2, FFmpeg, .NET, Ryujinx core code), say so; it may need to be reported upstream as well.

## Scope

In scope:

- The Swift app in `src/MeloNX` and the Meliora-specific core changes.
- The build and release pipeline in `.github/workflows/ios.yml` and `distribution/ios`.
- Handling of game files, keys, firmware and save data by the app.
- Anything that can escape the app sandbox, execute code without the user's intent, or exfiltrate local data.

Out of scope:

- Game compatibility, performance and crashes that are not security issues. Use the bug report template instead.
- Problems in iOS, StikDebug or a sideloader. Report those to their maintainers.
- Issues that require a jailbroken device, a debugger, or an already-compromised signing identity, unless Meliora makes the impact materially worse.
- The fact that the IPA is unsigned. That is expected; you sign it yourself.
- Requests for help obtaining games, keys or firmware.

## Supported versions

Security fixes target the latest release on the `main` branch. Older releases are not maintained. If you are on an older version, reproduce on the latest release before reporting when possible.

## What to expect

- The maintainer will acknowledge a valid report when possible and investigate.
- Please give a reasonable window for a fix before public disclosure. A coordinated disclosure date can be agreed in the private report.
- Credit is given in the release notes if you want it; say so in the report.
- There is no monetary reward.
