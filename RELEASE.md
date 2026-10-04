# SoD2SE-Loader release record

## Authority and current state

- Version authority: `Core/SoD2SE.Core.cs` (`FrameworkInfo.Version`)
- Current authority value at E8 start: `0.6.0-preview`.
- Remote tags at E8 start: 0; create the first tag only in a separately authorized release.
- Release tag convention for a future approved release: `v<exact-version-value>`.
- No tag or release is created by this maintenance change.
- The matching Loader.exe, Core.dll, and GameApi.dll payload produced by the product package script.

## Release inputs

- Generate inputs only with `package.ps1`.
- Do not hand-edit `.work` outputs or upload files copied from a game install, a local research database, a private profile, or a third-party checkout.
- Record the source commit, version authority value, build/test results, package file list, and SHA-256 for each release asset.
- Preserve current assembly names, ABI, game behavior, compatibility guard, and install-relative paths.

## Acceptance

1. Confirm the selected source commit and version match the authority above; do not infer a version from a workspace manifest.
2. Run `build.ps1` and `test.ps1` (or the equivalent product entrypoint) at the pinned dependency revision.
3. Record each required offline check as PASS, FAIL, or SKIPPED with its reason. A missing protected asset or research database remains SKIPPED.
4. Generate the package through `package.ps1`; validate its manifest and payload against the product's release inputs.
5. Review the resulting file list, sizes, hashes, license/provenance, and install-relative paths before any separately authorized release action.
