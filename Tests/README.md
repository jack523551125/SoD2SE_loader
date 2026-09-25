# Offline tests

Run `..\test.ps1` from PowerShell. It builds Core, Game API, and the loader, then runs the loader's `--self-test` and the MCM configuration/language smoke test. These checks do not start or attach to *State of Decay 2*.

If you have a local MO2 installation, Python 3 can additionally exercise the real loader launch path inside a **private** USVFS instance with a fake child process:

```powershell
python .\Tests\run_loader_vfs_smoke.py --mo2-path "C:\path\to\Mod Organizer 2" --build-dir .\artifacts\bin
```

The optional test does not start the game and does not require a game installation. It probes virtual files in temporary paths and removes its USVFS instance when finished. Supply the directory containing `usvfs_x64.dll` as `--mo2-path`.
