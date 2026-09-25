using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace SoD2SE
{
    public static class NativePluginModule
    {
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, UIntPtr size, uint type, uint protection);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool VirtualFreeEx(IntPtr process, IntPtr address, UIntPtr size, uint type);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] bytes, UIntPtr size, out UIntPtr written);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateRemoteThread(IntPtr process, IntPtr attributes, UIntPtr stack, IntPtr start, IntPtr argument, uint flags, IntPtr id);
        [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle, uint timeout);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetExitCodeThread(IntPtr thread, out uint code);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)] static extern IntPtr GetProcAddress(IntPtr module, string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibraryEx(string name, IntPtr file, uint flags);
        [DllImport("kernel32.dll")] static extern bool FreeLibrary(IntPtr module);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool GetModuleHandleEx(uint flags, IntPtr address, out IntPtr module);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern uint GetModuleFileName(IntPtr module, StringBuilder name, int capacity);

        public static void LoadAndStart(Process game, string library, string entryPoint, string channel)
        {
            if (game == null) throw new ArgumentNullException("game");
            if (String.IsNullOrWhiteSpace(entryPoint)) throw new ArgumentException("缺少原生插件入口。", "entryPoint");
            library = Path.GetFullPath(library);
            var process = OpenProcess(0x043a, false, game.Id);
            if (process == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "原生插件无法访问游戏进程。");
            try
            {
                IntPtr function = GetProcAddress(GetModuleHandle("kernel32.dll"), "LoadLibraryW");
                IntPtr owner;
                if (function == IntPtr.Zero || !GetModuleHandleEx(6, function, out owner)) throw new InvalidOperationException("无法定位 LoadLibraryW。");
                var name = new StringBuilder(32768);
                if (GetModuleFileName(owner, name, name.Capacity) == 0) throw new InvalidOperationException("无法定位系统模块。");
                var remoteOwner = FindModule(game, Path.GetFileName(name.ToString()));
                var load = new IntPtr(remoteOwner.ToInt64() + function.ToInt64() - owner.ToInt64());
                // HMODULE is 64 bits but remote thread exit codes are only 32 bits.
                // Confirm loading through the module list, not a truncated handle.
                Invoke(process, load, library);
                var remote = FindModule(game, Path.GetFileName(library));
                // Inspect only the export RVA locally; never initialize the game renderer in the loader.
                var local = LoadLibraryEx(library, IntPtr.Zero, 1);
                if (local == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取原生插件导出函数。");
                try
                {
                    var entry = GetProcAddress(local, entryPoint);
                    if (entry == IntPtr.Zero) throw new InvalidOperationException("原生插件缺少入口：" + entryPoint);
                    uint result = Invoke(process, new IntPtr(remote.ToInt64() + entry.ToInt64() - local.ToInt64()), channel);
                    if (result != 0) throw new InvalidOperationException("原生插件 " + entryPoint + " 初始化失败，状态 " + result + "。");
                }
                finally { FreeLibrary(local); }
            }
            finally { CloseHandle(process); }
        }
        static IntPtr FindModule(Process process, string name)
        {
            process.Refresh();
            foreach (ProcessModule module in process.Modules)
                if (String.Equals(module.ModuleName, name, StringComparison.OrdinalIgnoreCase)) return module.BaseAddress;
            throw new InvalidOperationException("游戏进程中找不到模块：" + name);
        }
        static uint Invoke(IntPtr process, IntPtr function, string argument)
        {
            byte[] data = Encoding.Unicode.GetBytes(argument + "\0");
            var remote = VirtualAllocEx(process, IntPtr.Zero, (UIntPtr)data.Length, 0x3000, 4);
            if (remote == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            IntPtr thread = IntPtr.Zero;
            bool safeToFree = true;
            try
            {
                UIntPtr written;
                if (!WriteProcessMemory(process, remote, data, (UIntPtr)data.Length, out written) || written.ToUInt64() != (ulong)data.Length)
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                thread = CreateRemoteThread(process, IntPtr.Zero, UIntPtr.Zero, function, remote, 0, IntPtr.Zero);
                if (thread == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (WaitForSingleObject(thread, 15000) != 0)
                {
                    safeToFree = false; // The remote thread may still read this argument. Never terminate it.
                    throw new TimeoutException("原生插件初始化超时。请退出游戏后重试。");
                }
                uint code;
                if (!GetExitCodeThread(thread, out code)) throw new Win32Exception(Marshal.GetLastWin32Error());
                return code;
            }
            finally
            {
                if (thread != IntPtr.Zero) CloseHandle(thread);
                if (safeToFree) VirtualFreeEx(process, remote, UIntPtr.Zero, 0x8000);
            }
        }
    }
}
