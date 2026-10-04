# SoD2SE-Loader agent rules

This repository is compatibility/rollback source under workspace Compatibility/SoD2SE-Loader. New native Loader changes belong to SoD2SE/Rust/loader. Preserve this managed snapshot and historical releases.

Read README.md and DESIGN.md before editing. This repository owns the loader and its Core/GameApi release snapshot; inspect its Git status and preserve user work. Use `build.ps1`, `test.ps1`, and `package.ps1` or the workspace dispatcher. `Core/SoD2SE.Core.cs` remains the version authority; do not change it unless a version change is explicitly in scope. Offline checks must not launch or attach to the game.
