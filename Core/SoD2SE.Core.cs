using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;

namespace SoD2SE
{
    public sealed class PatchSpec
    {
        readonly byte[] original;
        readonly byte[] replacement;
        readonly byte[] guardOriginal;

        public string Name { get; private set; }
        public int Rva { get; private set; }
        public int GuardRva { get; private set; }
        // Public callers receive copies; the framework uses the internal views below.
        public byte[] Original { get { return (byte[])original.Clone(); } }
        public byte[] Replacement { get { return (byte[])replacement.Clone(); } }
        public byte[] GuardOriginal { get { return (byte[])guardOriginal.Clone(); } }
        internal byte[] OriginalBytes { get { return original; } }
        internal byte[] ReplacementBytes { get { return replacement; } }
        internal byte[] GuardOriginalBytes { get { return guardOriginal; } }

        public PatchSpec(string name, int rva, byte[] original, byte[] replacement, int guardRva, byte[] guardOriginal)
        {
            if (String.IsNullOrWhiteSpace(name) || original == null || replacement == null || guardOriginal == null)
                throw new ArgumentException("补丁描述不完整");
            if (original.Length == 0 || original.Length != replacement.Length || guardOriginal.Length == 0)
                throw new ArgumentException("补丁字节长度不合法");
            if (rva < 0 || guardRva < 0)
                throw new ArgumentException("RVA 不能为负数");
            if (original.SequenceEqual(replacement))
                throw new ArgumentException("补丁替换字节不能与原始字节相同");
            long patchEnd = (long)rva + original.Length;
            long guardEnd = (long)guardRva + guardOriginal.Length;
            if (guardRva > rva || guardEnd < patchEnd)
                throw new ArgumentException("补丁上下文必须覆盖写入范围");
            Name = name;
            Rva = rva;
            GuardRva = guardRva;
            this.original = (byte[])original.Clone();
            this.replacement = (byte[])replacement.Clone();
            this.guardOriginal = (byte[])guardOriginal.Clone();
        }
    }

    public interface IGameSession
    {
        Process GameProcess { get; }
        IntPtr ModuleBase { get; }
        string ExePath { get; }
        int Apply(string pluginId, string expectedSha256, IList<PatchSpec> patches);
        int Restore(string pluginId, IList<PatchSpec> patches);
    }

    public interface ISoD2Plugin
    {
        int ApiVersion { get; }
        string Id { get; }
        string Name { get; }
        string Description { get; }
        void Initialize(IGameSession session);
        void Shutdown();
    }

    // Optional health reporting lets the loader keep running when one fixed
    // version capability is unavailable.  Older plugins remain compatible.
    public interface IPluginRuntimeStatus
    {
        bool IsActive { get; }
        string Status { get; }
    }

    public static class FrameworkInfo
    {
        public const string Version = "0.6.0-preview";
        public const int PluginApiVersion = 1;
        public const int ArchitectureApiVersion = 2;
        public const string GameProcessName = "StateOfDecay2-Win64-Shipping";
        public const string GameExecutableName = GameProcessName + ".exe";
        public const string GameLauncherName = "StateOfDecay2.exe";
        // The folder the game owns next to its executable and under Documents:
        // <GameDir>\StateOfDecay2\Binaries\Win64\ and %Documents%\StateOfDecay2.
        public const string GameDataFolderName = "StateOfDecay2";
        // Keep the process launcher independent from Steam.  A user can still
        // provide an optional command line through SOD2_GAME_ARGS when a
        // particular distribution needs one.
        public const string GameLaunchArguments = "";
        public const string SteamAppId = "495420";
        public const string TargetSha256 = "EBF0A73E164BAA701F74655585F262F8E38A32B1F6DC5057303489BA5B333ECE";

        public static bool IsSha256(string value)
        {
            if (String.IsNullOrWhiteSpace(value) || value.Length != 64) return false;
            foreach (var character in value)
                if (!Uri.IsHexDigit(character)) return false;
            return true;
        }

        public static bool IsValidPluginId(string value)
        {
            if (String.IsNullOrWhiteSpace(value) || value.Length > 64) return false;
            if (!IsAsciiLetterOrDigit(value[0])) return false;
            for (int i = 1; i < value.Length; i++)
            {
                char character = value[i];
                if (!IsAsciiLetterOrDigit(character) && character != '-' && character != '_' && character != '.') return false;
            }
            return true;
        }

        static bool IsAsciiLetterOrDigit(char character)
        {
            return (character >= 'a' && character <= 'z') || (character >= 'A' && character <= 'Z') ||
                   (character >= '0' && character <= '9');
        }

        public static string HashFile(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("文件路径不能为空", "path");
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
        }

        public static string ResolveGameExecutable(string explicitPath, string baseDirectory)
        {
            var candidates = new List<string>();
            if (String.IsNullOrWhiteSpace(baseDirectory)) baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            if (!String.IsNullOrWhiteSpace(explicitPath))
            {
                AddGamePathCandidates(candidates, explicitPath, baseDirectory);
                return candidates.FirstOrDefault(File.Exists);
            }

            AddGamePathCandidates(candidates, Environment.GetEnvironmentVariable("SOD2_GAME_EXE"), baseDirectory);
            AddConfiguredCandidate(candidates, Path.Combine(baseDirectory, "SoD2SE.GamePath.txt"), baseDirectory);
            AddSearchRoots(candidates, baseDirectory);
            AddSearchRoots(candidates, Environment.CurrentDirectory);

            foreach (var candidate in candidates)
                if (File.Exists(candidate)) return candidate;
            return null;
        }

        public static string ResolveGameWorkingDirectory(string gameExecutable)
        {
            if (String.IsNullOrWhiteSpace(gameExecutable)) throw new ArgumentException("游戏 EXE 路径不能为空", "gameExecutable");
            var directory = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(gameExecutable)));
            for (var current = directory; current != null; current = current.Parent)
            {
                var nestedGameRoot = Path.Combine(current.FullName, GameDataFolderName, "Content");
                if (Directory.Exists(nestedGameRoot)) return current.FullName;
                if (String.Equals(current.Name, GameDataFolderName, StringComparison.OrdinalIgnoreCase))
                {
                    var parent = current.Parent;
                    if (parent != null && File.Exists(Path.Combine(parent.FullName, GameLauncherName)))
                        return parent.FullName;
                    if (Directory.Exists(Path.Combine(current.FullName, "Content"))) return current.FullName;
                }
            }
            return directory.FullName;
        }

        public static string ResolveGameLauncher(string gameExecutable)
        {
            if (String.IsNullOrWhiteSpace(gameExecutable)) throw new ArgumentException("游戏 EXE 路径不能为空", "gameExecutable");
            var workingDirectory = ResolveGameWorkingDirectory(gameExecutable);
            var launcher = Path.Combine(workingDirectory, GameLauncherName);
            return File.Exists(launcher) ? launcher : gameExecutable;
        }

        static void AddSearchRoots(IList<string> candidates, string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return;
            try
            {
                var current = new DirectoryInfo(Path.GetFullPath(value.Trim().Trim('"')));
                if (File.Exists(current.FullName)) current = current.Parent;
                // The loader may be in the game root or in a small subfolder.
                for (int depth = 0; current != null && depth < 5; depth++, current = current.Parent)
                    AddGamePathCandidates(candidates, current.FullName, current.FullName);
            }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
        }

        static void AddGamePathCandidates(IList<string> candidates, string value, string baseDirectory)
        {
            if (String.IsNullOrWhiteSpace(value)) return;
            try
            {
                value = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
                if (!Path.IsPathRooted(value)) value = Path.Combine(baseDirectory, value);
                value = Path.GetFullPath(value);
                if (File.Exists(value))
                {
                    var fileName = Path.GetFileName(value);
                    if (String.Equals(fileName, GameExecutableName, StringComparison.OrdinalIgnoreCase) ||
                        String.Equals(fileName, GameLauncherName, StringComparison.OrdinalIgnoreCase))
                        AddCandidate(candidates, value);
                    return;
                }
                if (!Directory.Exists(value)) return;

                // Shipping executable first keeps diagnostics and compatibility
                // checks pointed at the real game module.  ResolveGameLauncher
                // can still select the root bootstrap when it exists.
                AddCandidate(candidates, Path.Combine(value, GameDataFolderName, "Binaries", "Win64", GameExecutableName));
                AddCandidate(candidates, Path.Combine(value, "Binaries", "Win64", GameExecutableName));
                AddCandidate(candidates, Path.Combine(value, GameExecutableName));
                AddCandidate(candidates, Path.Combine(value, GameLauncherName));
            }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
        }

        static void AddCandidate(IList<string> candidates, string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return;
            try
            {
                var path = Path.GetFullPath(value);
                if (!candidates.Contains(path, StringComparer.OrdinalIgnoreCase)) candidates.Add(path);
            }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
        }

        static void AddConfiguredCandidate(IList<string> candidates, string configPath, string baseDirectory)
        {
            if (!File.Exists(configPath)) return;
            try
            {
                foreach (var line in File.ReadAllLines(configPath))
                {
                    var value = line.Trim();
                    if (value.Length == 0 || value.StartsWith("#", StringComparison.Ordinal)) continue;
                    AddGamePathCandidates(candidates, value, baseDirectory);
                    return;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        public static Process FindRunningGame()
        {
            var matches = Process.GetProcessesByName(GameProcessName);
            if (matches.Length > 1)
            {
                foreach (var match in matches) match.Dispose();
                throw new InvalidOperationException("检测到多个腐烂国度 2 进程，无法安全选择目标。");
            }
            return matches.Length == 0 ? null : matches[0];
        }
    }

    public sealed class GameSession : IGameSession, IRuntimeGameSession, IDisposable
    {
        internal const int MemoryPageSize = 0x1000;
        readonly Process process;
        readonly object sync = new object();
        readonly IntPtr moduleBase;
        readonly string exePath;
        readonly int moduleSize;
        readonly int processId;
        IntPtr processHandle;
        readonly Dictionary<string, IList<PatchSpec>> active = new Dictionary<string, IList<PatchSpec>>(StringComparer.OrdinalIgnoreCase);
        readonly GameRuntime runtime;
        bool disposed;

        public GameSession(Process process) : this(process, new GenericGameApi()) { }

        public GameSession(Process process, IGameVersionApi api)
        {
            if (process == null) throw new ArgumentNullException("process");
            if (api == null) throw new ArgumentNullException("api");
            this.process = process;
            // Accessing MainModule here makes a failed/partial process attach fail before any write.
            if (process.HasExited) throw new InvalidOperationException("游戏进程已经退出。");
            var module = process.MainModule;
            if (module == null || module.BaseAddress == IntPtr.Zero || module.ModuleMemorySize <= 0 || String.IsNullOrWhiteSpace(module.FileName))
                throw new InvalidOperationException("无法取得游戏主模块信息。");
            moduleBase = module.BaseAddress;
            exePath = module.FileName;
            moduleSize = module.ModuleMemorySize;
            processId = process.Id;
            processHandle = Native.OpenProcessForPatch(processId);
            try
            {
                if (Native.GetProcessId(processHandle) != processId)
                    throw new InvalidOperationException("进程句柄与目标进程不一致。");
            }
            catch
            {
                Native.CloseHandle(processHandle);
                processHandle = IntPtr.Zero;
                throw;
            }
            runtime = new GameRuntime(this, api, new HookBroker(this, api), new GameEventBus(),
                new PropertyModifierStack(), PauseServiceFactory.For(this));
            try { api.Detect(this); }
            catch (Exception error)
            {
                api.Capabilities.DisableAll("能力检测失败：" + error.Message);
                Trace.WriteLine("游戏版本 API 能力检测失败：" + error.Message);
            }
        }

        public Process GameProcess { get { return process; } }
        public IntPtr ModuleBase { get { return moduleBase; } }
        public string ExePath { get { return exePath; } }
        public GameRuntime Runtime { get { return runtime; } }

        public int Apply(string pluginId, string expectedSha256, IList<PatchSpec> patches)
        {
            lock (sync)
            {
                EnsureUsable();
                ValidatePluginId(pluginId);
                var snapshot = SnapshotPatches(patches);
                VerifyTarget(expectedSha256);
                ValidateRanges(snapshot);
                IList<PatchSpec> existing;
                if (active.TryGetValue(pluginId, out existing))
                {
                    if (!PatchSetsEqual(existing, snapshot))
                        throw new InvalidOperationException("插件 ID 已经注册了另一组补丁：" + pluginId);
                    return Native.ApplySuspended(processHandle, moduleBase, snapshot, true);
                }
                EnsureNoActiveOverlap(snapshot);
                int changed = Native.ApplySuspended(processHandle, moduleBase, snapshot, true);
                active.Add(pluginId, snapshot);
                return changed;
            }
        }

        public int Restore(string pluginId, IList<PatchSpec> patches)
        {
            lock (sync)
            {
                if (disposed || process.HasExited) return 0;
                if (String.IsNullOrWhiteSpace(pluginId) || patches == null || patches.Count == 0) return 0;
                IList<PatchSpec> applied;
                if (!active.TryGetValue(pluginId, out applied)) return 0;
                var snapshot = SnapshotPatches(patches);
                if (!PatchSetsEqual(applied, snapshot))
                    throw new InvalidOperationException("还原补丁与插件已应用的补丁不一致：" + pluginId);
                ValidateRanges(applied);
                int changed = Native.ApplySuspended(processHandle, moduleBase, applied, false);
                active.Remove(pluginId);
                return changed;
            }
        }

        void EnsureUsable()
        {
            if (disposed) throw new ObjectDisposedException("GameSession");
            if (process.HasExited) throw new InvalidOperationException("游戏进程已经退出。");
            if (processHandle == IntPtr.Zero) throw new ObjectDisposedException("GameSession");
            if (Native.GetProcessId(processHandle) != processId)
                throw new InvalidOperationException("进程句柄已经指向其他进程，已拒绝补丁操作。");
        }

        void VerifyTarget(string expectedSha256)
        {
            var expected = String.IsNullOrWhiteSpace(expectedSha256) ? FrameworkInfo.TargetSha256 : expectedSha256.Trim();
            if (!FrameworkInfo.IsSha256(expected))
                throw new ArgumentException("目标 SHA256 格式无效", "expectedSha256");
            var actual = FrameworkInfo.HashFile(ExePath);
            if (!String.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("游戏 EXE SHA256 不匹配。期望 " + expected + "，实际 " + actual + "；未修改游戏进程。");
        }

        void ValidateRanges(IList<PatchSpec> patches)
        {
            if (moduleSize <= 0) throw new InvalidOperationException("无法取得游戏模块大小。");
            foreach (var patch in patches)
            {
                if (patch == null) throw new InvalidOperationException("补丁列表包含空项目。");
                long patchEnd = (long)patch.Rva + patch.OriginalBytes.Length;
                long guardEnd = (long)patch.GuardRva + patch.GuardOriginalBytes.Length;
                if (patch.Rva < 0 || patch.GuardRva < 0 || patchEnd > moduleSize || guardEnd > moduleSize)
                    throw new InvalidOperationException("补丁地址超出游戏模块范围：" + patch.Name);
                if (patch.GuardRva > patch.Rva || guardEnd < patchEnd)
                    throw new InvalidOperationException("补丁上下文必须覆盖写入范围：" + patch.Name);
                long pageOffset = (moduleBase.ToInt64() + patch.Rva) & (MemoryPageSize - 1);
                if (pageOffset + patch.OriginalBytes.Length > MemoryPageSize)
                    throw new InvalidOperationException("补丁不能跨越内存页：" + patch.Name);
            }
        }

        static void ValidatePluginId(string pluginId)
        {
            if (!FrameworkInfo.IsValidPluginId(pluginId))
                throw new ArgumentException("插件 ID 必须以字母或数字开头，并且只允许字母、数字、-、_、.（最长 64 个字符）", "pluginId");
        }

        static IList<PatchSpec> SnapshotPatches(IList<PatchSpec> patches)
        {
            if (patches == null || patches.Count == 0) throw new ArgumentException("插件没有提供补丁", "patches");
            var snapshot = new List<PatchSpec>(patches.Count);
            foreach (var patch in patches)
            {
                if (patch == null) throw new ArgumentException("补丁列表包含空项目", "patches");
                snapshot.Add(patch);
            }
            for (int i = 0; i < snapshot.Count; i++)
                for (int j = i + 1; j < snapshot.Count; j++)
                    if (RangesOverlap(snapshot[i].Rva, snapshot[i].OriginalBytes.Length, snapshot[j].Rva, snapshot[j].OriginalBytes.Length))
                        throw new InvalidOperationException("同一插件的补丁地址重叠：" + snapshot[i].Name + " / " + snapshot[j].Name);
            return snapshot;
        }

        void EnsureNoActiveOverlap(IList<PatchSpec> patches)
        {
            foreach (var item in active)
                EnsurePatchSetIsolation(item.Key, item.Value, patches);
        }

        internal static void EnsurePatchSetIsolation(string activePluginId, IList<PatchSpec> appliedPatches, IList<PatchSpec> requestedPatches)
        {
            foreach (var applied in appliedPatches)
                foreach (var requested in requestedPatches)
                {
                    // A transaction normalizes only its own patch bytes when checking
                    // guards. Reject both directions before writing so each plugin can
                    // be initialized and restored independently, in either order.
                    if (RangesOverlap(applied.Rva, applied.OriginalBytes.Length, requested.GuardRva, requested.GuardOriginalBytes.Length) ||
                        RangesOverlap(requested.Rva, requested.OriginalBytes.Length, applied.GuardRva, applied.GuardOriginalBytes.Length))
                        throw new InvalidOperationException("补丁写入范围与已加载插件的写入或上下文校验范围冲突：" + activePluginId + " / " + applied.Name + " / " + requested.Name);
                }
        }

        static bool RangesOverlap(int firstRva, int firstLength, int secondRva, int secondLength)
        {
            long firstEnd = (long)firstRva + firstLength;
            long secondEnd = (long)secondRva + secondLength;
            return firstRva < secondEnd && secondRva < firstEnd;
        }

        static bool PatchSetsEqual(IList<PatchSpec> first, IList<PatchSpec> second)
        {
            if (first.Count != second.Count) return false;
            for (int i = 0; i < first.Count; i++)
            {
                var a = first[i];
                var b = second[i];
                if (a.Rva != b.Rva || a.GuardRva != b.GuardRva || !String.Equals(a.Name, b.Name, StringComparison.Ordinal) ||
                    !a.OriginalBytes.SequenceEqual(b.OriginalBytes) || !a.ReplacementBytes.SequenceEqual(b.ReplacementBytes) ||
                    !a.GuardOriginalBytes.SequenceEqual(b.GuardOriginalBytes)) return false;
            }
            return true;
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed) return;
                foreach (var item in active.ToArray())
                {
                    try { Restore(item.Key, item.Value); }
                    catch (Exception error) { Trace.WriteLine("还原插件补丁失败：" + error.Message); }
                }
                active.Clear();
                var handle = processHandle;
                processHandle = IntPtr.Zero;
                try { Native.CloseHandle(handle); }
                finally { disposed = true; }
            }
        }
    }

    internal static class Native
    {
        const uint ProcessQueryInformation = 0x0400;
        const uint ProcessVmRead = 0x0010;
        const uint ProcessVmWrite = 0x0020;
        const uint ProcessVmOperation = 0x0008;
        const uint ProcessSuspendResume = 0x0800;
        const uint ProcessAccess = ProcessQueryInformation | ProcessVmRead | ProcessVmWrite | ProcessVmOperation | ProcessSuspendResume;
        const uint PageReadWrite = 0x04;
        const uint PageExecuteReadWrite = 0x40;
        const uint MemCommit = 0x1000;
        const uint MemReserve = 0x2000;
        const uint MemRelease = 0x8000;
        [DllImport("ntdll.dll")] static extern int NtSuspendProcess(IntPtr process);
        [DllImport("ntdll.dll")] static extern int NtResumeProcess(IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int processId);
        [DllImport("kernel32.dll", EntryPoint = "GetProcessId", SetLastError = true)] static extern uint GetProcessIdWin32(IntPtr process);
        [DllImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)] static extern bool CloseHandleWin32(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, IntPtr size, out IntPtr read);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] buffer, IntPtr size, out IntPtr written);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool VirtualProtectEx(IntPtr process, IntPtr address, UIntPtr size, uint protection, out uint old);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool FlushInstructionCache(IntPtr process, IntPtr address, UIntPtr size);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint allocationType, uint protection);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool VirtualFree(IntPtr address, UIntPtr size, uint freeType);

        public static IntPtr OpenProcessForPatch(int processId)
        {
            var handle = OpenProcess(ProcessAccess, false, processId);
            if (handle == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法打开游戏进程。请用与游戏相同的权限启动加载器。");
            return handle;
        }

        public static void CloseHandle(IntPtr handle)
        {
            if (handle != IntPtr.Zero) CloseHandleWin32(handle);
        }

        public static int GetProcessId(IntPtr handle)
        {
            if (handle == IntPtr.Zero) throw new ArgumentException("进程句柄不能为空", "handle");
            uint processId = GetProcessIdWin32(handle);
            if (processId == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取进程句柄身份。");
            return checked((int)processId);
        }

        static byte[] Read(IntPtr handle, IntPtr address, int count)
        {
            if (handle == IntPtr.Zero) throw new ArgumentException("进程句柄不能为空", "handle");
            if (count <= 0) throw new ArgumentOutOfRangeException("count");
            var bytes = new byte[count];
            IntPtr read;
            if (!ReadProcessMemory(handle, address, bytes, (IntPtr)count, out read) || read.ToInt64() != count)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "读取游戏内存失败");
            return bytes;
        }

        static void Write(IntPtr handle, IntPtr address, byte[] bytes)
        {
            if (handle == IntPtr.Zero) throw new ArgumentException("进程句柄不能为空", "handle");
            if (bytes == null || bytes.Length == 0) throw new ArgumentException("写入字节不能为空", "bytes");
            uint old;
            if (!VirtualProtectEx(handle, address, (UIntPtr)bytes.Length, PageExecuteReadWrite, out old))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法修改内存保护");
            Exception writeError = null;
            try
            {
                IntPtr written;
                if (!WriteProcessMemory(handle, address, bytes, (IntPtr)bytes.Length, out written) || written.ToInt64() != bytes.Length)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "写入游戏内存失败");
                if (!FlushInstructionCache(handle, address, (UIntPtr)bytes.Length))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "刷新指令缓存失败");
                if (!Read(handle, address, bytes.Length).SequenceEqual(bytes))
                    throw new InvalidOperationException("写入后的内存校验失败");
            }
            catch (Exception error)
            {
                writeError = error;
            }
            uint ignored;
            if (!VirtualProtectEx(handle, address, (UIntPtr)bytes.Length, old, out ignored))
            {
                var message = "恢复内存保护失败，请退出并重启游戏。";
                if (writeError != null) message += "\n写入错误：" + writeError.Message;
                throw new InvalidOperationException(message, writeError);
            }
            if (writeError != null) ExceptionDispatchInfo.Capture(writeError).Throw();
        }

        public static int ApplySuspended(IntPtr handle, IntPtr module, IList<PatchSpec> patches, bool enable)
        {
            if (handle == IntPtr.Zero) throw new ArgumentException("进程句柄不能为空", "handle");
            if (module == IntPtr.Zero) throw new ArgumentException("模块基址不能为空", "module");
            if (patches == null || patches.Count == 0) throw new ArgumentException("补丁列表不能为空", "patches");
            using (var current = Process.GetCurrentProcess())
                if (GetProcessId(handle) == current.Id) throw new InvalidOperationException("不能暂停加载器自身来写入补丁。");
            for (int attempt = 0; ; attempt++)
            {
                try { return ApplySuspendedOnce(handle, module, patches, enable); }
                catch (PatchBusyException)
                {
                    if (attempt >= 39) throw;
                    System.Threading.Thread.Sleep(25);
                }
            }
        }

        static int ApplySuspendedOnce(IntPtr handle, IntPtr module, IList<PatchSpec> patches, bool enable)
        {
            int suspended = NtSuspendProcess(handle);
            if (suspended < 0) throw new InvalidOperationException("无法短暂暂停游戏进程，状态 0x" + suspended.ToString("X8"));
            Exception operationError = null;
            int result = 0;
            bool transactionStarted = false;
            try
            {
                var changing = patches.Where(p => !Read(handle, IntPtr.Add(module, p.Rva), p.OriginalBytes.Length)
                    .SequenceEqual(enable ? p.ReplacementBytes : p.OriginalBytes)).ToArray();
                ThreadSafety.EnsureQuiescent(handle, GetProcessId(handle), module, changing);
                transactionStarted = true;
                result = ApplyTransaction(handle, module, patches, enable);
            }
            catch (Exception error) { operationError = error; }
            int resumed = NtResumeProcess(handle);
            if (resumed < 0)
            {
                // A failed resume normally leaves the target suspended. Restore code first,
                // then retry the resume so a partial enable cannot be left in the process.
                var recoveryErrors = new List<string>();
                try { if (transactionStarted) ApplyTransaction(handle, module, patches, false); }
                catch (Exception recovery) { recoveryErrors.Add("补丁回滚失败：" + recovery.Message); }
                int retryResume = NtResumeProcess(handle);
                var message = "恢复游戏进程失败。请退出并重启游戏，状态 0x" + resumed.ToString("X8");
                if (retryResume < 0) message += "；重试状态 0x" + retryResume.ToString("X8");
                else message += "；已重试恢复进程";
                if (operationError != null) message += "\n补丁事务错误：" + operationError.Message;
                if (recoveryErrors.Count != 0) message += "\n" + String.Join("\n", recoveryErrors);
                throw new InvalidOperationException(message, operationError);
            }
            if (operationError is PatchBusyException) throw operationError;
            if (operationError != null)
                throw new InvalidOperationException("补丁事务失败：" + operationError.Message, operationError);
            return result;
        }

        public static void SelfTest()
        {
            var page = VirtualAlloc(IntPtr.Zero, (UIntPtr)4096, MemCommit | MemReserve, PageReadWrite);
            if (page == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "测试内存分配失败");
            try
            {
                var initial = Enumerable.Repeat((byte)0x90, 64).ToArray();
                Marshal.Copy(initial, 0, page, initial.Length);
                var patches = new[] {
                    new PatchSpec("fixture-a", 4, new byte[] {0x90}, new byte[] {0x91}, 0, Enumerable.Repeat((byte)0x90, 8).ToArray()),
                    new PatchSpec("fixture-b", 20, new byte[] {0x90}, new byte[] {0x92}, 16, Enumerable.Repeat((byte)0x90, 8).ToArray())
                };
                using (var process = Process.GetCurrentProcess())
                {
                    if (ApplyTransaction(process.Handle, page, patches, true) != 2) throw new Exception("enable failed");
                    if (Marshal.ReadByte(page, 4) != 0x91 || Marshal.ReadByte(page, 20) != 0x92) throw new Exception("readback failed");
                    if (ApplyTransaction(process.Handle, page, patches, true) != 0) throw new Exception("idempotency failed");
                    if (ApplyTransaction(process.Handle, page, patches, false) != 2) throw new Exception("restore failed");
                    if (!Read(process.Handle, page, 64).SequenceEqual(initial)) throw new Exception("restore mismatch");
                    Marshal.WriteByte(page, 4, 0x91);
                    if (ApplyTransaction(process.Handle, page, patches, false) != 1) throw new Exception("mixed restore failed");
                    if (!Read(process.Handle, page, 64).SequenceEqual(initial)) throw new Exception("mixed restore mismatch");

                    int writeCalls = 0;
                    Action<IntPtr, IntPtr, byte[]> failOnSecondWrite = delegate(IntPtr target, IntPtr address, byte[] bytes) {
                        if (writeCalls++ == 1) throw new InvalidOperationException("injected write failure");
                        Write(target, address, bytes);
                    };
                    bool rolledBack = false;
                    try { ApplyTransaction(process.Handle, page, patches, true, failOnSecondWrite); }
                    catch (InvalidOperationException) { rolledBack = true; }
                    if (!rolledBack || !Read(process.Handle, page, 64).SequenceEqual(initial))
                        throw new Exception("transaction rollback failed");

                    Marshal.WriteByte(page, 20, 0xCC);
                    bool rejected = false;
                    try { ApplyTransaction(process.Handle, page, patches, true); }
                    catch (InvalidOperationException) { rejected = true; }
                    if (!rejected || Marshal.ReadByte(page, 4) != 0x90) throw new Exception("conflict guard failed");
                }
            }
            finally { VirtualFree(page, UIntPtr.Zero, MemRelease); }
        }

        static int ApplyTransaction(IntPtr handle, IntPtr module, IList<PatchSpec> patches, bool enable)
        {
            return ApplyTransaction(handle, module, patches, enable, Write);
        }

        static int ApplyTransaction(IntPtr handle, IntPtr module, IList<PatchSpec> patches, bool enable, Action<IntPtr, IntPtr, byte[]> writer)
        {
            if (writer == null) throw new ArgumentNullException("writer");
            var current = new List<byte[]>();
            foreach (var patch in patches)
            {
                var bytes = Read(handle, IntPtr.Add(module, patch.Rva), patch.OriginalBytes.Length);
                if (!bytes.SequenceEqual(patch.OriginalBytes) && !bytes.SequenceEqual(patch.ReplacementBytes))
                    throw new InvalidOperationException("代码字节不匹配：" + patch.Name + "；可能有其他 Mod 修改了同一位置。");
                current.Add(bytes);
            }
            foreach (var patch in patches)
            {
                var context = Read(handle, IntPtr.Add(module, patch.GuardRva), patch.GuardOriginalBytes.Length);
                foreach (var other in patches)
                {
                    int offset = other.Rva - patch.GuardRva;
                    for (int j = 0; j < other.ReplacementBytes.Length; j++)
                        if (offset + j >= 0 && offset + j < context.Length && context[offset + j] == other.ReplacementBytes[j])
                            context[offset + j] = other.OriginalBytes[j];
                }
                if (!context.SequenceEqual(patch.GuardOriginalBytes))
                    throw new InvalidOperationException("补丁周围的代码不匹配：" + patch.Name + "；可能有冲突 Mod。");
            }
            var changed = new List<int>();
            try
            {
                for (int i = 0; i < patches.Count; i++)
                {
                    var desired = enable ? patches[i].ReplacementBytes : patches[i].OriginalBytes;
                    if (current[i].SequenceEqual(desired)) continue;
                    changed.Add(i);
                    writer(handle, IntPtr.Add(module, patches[i].Rva), desired);
                }
                return changed.Count;
            }
            catch (Exception error)
            {
                var rollbackErrors = new List<string>();
                foreach (var index in changed.AsEnumerable().Reverse())
                {
                    try { writer(handle, IntPtr.Add(module, patches[index].Rva), current[index]); }
                    catch (Exception rollback) { rollbackErrors.Add(rollback.Message); }
                }
                if (rollbackErrors.Count != 0)
                    throw new InvalidOperationException("补丁失败且未能完全还原，请重启游戏。\n" + error.Message + "\n" + String.Join("\n", rollbackErrors));
                throw new InvalidOperationException("补丁失败，本次修改已还原。\n" + error.Message, error);
            }
        }
    }

    public static class FrameworkDiagnostics
    {
        public static void SelfTest()
        {
            PatchSpecSelfTest();
            GameLaunchPathSelfTest();
            GameSessionValidationSelfTest();
            PluginIsolationSelfTest();
            RuntimeArchitectureSelfTest();
            Native.SelfTest();
            Console.WriteLine("PASS: immutable patch descriptor contract");
            Console.WriteLine("PASS: direct game launch path resolution");
            Console.WriteLine("PASS: session validation and process-handle lifecycle");
            Console.WriteLine("PASS: cross-plugin write/context isolation in either load order");
            Console.WriteLine("PASS: framework memory patch, idempotency, restore and conflict guard");
            Console.WriteLine("PASS: capability, event, Hook Broker request and property modifier contracts");
        }

        static void PatchSpecSelfTest()
        {
            var original = new byte[] { 0x90 };
            var replacement = new byte[] { 0x91 };
            var guard = new byte[] { 0x90, 0x90 };
            var patch = new PatchSpec("descriptor", 0, original, replacement, 0, guard);
            original[0] = 0xCC;
            replacement[0] = 0xCC;
            guard[0] = 0xCC;
            if (patch.Original[0] != 0x90 || patch.Replacement[0] != 0x91 || patch.GuardOriginal[0] != 0x90)
                throw new Exception("constructor did not copy descriptor bytes");
            var exposed = patch.Original;
            exposed[0] = 0xCC;
            if (patch.Original[0] != 0x90) throw new Exception("descriptor exposed mutable storage");
            bool rejected = false;
            try { new PatchSpec("no-op", 0, new byte[] { 0x90 }, new byte[] { 0x90 }, 0, new byte[] { 0x90 }); }
            catch (ArgumentException) { rejected = true; }
            if (!rejected) throw new Exception("no-op descriptor was accepted");
            rejected = false;
            try { new PatchSpec("bad-guard", 4, new byte[] { 0x90, 0x90 }, new byte[] { 0x91, 0x91 }, 5, new byte[] { 0x90 }); }
            catch (ArgumentException) { rejected = true; }
            if (!rejected) throw new Exception("under-sized guard was accepted");
            if (!FrameworkInfo.IsSha256(FrameworkInfo.TargetSha256) || !FrameworkInfo.IsSha256(FrameworkInfo.TargetSha256.ToLowerInvariant()) || FrameworkInfo.IsSha256("invalid"))
                throw new Exception("SHA256 format validation failed");
            if (!FrameworkInfo.IsValidPluginId("unlimited-followers.1") || FrameworkInfo.IsValidPluginId("-bad") || FrameworkInfo.IsValidPluginId("bad id"))
                throw new Exception("plugin ID format validation failed");
        }

        static void GameLaunchPathSelfTest()
        {
            var directory = Path.Combine(Path.GetTempPath(), "sod2se-path-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var expected = Path.Combine(directory, FrameworkInfo.GameExecutableName);
            var nestedDirectory = Path.Combine(directory, FrameworkInfo.GameDataFolderName, "Binaries", "Win64");
            var nestedExecutable = Path.Combine(nestedDirectory, FrameworkInfo.GameExecutableName);
            var rootLauncher = Path.Combine(directory, FrameworkInfo.GameLauncherName);
            try
            {
                File.WriteAllText(expected, "fixture");
                var resolved = FrameworkInfo.ResolveGameExecutable(expected, directory);
                if (!String.Equals(Path.GetFullPath(expected), resolved, StringComparison.OrdinalIgnoreCase))
                    throw new Exception("explicit game path resolution failed");
                if (!String.Equals(directory, FrameworkInfo.ResolveGameWorkingDirectory(expected), StringComparison.OrdinalIgnoreCase))
                    throw new Exception("game working directory resolution failed");
                if (!String.Equals(expected, FrameworkInfo.ResolveGameLauncher(expected), StringComparison.OrdinalIgnoreCase))
                    throw new Exception("game launcher fallback resolution failed");
                File.Delete(expected);
                Directory.CreateDirectory(nestedDirectory);
                File.WriteAllText(nestedExecutable, "nested fixture");
                File.WriteAllText(rootLauncher, "launcher fixture");
                var resolvedNested = FrameworkInfo.ResolveGameExecutable(null, directory);
                if (!String.Equals(nestedExecutable, resolvedNested, StringComparison.OrdinalIgnoreCase))
                    throw new Exception("game-root nested executable resolution failed");
                if (!String.Equals(directory, FrameworkInfo.ResolveGameWorkingDirectory(resolvedNested), StringComparison.OrdinalIgnoreCase))
                    throw new Exception("game-root working directory resolution failed");
                if (!String.Equals(rootLauncher, FrameworkInfo.ResolveGameLauncher(resolvedNested), StringComparison.OrdinalIgnoreCase))
                    throw new Exception("game-root launcher resolution failed");
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        static void GameSessionValidationSelfTest()
        {
            using (var process = Process.GetCurrentProcess())
            using (var session = new GameSession(process))
            {
                var overlapping = new List<PatchSpec> {
                    new PatchSpec("overlap-a", 0, new byte[] { 0x90, 0x90 }, new byte[] { 0x91, 0x91 }, 0, new byte[] { 0x90, 0x90, 0x90 }),
                    new PatchSpec("overlap-b", 1, new byte[] { 0x90, 0x90 }, new byte[] { 0x92, 0x92 }, 0, new byte[] { 0x90, 0x90, 0x90 })
                };
                bool rejected = false;
                try { session.Apply("overlap-test", FrameworkInfo.TargetSha256, overlapping); }
                catch (InvalidOperationException) { rejected = true; }
                if (!rejected) throw new Exception("overlapping patch set was accepted");

                int crossPageRva = GameSession.MemoryPageSize - (int)(session.ModuleBase.ToInt64() & (GameSession.MemoryPageSize - 1)) - 1;
                var crossPage = new List<PatchSpec> {
                    new PatchSpec("cross-page", crossPageRva, new byte[] { 0x90, 0x90 }, new byte[] { 0x91, 0x91 }, crossPageRva, new byte[] { 0x90, 0x90 })
                };
                rejected = false;
                try { session.Apply("cross-page-test", FrameworkInfo.HashFile(session.ExePath), crossPage); }
                catch (InvalidOperationException) { rejected = true; }
                if (!rejected) throw new Exception("cross-page patch was accepted");

                session.Dispose();
                session.Dispose();
                bool disposedRejected = false;
                try { session.Apply("after-dispose", FrameworkInfo.TargetSha256, overlapping); }
                catch (ObjectDisposedException) { disposedRejected = true; }
                if (!disposedRejected) throw new Exception("disposed session remained usable");
            }
        }

        static void PluginIsolationSelfTest()
        {
            var first = new PatchSpec("first", 4, new byte[] { 0x90 }, new byte[] { 0x91 }, 0, Enumerable.Repeat((byte)0x90, 16).ToArray());
            var conflicts = new[] {
                new PatchSpec("same-write", 4, new byte[] { 0x90 }, new byte[] { 0x92 }, 4, new byte[] { 0x90 }),
                new PatchSpec("write-in-existing-guard", 12, new byte[] { 0x90 }, new byte[] { 0x92 }, 12, new byte[] { 0x90 }),
                new PatchSpec("existing-write-in-guard", 20, new byte[] { 0x90 }, new byte[] { 0x92 }, 4, Enumerable.Repeat((byte)0x90, 17).ToArray())
            };
            foreach (var conflict in conflicts)
            {
                for (int order = 0; order < 2; order++)
                {
                    bool rejected = false;
                    try
                    {
                        GameSession.EnsurePatchSetIsolation("fixture", new[] { order == 0 ? first : conflict }, new[] { order == 0 ? conflict : first });
                    }
                    catch (InvalidOperationException) { rejected = true; }
                    if (!rejected) throw new Exception("cross-plugin conflict accepted: " + conflict.Name + " / " + order);
                }
            }
            var compatible = new[] {
                new PatchSpec("overlapping-guards-only", 20, new byte[] { 0x90 }, new byte[] { 0x92 }, 8, Enumerable.Repeat((byte)0x90, 13).ToArray()),
                new PatchSpec("touching-boundary", 16, new byte[] { 0x90 }, new byte[] { 0x92 }, 16, new byte[] { 0x90 })
            };
            foreach (var other in compatible)
            {
                GameSession.EnsurePatchSetIsolation("fixture", new[] { first }, new[] { other });
                GameSession.EnsurePatchSetIsolation("fixture", new[] { other }, new[] { first });
            }
        }

        static void RuntimeArchitectureSelfTest()
        {
            var capabilities = new CapabilitySet();
            capabilities.Declare("fixture.capability", true, "fixture");
            if (!capabilities.IsAvailable("fixture.capability") || capabilities.IsAvailable("missing"))
                throw new Exception("capability detection contract failed");

            var events = new GameEventBus();
            int eventCount = 0;
            var subscription = events.Subscribe("fixture.event", delegate(GameEvent value) { eventCount += value.Code; });
            events.Publish(new GameEvent("fixture.event", "self-test", 0, 0, new GameObjectToken(7, 1, "fixture"), 2, 0, 0, ""));
            subscription.Dispose();
            events.Publish(new GameEvent("fixture.event", "self-test", 0, 0, new GameObjectToken(), 4, 0, 0, ""));
            if (eventCount != 2) throw new Exception("event subscription lifecycle failed");

            var properties = new PropertyModifierStack();
            var multiply = properties.Register("fixture.rate", "fixture.multiply", PropertyModifierMode.Multiply, 10, 1.5f, 1, true);
            var add = properties.Register("fixture.rate", "fixture.add", PropertyModifierMode.Add, 20, 0.25f, 1, true);
            if (Math.Abs(properties.Evaluate("fixture.rate", 1.0f) - 1.75f) > 0.001f)
                throw new Exception("property modifier evaluation failed");
            multiply.Update(2.0f, true);
            add.Update(0.5f, false);
            if (Math.Abs(properties.Evaluate("fixture.rate", 1.0f) - 2.0f) > 0.001f)
                throw new Exception("property modifier update failed");
            multiply.Dispose(); add.Dispose();
            var global = properties.Register("fixture.scoped", "fixture.global", PropertyModifierMode.Multiply, 10, 2.0f, 1, true);
            var alice = properties.Register("fixture.scoped", "fixture.alice", "survivor-A", PropertyModifierMode.Multiply, 10, 1.5f, 1, true);
            var scoped = properties.Snapshot("survivor-A", new Dictionary<string, float> { { "fixture.scoped", 1.0f } });
            var other = properties.Snapshot("survivor-B", new Dictionary<string, float> { { "fixture.scoped", 1.0f } });
            if (Math.Abs(scoped["fixture.scoped"] - 3.0f) > 0.001f || Math.Abs(other["fixture.scoped"] - 2.0f) > 0.001f)
                throw new Exception("scoped property modifier evaluation failed");
            global.Dispose(); alice.Dispose();

            var patch = new PatchSpec("broker-fixture", 0, new byte[] { 0x90 }, new byte[] { 0x91 }, 0, new byte[] { 0x90 });
            var request = HookRequest.BytePatch("fixture", "fixture.capability", FrameworkInfo.TargetSha256, new[] { patch });
            if (request.Kind != HookKind.BytePatch || request.Patches.Count != 1)
                throw new Exception("Hook Broker request contract failed");
            bool rejected = false;
            try { HookRequest.NativeModule("fixture", "fixture.capability", "", "entry", "argument"); }
            catch (ArgumentException) { rejected = true; }
            if (!rejected) throw new Exception("invalid native Hook request was accepted");
            rejected = false;
            try { HookRequest.BytePatch("fixture", "fixture.capability", "invalid", new[] { patch }); }
            catch (ArgumentException) { rejected = true; }
            if (!rejected) throw new Exception("invalid byte-patch SHA256 was accepted");
        }
    }
}
