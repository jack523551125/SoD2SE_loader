using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SoD2SE
{
    internal sealed class PatchBusyException : InvalidOperationException
    {
        public PatchBusyException() : base("游戏线程正在使用待修改代码，未写入补丁。请回到主菜单后重试。") { }
    }

    internal static class ThreadSafety
    {
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenThread(uint access, bool inherit, uint id);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetThreadContext(IntPtr thread, IntPtr context);
        [DllImport("ntdll.dll")] static extern int NtQueryInformationThread(IntPtr thread, int informationClass, IntPtr information, int size, out int returned);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] bytes, IntPtr size, out IntPtr read);

        static byte[] Read(IntPtr process, long address, int count)
        {
            var bytes = new byte[count];
            IntPtr read;
            if (!ReadProcessMemory(process, new IntPtr(address), bytes, (IntPtr)count, out read) || read.ToInt64() != count)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法检查线程活动栈；未写入补丁。");
            return bytes;
        }

        static bool InPatch(long address, long module, IList<PatchSpec> patches)
        {
            foreach (var patch in patches)
                if (address >= module + patch.Rva && address < module + patch.Rva + patch.OriginalBytes.Length) return true;
            return false;
        }

        // Called only after the entire target process is suspended. Checking RIP
        // alone is insufficient: a callee can return into a rewritten block.
        // Scan the active stack conservatively for such return addresses. An
        // ambiguous stack or inaccessible thread fails closed; no context is set.
        public static void EnsureQuiescent(IntPtr process, int processId, IntPtr module, IList<PatchSpec> patches)
        {
            if (patches.Count == 0) return;
            if (IntPtr.Size != 8) throw new PlatformNotSupportedException("线程补丁检查仅支持 x64。");
            var allocation = Marshal.AllocHGlobal(1232 + 15);
            var basic = Marshal.AllocHGlobal(48);
            var context = new IntPtr((allocation.ToInt64() + 15) & ~15L);
            try
            {
                using (var target = Process.GetProcessById(processId))
                {
                    var threads = target.Threads;
                    if (threads.Count == 0) throw new InvalidOperationException("未找到可检查的游戏线程。");
                    foreach (ProcessThread item in threads)
                    {
                        using (item)
                        {
                            var thread = OpenThread(0x0048, false, checked((uint)item.Id)); // GET_CONTEXT | QUERY_INFORMATION
                            if (thread == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法检查游戏线程；未写入补丁。");
                            try
                            {
                                Marshal.Copy(new byte[1232], 0, context, 1232);
                                Marshal.WriteInt32(context, 48, 0x00100001); // AMD64 CONTEXT_CONTROL
                                if (!GetThreadContext(thread, context)) throw new Win32Exception(Marshal.GetLastWin32Error(), "读取线程上下文失败；未写入补丁。");
                                long rip = Marshal.ReadInt64(context, 248);
                                long rsp = Marshal.ReadInt64(context, 152);
                                if (InPatch(rip, module.ToInt64(), patches)) throw new PatchBusyException();
                                int returned;
                                if (NtQueryInformationThread(thread, 0, basic, 48, out returned) < 0 || returned < 48)
                                    throw new InvalidOperationException("无法取得线程栈边界；未写入补丁。");
                                long teb = Marshal.ReadIntPtr(basic, 8).ToInt64();
                                var bounds = Read(process, teb + 8, 16); // NT_TIB StackBase, StackLimit
                                long top = BitConverter.ToInt64(bounds, 0), bottom = BitConverter.ToInt64(bounds, 8);
                                if (rsp < bottom || rsp > top || top - rsp > 32 * 1024 * 1024 || (rsp & 7) != 0)
                                    throw new InvalidOperationException("线程栈范围不明确；未写入补丁。");
                                for (long address = rsp; address < top; )
                                {
                                    int count = (int)Math.Min(65536, top - address);
                                    var stack = Read(process, address, count);
                                    for (int offset = 0; offset + 8 <= count; offset += 8)
                                        if (InPatch(BitConverter.ToInt64(stack, offset), module.ToInt64(), patches)) throw new PatchBusyException();
                                    address += count;
                                }
                            }
                            finally { CloseHandle(thread); }
                        }
                    }
                }
            }
            finally { Marshal.FreeHGlobal(basic); Marshal.FreeHGlobal(allocation); }
        }
    }
}
