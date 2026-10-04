# SoD2SE-Loader design boundary

The owner authorized consolidation into SoD2SE and moving this repository to workspace Compatibility/SoD2SE-Loader. Rust Loader/Runtime/GameApi are canonical in SoD2SE. This repository retains only the historical managed release boundary below; do not add a competing native implementation.

Product: SoD2SE-Loader. Current status: preview. Canonical source is managed by `Projects/SoD2SE-Loader`. The machine-readable entry is project.toml; version authority is `Projects/SoD2SE-Loader/Core/SoD2SE.Core.cs`.

This is an independent Git repository. Its build/release scripts must work independently of workspace navigation. Do not assume another repository's Core or GameApi snapshot can replace its files.

Invariants: no game-file mutation; preserve version guards, ABI, existing package paths and save behavior. Gameplay changes are outside the directory migration. Production access uses GameApi; evidence remains research.

Validation: use the root Automation/dev.ps1 commands. Offline tests do not certify gamepad coverage, localization completeness, save compatibility or game integration. Existing implementation is C#/C++ or Python; Rust and C ABI adoption is a separate task.

## Responsibilities and interactions

Loader process startup and MO2 environment handling are packaged with its own Core/GameApi snapshot. The repository excludes native MCM and gameplay plugins. Offline self-test and language/configuration smoke tests do not launch a game.
