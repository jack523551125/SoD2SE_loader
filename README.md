# SoD2SE Loader

**Compatibility repository.** The active Rust Loader is now part of [SoD2SE — State of Decay 2 System Extender](https://github.com/jack523551125/SoD2SE), under `Rust/loader`. New native development and issues belong there. This repository is mounted under workspace `Compatibility/SoD2SE-Loader` and preserves the managed Loader/Core/GameApi snapshot and release history for rollback. The instructions below describe that legacy product.

[简体中文说明](README.zh-CN.md)

SoD2SE Loader starts *State of Decay 2*, waits for its game process, and loads compatible SoD2SE DLL plugins. This repository contains the loader and the Core and Game API sources needed to build it. It does **not** include game files, plugin DLLs, the native MCM renderer, or an MO2 game-support plugin.

The current Game API targets game build **16535856**. The loader can start a different game build, but memory patches and native hooks require an exact compatible target; an unknown build must not be treated as compatible. This is a preview project, not an official Undead Labs or Microsoft release.

## Build and check

Requirements: Windows x64, PowerShell, and the .NET Framework 4.x C# compiler (`csc.exe`, available with the Windows .NET Framework/Developer Pack). Run from a PowerShell terminal:

```powershell
& .\build.ps1
& .\test.ps1
& .\package.ps1
```

`package.ps1` creates a fresh ZIP under `.work` by default. It never overwrites an existing package and does not install or launch the game.

The three runtime files appear in `.work\build` for an independent checkout or the workspace's `.work\products\SoD2SE-Loader`:

```text
SoD2SE.Loader.exe
SoD2SE.Core.dll
SoD2SE.GameApi.dll
```

`test.ps1` builds these files and runs the loader self-test plus a configuration/language test in temporary directories. It does not start or attach to the game. The optional [MO2 USVFS smoke test](Tests/README.md) also uses a fake child process rather than the game.

## Install and launch

Copy the three runtime files into the game root, alongside `StateOfDecay2.exe`. Install compatible plugin DLLs separately under `Plugins\`; this repository does not provide gameplay mods. Run `SoD2SE.Loader.exe` from the game root. By default it starts the game's launcher executable directly, then waits for the actual game process. It does not use Steam's `-applaunch` command; the Steam edition may still require Steam to be ready for the game itself.

The normal executable is a Windows GUI program, so it does not open a command window. It writes `SoD2SE-launch.log` beside the loader, falling back to the system temporary directory if necessary. For a safe check without launching the game:

```powershell
& .\artifacts\bin\SoD2SE.Loader.exe --self-test
& .\artifacts\bin\SoD2SE.Loader.exe --diagnose-launch
```

The loader also supports `--game-exe <path-or-game-directory>`, `--game-args=<arguments>`, `--direct-main`, `--attach`, and `--console`. `--attach` targets a running game; use it only when you intend to load plugins into that process. For MO2, use a compatible State of Decay 2 support plugin that starts the loader with `--mo2 --direct-main` and supplies the virtualized game executable. MO2 must keep its USVFS environment attached to the game process. [Chinese usage notes](README.zh-CN.md) explain the command line and layout in more detail.

## Repository layout

| Path | Purpose |
| --- | --- |
| `Loader/` | Game startup, process tracking, plugin discovery, logging, and MO2 session checks. |
| `Core/` | Shared plugin ABI, configuration, runtime services, and safe patch application. |
| `GameApi/` | Game-build-specific capabilities and patch metadata. |
| `Tests/` | Offline self-tests and an optional private MO2 USVFS test. |
| `build.ps1`, `test.ps1` | Reproducible build and offline verification. |

`FrameworkInfo.Version` in `Core/SoD2SE.Core.cs` is the single version source used to stamp all three assemblies. The retired loader-owned MCM overlay is intentionally absent: MCM is a separate plugin. Build outputs, logs, local game path hints, MO2 state, and user configuration are excluded by `.gitignore`.

## Scope and release status

This source snapshot is intended for the fixed game build above. It has been checked by offline tests; it has not been accepted as a new public binary release. The native settings-page integration in the separate MCM plugin still needs in-game acceptance. If you publish binaries, package the three matching files together and verify them with the game and intended MO2 profile before marking a release stable.

No open-source license has been selected for this repository. Add a `LICENSE` file after deciding what rights to grant others; public visibility alone does not grant reuse rights.


## Development, offline validation, and packaging

Windows x64, PowerShell 7, and the .NET Framework 4.x C# compiler (`csc.exe`).

From an independent checkout, use the repository-owned entrypoints:

```powershell
.\build.ps1
.\test.ps1
.\package.ps1
```


From the workspace root, `Automation/dev.ps1` dispatches to those same repository-owned entrypoints:

```powershell
.\Automation\dev.ps1 build SoD2SE-Loader
.\Automation\dev.ps1 check SoD2SE-Loader
.\Automation\dev.ps1 test SoD2SE-Loader
.\Automation\dev.ps1 package SoD2SE-Loader
```

Run `package.ps1` to generate package inputs below the repository's ignored `.work` directory. Checks never launch or attach to the game. See [RELEASE.md](RELEASE.md) for version authority, tag convention, required acceptance evidence, and release inputs.
