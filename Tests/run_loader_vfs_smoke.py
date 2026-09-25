import argparse
import tempfile
import ctypes
import json
import os
import subprocess
import uuid
from ctypes import wintypes as w
from pathlib import Path

parser = argparse.ArgumentParser(description="Run the actual SoD2SE launch path inside private USVFS; no real game is started.")
parser.add_argument("--mo2-path", type=Path, required=True)
parser.add_argument("--build-dir", type=Path, default=Path(__file__).resolve().parents[1] / "compiled")
parser.add_argument("--game-root", type=Path,
                    help="Real game folder used as the third probe target.  "
                         "Defaults to the same lookup Environment.ps1 uses, then "
                         "to the temporary root, so the test never depends on "
                         "one machine's drive layout.")
args = parser.parse_args()
root = Path(tempfile.mkdtemp(prefix="SoD2SE-vfs-smoke-"))
(root / "work").mkdir()


def detect_game_root():
    """Mirrors Environment.ps1: explicit path, env var, then a hint file."""
    explicit = os.environ.get("SOD2_GAME_DIR")
    if explicit and Path(explicit).is_dir():
        return Path(explicit)
    executable = os.environ.get("SOD2_GAME_EXE")
    if executable:
        candidate = Path(executable)
        if candidate.is_file():
            return candidate.parents[3] if len(candidate.parents) > 3 else candidate.parent
    for directory in (Path(__file__).resolve().parents[1], Path.cwd()):
        for name in ("SoD2SE.GamePath.txt", "SOD2SE_GAME_PATH.txt"):
            hint = directory / name
            if not hint.is_file():
                continue
            for line in hint.read_text(encoding="utf-8-sig").splitlines():
                value = line.strip()
                if not value or value.startswith("#"):
                    continue
                path = Path(value)
                return path if path.is_dir() else path.parent
    return None


game_root = args.game_root or detect_game_root() or root
mo = args.mo2_path.resolve()
build = args.build_dir.resolve()
harness = root / "LoaderVfsSmoke.exe"
subprocess.run([str(Path(os.environ["WINDIR"]) / "Microsoft.NET/Framework64/v4.0.30319/csc.exe"),
    "/nologo", "/platform:x64", "/target:exe", "/warnaserror+", "/out:" + str(harness),
    str(Path(__file__).with_name("LoaderVfsSmoke.cs"))], check=True)
dll_dirs = [os.add_dll_directory(str(p)) for p in [mo, mo / "dlls"]]
lib = ctypes.WinDLL(str(mo / "usvfs_x64.dll"))
lib.usvfsCreateParameters.restype = ctypes.c_void_p
lib.usvfsSetInstanceName.argtypes = [ctypes.c_void_p, ctypes.c_char_p]
lib.usvfsSetDebugMode.argtypes = [ctypes.c_void_p, w.BOOL]
lib.usvfsCreateVFS.argtypes = [ctypes.c_void_p]
lib.usvfsCreateVFS.restype = w.BOOL
lib.usvfsFreeParameters.argtypes = [ctypes.c_void_p]
lib.usvfsVirtualLinkFile.argtypes = [w.LPCWSTR, w.LPCWSTR, w.UINT]
lib.usvfsVirtualLinkFile.restype = w.BOOL
lib.usvfsVirtualLinkDirectoryStatic.argtypes = [w.LPCWSTR, w.LPCWSTR, w.UINT]
lib.usvfsVirtualLinkDirectoryStatic.restype = w.BOOL
lib.usvfsInitLogging.argtypes = [ctypes.c_bool]
lib.usvfsInitLogging(False)
token = uuid.uuid4().hex
params = lib.usvfsCreateParameters()
lib.usvfsSetInstanceName(params, ("sod2-test-" + token).encode())
lib.usvfsSetDebugMode(params, False)
assert lib.usvfsCreateVFS(params)
lib.usvfsFreeParameters(params)

class STARTUPINFO(ctypes.Structure):
    _fields_ = [("cb", w.DWORD), ("lpReserved", w.LPWSTR), ("lpDesktop", w.LPWSTR),
                ("lpTitle", w.LPWSTR), ("dwX", w.DWORD), ("dwY", w.DWORD),
                ("dwXSize", w.DWORD), ("dwYSize", w.DWORD), ("dwXCountChars", w.DWORD),
                ("dwYCountChars", w.DWORD), ("dwFillAttribute", w.DWORD),
                ("dwFlags", w.DWORD), ("wShowWindow", w.WORD), ("cbReserved2", w.WORD),
                ("lpReserved2", ctypes.c_void_p), ("hStdInput", w.HANDLE),
                ("hStdOutput", w.HANDLE), ("hStdError", w.HANDLE)]
class PROCESSINFO(ctypes.Structure):
    _fields_ = [("hProcess", w.HANDLE), ("hThread", w.HANDLE), ("dwProcessId", w.DWORD), ("dwThreadId", w.DWORD)]
lib.usvfsCreateProcessHooked.argtypes = [w.LPCWSTR, w.LPWSTR, ctypes.c_void_p, ctypes.c_void_p,
                                      w.BOOL, w.DWORD, ctypes.c_void_p, w.LPCWSTR,
                                      ctypes.POINTER(STARTUPINFO), ctypes.POINTER(PROCESSINFO)]
lib.usvfsCreateProcessHooked.restype = w.BOOL
kernel = ctypes.WinDLL("kernel32", use_last_error=True)
kernel.WaitForSingleObject.argtypes = [w.HANDLE, w.DWORD]
kernel.GetExitCodeProcess.argtypes = [w.HANDLE, ctypes.POINTER(w.DWORD)]
kernel.CloseHandle.argtypes = [w.HANDLE]
source = root / "work/vfs-probe-source.txt"
source.write_text("sod2-usvfs-probe", encoding="utf-8")
targets = [Path(os.environ["LOCALAPPDATA"]) / "StateOfDecay2/Saved/Paks" / ("probe-" + token + ".pak"),
           Path(os.environ["LOCALAPPDATA"]) / "StateOfDecay2/Saved/Cooked/WindowsNoEditor/StateOfDecay2/Content" / ("probe-" + token) / "test.uasset",
           game_root / ("probe-" + token + ".txt")]
output = root / "work/usvfs-result.json"
try:
    for target in targets:
        assert not target.exists(), str(target)
    saved = Path(os.environ["LOCALAPPDATA"]) / "StateOfDecay2/Saved"
    branch = root / "virtual-saved"
    for target in targets[:2]:
        leaf = branch / target.relative_to(saved)
        leaf.parent.mkdir(parents=True, exist_ok=True)
        leaf.write_text("sod2-usvfs-probe", encoding="utf-8")
    assert lib.usvfsVirtualLinkDirectoryStatic(str(branch), str(saved), 8)
    assert lib.usvfsVirtualLinkFile(str(source), str(targets[2]), 0)
    si, pi = STARTUPINFO(), PROCESSINFO()
    si.cb = ctypes.sizeof(si)
    cmd = ctypes.create_unicode_buffer(subprocess.list2cmdline([str(harness), str(build / "SoD2SE.Loader.exe"), str(output), *map(str, targets)]))
    ok = lib.usvfsCreateProcessHooked(None, cmd, None, None, False, 0x08000000, None, str(root), ctypes.byref(si), ctypes.byref(pi))
    assert ok, ctypes.get_last_error()
    assert kernel.WaitForSingleObject(pi.hProcess, 30000) == 0
    status = w.DWORD()
    kernel.GetExitCodeProcess(pi.hProcess, ctypes.byref(status))
    kernel.CloseHandle(pi.hThread); kernel.CloseHandle(pi.hProcess)
    assert status.value == 0, (root / "failure.txt").read_text(encoding="utf-8-sig") if (root / "failure.txt").exists() else status.value
    assert output.read_text() == "PASS"
    assert all(not target.exists() for target in targets)
    print("USVFS PASS: actual loader direct-launch path, child USVFS module, AppData Paks, nested Cooked, game root; no real targets created.")
finally:
    lib.usvfsDisconnectVFS()
