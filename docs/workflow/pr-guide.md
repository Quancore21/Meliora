# Pull request guide

This guide describes how changes reach `main` in this repository. It replaces the inherited Ryujinx rules; some of their principles still apply, but the review and release process is Meliora's.

## Rules

- Changes are usually made through pull requests. The maintainer may commit directly for small fixes, documentation batches, housekeeping and release commits.
- A pull request needs a maintainer review and a green CI run before merge.
- This is a single-maintainer volunteer project. There is no two-approval rule and no guaranteed review time.
- Every pull request targets the `main` branch of this repository, not an upstream repository.

## Keep changes reviewable

- Do not mix unrelated changes in one pull request. A code style sweep should never be bundled with a bug fix.
- Keep the diff as small as the problem allows. If a refactor is needed to fix the bug, explain why in the description.
- Follow the existing style of the file you are changing. The general C# style is in [coding-style.md](../coding-guidelines/coding-style.md).
- Avoid adding external dependencies unless not doing so would introduce significant complexity. Justify any new dependency in the description and expect a discussion.
- Use a draft pull request for work in progress if you want early CI feedback. Mark it ready when it should be reviewed.
- Rebase your branch when it conflicts with `main`; do not merge `main` into the branch just to resolve a trivial conflict.
- Address review feedback with new commits. Do not force-push a branch while a review is in progress unless the reviewer asks for it.
- Resolve review conversations only after the feedback is addressed or both sides agree.

## What the CI checks

The `Build iOS IPA` workflow runs on pull requests unless the change only touches Markdown, `docs/**` or issue templates. It:

- verifies the prebuilt library checksums,
- builds the emulator core with NativeAOT for `ios-arm64`,
- checks the Meliora entry points are exported,
- builds the app unsigned,
- packages and verifies both IPAs,
- uploads the IPAs as `meliora-ipa` and the build logs as `build-logs`.

A green run means the app compiles and the IPAs are structurally valid. It does **not** mean a game runs well. Performance, thermal and memory behaviour must be tested on a device using [TESTING.md](../TESTING.md).

If CI fails, read the failing step before asking for a review. The `build-logs` artifact contains the core build and Xcode logs. A docs-only pull request may have no CI run; that is expected.

## Performance and behaviour changes

A pull request that changes the adaptive controller, memory governor, pacing, QoS or any performance constant must include:

- a before/after log from the same game, route, settings, IPA variant and a similar thermal state;
- the exact lines that show the old and new behaviour;
- a statement of what is verified and what is not;
- updates to [PERFORMANCE.md](../PERFORMANCE.md) and [TESTING.md](../TESTING.md) if a constant, log format or acceptance criterion changed.

Do not change several thresholds at once. A log with two changes cannot show which one mattered. If the change cannot be tested on a device before review, say so clearly; it may be merged as an experiment, but the release notes must not describe it as measured.

## Licensing and attribution

- Do not submit pull requests that alter licensing files, copyright headers or attribution unless the change is legally necessary. Open an issue first.
- Code copied from another project must have a compatible licence, keep its original headers, and be attributed in [NOTICE.md](../../NOTICE.md) when required.
- By contributing, you agree that your change is licensed under the same terms as the file it modifies.

## Commit and PR titles

- PR titles should describe the change, not the branch name. The title becomes the squash-merge commit.
- Commit summaries are imperative and specific: "Fix trim below texture cache floor", not "Fixes".
- Release commits are titled with the version only, for example `0.0.5`.
- Documentation batches may use `docs` or `housekeeping`.

## Review and merge

1. The maintainer reads the description and the diff.
2. CI must be green, unless the change is docs-only or the failure is unrelated and explained.
3. Behaviour changes need device evidence; unavailable evidence is a reason to mark the change experimental, not to overstate it.
4. The PR is squash-merged. The branch can be deleted afterwards.
5. If a PR becomes inactive or no longer applies, it may be closed. Reopen it if you want to continue.

## Security

Do not open a public pull request or issue for a vulnerability. Follow [SECURITY.md](../../SECURITY.md).

## Related documents

- [CONTRIBUTING.md](../../CONTRIBUTING.md) — reporting issues and the contribution workflow.
- [Compile.md](../../Compile.md) — building the core and the app.
- [TESTING.md](../TESTING.md) — the device test matrix.
- [RELEASING.md](../RELEASING.md) — how a merged change becomes a release.
