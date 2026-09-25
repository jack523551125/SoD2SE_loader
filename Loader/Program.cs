using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using SoD2SE;
using SoD2SE.GameApi;

namespace SoD2SE.Loader
{
    static class Program
    {
        static readonly ManualResetEvent StopRequested = new ManualResetEvent(false);
        static readonly object LogSync = new object();
        static readonly string LaunchLogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SoD2SE-launch.log");
        static readonly string FallbackLogPath = Path.Combine(Path.GetTempPath(), "SoD2SE-launch.log");
        static bool FileLoggingEnabled = true;
        static bool ConsoleRequested;
        static string LastLogPath;

        static int Main(string[] args)
        {
            try
            {
                ConsoleRequested = HasArg(args, "--console");
                if (ConsoleRequested) NativeConsole.EnsureConsole();
                if (HasArg(args, "--self-test")) FileLoggingEnabled = false;
                WriteLog("启动参数：" + (args == null ? "" : String.Join(" ", args)));
                if (ShouldStartBackgroundWorker(args)) return StartBackgroundWorker(args);
                if (HasArg(args, "--self-test")) return RunSelfTest();
                if (HasArg(args, "--diagnose-launch")) return RunLaunchDiagnostics(args);
                if (HasArg(args, "--help") || HasArg(args, "/?"))
                {
                    PrintUsage();
                    return 0;
                }
                return RunInteractive(args);
            }
            catch (OperationCanceledException error)
            {
                WriteStatus(error.Message);
                return 0;
            }
            catch (Exception error)
            {
                var message = "SoD2SE 未完成操作：" + error.Message;
                WriteError(message);
                if (!ConsoleRequested && !NativeConsole.HasConsole)
                    NativeConsole.ShowError(message + "\n\n日志：" + GetLogPathForUser());
                return 1;
            }
        }

        static void WriteStatus(string message)
        {
            WriteLog(message);
            if (NativeConsole.HasConsole)
            {
                try { Console.WriteLine(message); }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
            }
        }

        static void WriteError(string message)
        {
            WriteLog("ERROR: " + message);
            if (NativeConsole.HasConsole)
            {
                try { Console.Error.WriteLine(message); }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
            }
        }

        static void WriteLog(string message)
        {
            if (!FileLoggingEnabled) return;
            var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz") + " " + (message ?? "") + Environment.NewLine;
            lock (LogSync)
            {
                try
                {
                    File.AppendAllText(LaunchLogPath, line, Encoding.UTF8);
                    LastLogPath = LaunchLogPath;
                    return;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            try
            {
                File.AppendAllText(FallbackLogPath, line, Encoding.UTF8);
                LastLogPath = FallbackLogPath;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        static string GetLogPathForUser()
        {
            return String.IsNullOrWhiteSpace(LastLogPath) ? LaunchLogPath + "（不可写时使用 " + FallbackLogPath + "）" : LastLogPath;
        }

        static bool ShouldStartBackgroundWorker(string[] args)
        {
            // MO2 must track the process that owns the virtual filesystem session.
            // Staying in this GUI process does not create a console window.
            if (HasArg(args, "--mo2") || VirtualFileSystem.IsLoaded) return false;
            if (HasArg(args, "--worker") || HasArg(args, "--console") || HasArg(args, "--self-test") || HasArg(args, "--diagnose-launch")) return false;
            return !HasArg(args, "--help") && !HasArg(args, "/?");
        }

        static int StartBackgroundWorker(string[] args)
        {
            var workerArguments = new StringBuilder("--worker");
            if (args != null)
            {
                foreach (var argument in args)
                {
                    if (String.IsNullOrWhiteSpace(argument) || String.Equals(argument, "--worker", StringComparison.OrdinalIgnoreCase)) continue;
                    workerArguments.Append(' ').Append(QuoteProcessArgument(argument));
                }
            }
            var executable = Assembly.GetExecutingAssembly().Location;
            if (String.IsNullOrWhiteSpace(executable)) executable = Process.GetCurrentProcess().MainModule.FileName;
            try
            {
                var worker = Process.Start(new ProcessStartInfo {
                    FileName = executable,
                    Arguments = workerArguments.ToString(),
                    WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
                if (worker == null) throw new InvalidOperationException("无法创建后台加载器进程。");
                var workerId = worker.Id;
                worker.Dispose();
                WriteStatus("后台加载器已启动，PID：" + workerId);
                return 0;
            }
            catch (Exception error)
            {
                throw new InvalidOperationException("无法启动后台加载器。\n" + error.Message, error);
            }
        }

        static string QuoteProcessArgument(string value)
        {
            if (value == null) return "\"\"";
            if (value.Length > 0 && value.All(character => !Char.IsWhiteSpace(character) && character != '\"')) return value;
            var result = new StringBuilder("\"");
            var slashes = 0;
            foreach (var character in value)
            {
                if (character == '\\') { slashes++; continue; }
                if (character == '\"')
                {
                    result.Append('\\', slashes * 2 + 1).Append('\"');
                    slashes = 0;
                    continue;
                }
                result.Append('\\', slashes).Append(character);
                slashes = 0;
            }
            result.Append('\\', slashes * 2).Append('\"');
            return result.ToString();
        }

        static int RunSelfTest()
        {
            FrameworkDiagnostics.SelfTest();
            foreach (var plugin in DiscoverPlugins())
                WriteStatus("PASS: plugin " + plugin.Id + " - " + plugin.Name);
            return 0;
        }

        static int RunLaunchDiagnostics(string[] args)
        {
            string explicitGameExe = GetOptionValue(args, "--game-exe");
            string gameExe = FrameworkInfo.ResolveGameExecutable(explicitGameExe, AppDomain.CurrentDomain.BaseDirectory);
            var lines = new List<string> {
                "框架版本：" + FrameworkInfo.Version,
                "加载器目录：" + AppDomain.CurrentDomain.BaseDirectory,
                "当前目录：" + Environment.CurrentDirectory,
                "64 位进程：" + Environment.Is64BitProcess,
                "管理员权限：" + new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator),
                "游戏 EXE：" + (String.IsNullOrWhiteSpace(gameExe) ? "未找到" : gameExe),
                "游戏启动程序：" + (String.IsNullOrWhiteSpace(gameExe) ? "未找到" : FrameworkInfo.ResolveGameLauncher(gameExe)),
                "直接启动参数：" + GetGameLaunchArguments(args),
                "MO2 USVFS：" + VirtualFileSystem.IsLoaded,
                "直接主程序模式：" + (HasArg(args, "--direct-main") || HasArg(args, "--mo2")),
                "启动日志：" + GetLogPathForUser()
            };
            if (!String.IsNullOrWhiteSpace(gameExe))
            {
                lines.Insert(6, "游戏工作目录：" + FrameworkInfo.ResolveGameWorkingDirectory(gameExe));
            }
            foreach (var line in lines) WriteStatus(line);
            if (!ConsoleRequested && !NativeConsole.HasConsole)
                NativeConsole.ShowInfo(String.Join("\n", lines), "SoD2SE 启动诊断");
            return String.IsNullOrWhiteSpace(gameExe) ? 1 : 0;
        }

        static int RunInteractive(string[] args)
        {
            using (var singleInstance = AcquireSingleInstance())
            {
                StopRequested.Reset();
                ConsoleCancelEventHandler cancelHandler = delegate(object sender, ConsoleCancelEventArgs e) {
                    e.Cancel = true;
                    StopRequested.Set();
                };
                Console.CancelKeyPress += cancelHandler;
                try
                {
                    bool attachOnly = HasArg(args, "--attach");
                    string explicitGameExe = GetOptionValue(args, "--game-exe");
                    bool mo2 = HasArg(args, "--mo2") || VirtualFileSystem.IsLoaded;
                    if (mo2 && !VirtualFileSystem.IsLoaded)
                        throw new InvalidOperationException("--mo2 必须由 MO2 的虚拟环境启动。请从 MO2 运行 SoD2SE.Loader。");
                    WriteStatus("MO2 USVFS 环境：" + VirtualFileSystem.IsLoaded);
                    if (HasArg(args, "--steam") || HasArg(args, "--steam-uri"))
                        throw new InvalidOperationException("此版本只支持直接启动游戏 EXE；请去掉 --steam 或 --steam-uri。");
                    var discoveredPlugins = DiscoverPlugins();
                    var mcm = McmRegistry.Initialize(AppDomain.CurrentDomain.BaseDirectory);
                    UiRegistry.Initialize(AppDomain.CurrentDomain.BaseDirectory);
                    foreach (var plugin in discoveredPlugins)
                    {
                        var configurable = plugin as IMcmConfigurable;
                        if (configurable != null) configurable.RegisterMcm(mcm);
                        else if (plugin.Id != "mcm") mcm.RegisterPlugin(plugin.Id, plugin.Name, plugin.Description);
                    }
                    var plugins = discoveredPlugins.Where(plugin => mcm.IsEnabled(plugin.Id)).ToList();
                    if (plugins.Count == 0) WriteStatus("当前未启用 SoD2 插件，将正常启动游戏而不应用内存补丁。");
                    else if (plugins.Count != discoveredPlugins.Count)
                        WriteStatus("MCM 已停用 " + (discoveredPlugins.Count - plugins.Count) + " 个插件。");
                    using (var game = StartOrAttachGame(attachOnly, explicitGameExe,
                        HasArg(args, "--direct-main") || mo2, GetGameLaunchArguments(args), mo2))
                        RunPlugins(game, plugins);
                    WriteStatus("框架已退出。");
                    return 0;
                }
                finally { Console.CancelKeyPress -= cancelHandler; }
            }
        }

        static Process StartOrAttachGame(bool attachOnly, string explicitGameExe, bool directMain, string gameArguments, bool requireVfs)
        {
            var game = FrameworkInfo.FindRunningGame();
            if (game != null)
            {
                if (requireVfs)
                {
                    game.Dispose();
                    throw new InvalidOperationException("MO2 模式不能接管已经运行的游戏。请退出游戏，再从 MO2 启动，以应用当前 Mod 列表。");
                }
                return game;
            }
            if (attachOnly) throw new InvalidOperationException("未找到正在运行的游戏。");
            var gameExe = FrameworkInfo.ResolveGameExecutable(explicitGameExe, AppDomain.CurrentDomain.BaseDirectory);
            if (String.IsNullOrWhiteSpace(gameExe))
            {
                if (!String.IsNullOrWhiteSpace(explicitGameExe))
                    throw new FileNotFoundException("找不到 --game-exe 指定的游戏 EXE 或游戏根目录。", explicitGameExe);
                throw new FileNotFoundException("未找到游戏主程序。请把加载器放入游戏根目录，或使用 --game-exe 指定 EXE/游戏根目录。", FrameworkInfo.GameExecutableName);
            }
            return StartDirectGame(gameExe, directMain, gameArguments, requireVfs);
        }

        static Process StartDirectGame(string gameExe, bool directMain, string arguments, bool requireVfs)
        {
            var workingDirectory = FrameworkInfo.ResolveGameWorkingDirectory(gameExe);
            if (directMain)
            {
                WriteStatus("直接启动游戏主程序：" + gameExe);
                WriteStatus("游戏参数：" + arguments);
                int pid = DirectProcessLauncher.Start(gameExe, arguments, workingDirectory);
                WriteStatus("直接主程序 PID：" + pid);
                return WaitForDirectGame(pid, gameExe, requireVfs);
            }
            var launcher = FrameworkInfo.ResolveGameLauncher(gameExe);
            Exception firstError = null;

            WriteStatus("直接启动游戏：" + launcher);
            try
            {
                var launcherPid = DirectProcessLauncher.Start(launcher, arguments, workingDirectory);
                WriteStatus("已创建游戏启动进程，PID：" + launcherPid);
                // Both the root bootstrap and the shipping executable may
                // hand off to another process before the final game process
                // appears.  Treat the launched PID as a bootstrap handle and
                // keep watching for the real game process.
                return WaitForGame(TimeSpan.FromSeconds(45), launcherPid, true);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                firstError = error;
                WriteStatus("游戏引导程序没有创建游戏进程，尝试直接启动主程序：" + error.Message);
            }

            if (!String.Equals(launcher, gameExe, StringComparison.OrdinalIgnoreCase) && File.Exists(gameExe))
            {
                try
                {
                    var gamePid = DirectProcessLauncher.Start(gameExe, arguments, workingDirectory);
                    WriteStatus("已直接创建游戏主程序，PID：" + gamePid);
                    return WaitForGame(TimeSpan.FromSeconds(45), gamePid, true);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error)
                {
                    throw new InvalidOperationException("直接启动游戏失败。启动引导程序错误：" + firstError.Message + "；主程序错误：" + error.Message, error);
                }
            }
            throw new InvalidOperationException("直接启动游戏失败：" + firstError.Message, firstError);
        }

        static Process WaitForDirectGame(int pid, string expectedPath, bool requireVfs)
        {
            Process game = null;
            try
            {
                game = Process.GetProcessById(pid);
                // Keep a process handle so exit status remains available after termination.
                var processHandle = game.Handle;
                if (game.WaitForExit(2000))
                    throw new InvalidOperationException("直接启动的游戏主进程已退出，退出码 0x" + game.ExitCode.ToString("X8") +
                        "。请检查游戏自身启动条件：Steam 版需要客户端已就绪，并由 MO 插件传入 -steamlaunch。为保留 Mod 环境，本次不会接管其他进程。");
                if (StopRequested.WaitOne(0)) throw new OperationCanceledException("用户取消了等待游戏启动。");
                if (!String.Equals(Path.GetFullPath(game.MainModule.FileName), Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("直接启动进程的主程序路径不匹配。");
                bool hasVfs = VirtualFileSystem.IsLoadedIn(game);
                if (requireVfs && !hasVfs)
                    throw new InvalidOperationException("游戏进程未继承 MO2 USVFS，已拒绝把仅有内存插件生效误报为启动成功。请检查 MO 插件版本与权限。");
                WriteStatus("直接主程序已确认，PID：" + game.Id + "；USVFS：" + hasVfs);
                var result = game;
                game = null;
                return result;
            }
            finally { if (game != null) game.Dispose(); }
        }

        static void RunPlugins(Process game, IList<ISoD2Plugin> plugins)
        {
            WriteStatus("已找到游戏进程，正在加载固定版本框架 " + FrameworkInfo.Version + "...");
            var gameApi = StateOfDecay2GameApi.Create();
            using (var session = new GameSession(game, gameApi))
            {
                WriteStatus("游戏：" + session.ExePath);
                WriteStatus("模块基址：0x" + session.ModuleBase.ToInt64().ToString("X"));
                WriteStatus("Game API：" + gameApi.Id + " / Build " + gameApi.Target.BuildId);
                var loaded = new List<ISoD2Plugin>();
                try
                {
                    foreach (var plugin in plugins)
                    {
                        WriteStatus("加载插件：" + plugin.Id + " - " + plugin.Description);
                        try
                        {
                            plugin.Initialize(session);
                            loaded.Add(plugin);
                            var mcm = McmRegistry.Current;
                            var health = plugin as IPluginRuntimeStatus;
                            // "Loaded" describes successful plugin initialization.
                            // A feature may intentionally stay inactive while a
                            // required game capability is unverified; the plugin
                            // page must not report that as a DLL load failure.
                            if (mcm != null) mcm.MarkLoaded(plugin.Id, true);
                            WriteStatus("插件已初始化：" + plugin.Id + (health == null ? "" : "；状态：" + health.Status));
                        }
                        catch
                        {
                            TryShutdown(plugin, "失败插件清理异常");
                            throw;
                        }
                    }
                    WriteStatus("框架已运行。MCM 界面由独立插件提供；游戏退出后内存修改消失。");
                    while (!game.HasExited && !StopRequested.WaitOne(500)) { }
                }
                finally
                {
                    for (int i = loaded.Count - 1; i >= 0; i--)
                    {
                        var mcm = McmRegistry.Current;
                        if (mcm != null) mcm.MarkLoaded(loaded[i].Id, false);
                        TryShutdown(loaded[i], "插件还原失败");
                    }
                }
            }
        }

        static void TryShutdown(ISoD2Plugin plugin, string prefix)
        {
            try { plugin.Shutdown(); }
            catch (Exception error) { WriteError(prefix + "：" + error.Message); }
        }

        static Mutex AcquireSingleInstance()
        {
            bool created;
            var mutex = new Mutex(true, "Local\\SoD2SE.Loader." + FrameworkInfo.GameProcessName, out created);
            if (created) return mutex;
            mutex.Dispose();
            throw new InvalidOperationException("已经有一个 SoD2SE 加载器正在运行；请关闭它后再启动。");
        }

        static Process WaitForGame(TimeSpan timeout, int launchedProcessId)
        {
            return WaitForGame(timeout, launchedProcessId, false);
        }

        static Process WaitForGame(TimeSpan timeout, int launchedProcessId, bool launcherMayExit)
        {
            if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException("timeout");
            WriteStatus("等待游戏进程（最多 " + (int)Math.Ceiling(timeout.TotalSeconds) + " 秒）...");
            var stopwatch = Stopwatch.StartNew();
            int candidateId = 0;
            var candidateSince = DateTime.MinValue;
            bool launcherExitLogged = false;
            while (stopwatch.Elapsed < timeout)
            {
                if (StopRequested.WaitOne(0)) throw new OperationCanceledException("用户取消了等待游戏启动。");
                var game = FrameworkInfo.FindRunningGame();
                if (game != null)
                {
                    try
                    {
                        if (game.Id != candidateId)
                        {
                            candidateId = game.Id;
                            candidateSince = DateTime.UtcNow;
                        }
                        var ignored = game.MainModule;
                        if ((DateTime.UtcNow - candidateSince).TotalMilliseconds < 1500)
                        {
                            game.Dispose();
                            if (StopRequested.WaitOne(100)) throw new OperationCanceledException("用户取消了等待游戏启动。");
                            continue;
                        }
                        return game;
                    }
                    catch (InvalidOperationException) { game.Dispose(); }
                    catch (NotSupportedException) { game.Dispose(); }
                    catch (Win32Exception) { game.Dispose(); }
                }
                if (launchedProcessId != 0 && !launcherMayExit && stopwatch.Elapsed > TimeSpan.FromSeconds(2) && !IsProcessAlive(launchedProcessId))
                {
                    throw new InvalidOperationException("游戏主程序已退出，无法附加。");
                }
                if (launchedProcessId != 0 && launcherMayExit && !launcherExitLogged && !IsProcessAlive(launchedProcessId))
                {
                    WriteStatus("启动进程已退出，继续等待游戏接管（最多 " + (int)Math.Ceiling((timeout - stopwatch.Elapsed).TotalSeconds) + " 秒）...");
                    launcherExitLogged = true;
                }
                if (StopRequested.WaitOne(100)) throw new OperationCanceledException("用户取消了等待游戏启动。");
            }
            var suffix = launchedProcessId != 0 && !IsProcessAlive(launchedProcessId) ? "启动进程已退出。" : "";
            throw new TimeoutException("等待游戏进程超时。" + suffix + "请检查游戏根目录、主程序文件和启动日志：" + LaunchLogPath);
        }

        static bool IsProcessAlive(int processId)
        {
            try
            {
                using (var process = Process.GetProcessById(processId)) return !process.HasExited;
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
            catch (Win32Exception) { return false; }
        }

        static bool HasArg(string[] args, string expected)
        {
            return args != null && args.Any(arg => String.Equals(arg, expected, StringComparison.OrdinalIgnoreCase));
        }

        static string GetOptionValue(string[] args, string option)
        {
            if (args == null) return null;
            for (int i = 0; i < args.Length; i++)
            {
                var argument = args[i] ?? "";
                if (String.Equals(argument, option, StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= args.Length || String.IsNullOrWhiteSpace(args[i + 1]))
                        throw new ArgumentException(option + " 需要一个参数值。");
                    return args[i + 1];
                }
                var prefix = option + "=";
                if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    var value = argument.Substring(prefix.Length);
                    if (String.IsNullOrWhiteSpace(value)) throw new ArgumentException(option + " 需要一个参数值。");
                    return value;
                }
            }
            return null;
        }

        static string GetGameLaunchArguments(string[] args)
        {
            if (args != null)
                for (int i = 0; i < args.Length; i++)
                {
                    string argument = args[i] ?? "";
                    if (String.Equals(argument, "--game-args", StringComparison.OrdinalIgnoreCase))
                    {
                        if (i + 1 >= args.Length) throw new ArgumentException("--game-args 需要一个参数值。");
                        return args[i + 1] ?? "";
                    }
                    if (argument.StartsWith("--game-args=", StringComparison.OrdinalIgnoreCase))
                        return argument.Substring("--game-args=".Length);
                }
            var configured = Environment.GetEnvironmentVariable("SOD2_GAME_ARGS");
            return configured == null ? FrameworkInfo.GameLaunchArguments : configured;
        }

        static List<ISoD2Plugin> DiscoverPlugins()
        {
            var result = new List<ISoD2Plugin>();
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Plugins");
            if (!Directory.Exists(directory)) return result;
            foreach (var path in Directory.GetFiles(directory, "*.dll").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                Assembly assembly;
                try { assembly = Assembly.LoadFrom(path); }
                catch (Exception error) { throw new InvalidOperationException("加载插件程序集失败：" + path + "\n" + error.Message, error); }
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException error)
                {
                    var details = String.Join("；", error.LoaderExceptions.Where(item => item != null).Select(item => item.Message));
                    throw new InvalidOperationException("读取插件类型失败：" + path + (String.IsNullOrEmpty(details) ? "" : "\n" + details), error);
                }
                foreach (var type in types)
                {
                    if (!type.IsClass || type.IsAbstract || !typeof(ISoD2Plugin).IsAssignableFrom(type)) continue;
                    var constructor = type.GetConstructor(Type.EmptyTypes);
                    if (constructor == null) throw new InvalidOperationException("插件类型缺少公共无参数构造函数：" + type.FullName);
                    ISoD2Plugin plugin;
                    try { plugin = (ISoD2Plugin)constructor.Invoke(null); }
                    catch (Exception error) { throw new InvalidOperationException("创建插件实例失败：" + type.FullName + "\n" + error.Message, error); }
                    if (plugin.ApiVersion != FrameworkInfo.PluginApiVersion)
                        throw new InvalidOperationException("插件 API 版本不匹配：" + type.FullName + "（插件 " + plugin.ApiVersion + "，框架 " + FrameworkInfo.PluginApiVersion + "）");
                    if (!FrameworkInfo.IsValidPluginId(plugin.Id)) throw new InvalidOperationException("插件 ID 格式无效：" + type.FullName);
                    if (String.IsNullOrWhiteSpace(plugin.Name)) throw new InvalidOperationException("插件名称不能为空：" + type.FullName);
                    if (plugin.Description == null) throw new InvalidOperationException("插件描述不能为 null：" + type.FullName);
                    if (!ids.Add(plugin.Id)) throw new InvalidOperationException("发现重复的插件 ID：" + plugin.Id);
                    result.Add(plugin);
                }
            }
            return result;
        }

        static void PrintUsage()
        {
            WriteStatus("SoD2SE 固定版本加载器 " + FrameworkInfo.Version);
            WriteStatus("普通启动会先启动隐藏后台 worker，前台启动器立即返回");
            WriteStatus("默认：读取加载器所在游戏根目录并直接创建游戏 EXE，不调用 Steam");
            WriteStatus("--game-exe <路径>：指定游戏 EXE 或游戏根目录；也可设置 SOD2_GAME_EXE");
            WriteStatus("--game-args=<参数>：原始游戏启动参数；优先于 SOD2_GAME_ARGS，支持空值");
            WriteStatus("--direct-main：直接启动 Win64 主程序，不使用游戏根目录引导程序");
            WriteStatus("--mo2：由 MO2 启动、直接创建主程序并检查 USVFS；不允许接管外部游戏进程");
            WriteStatus("--attach：只附加已经运行的游戏，不启动游戏");
            WriteStatus("--console：为诊断模式创建控制台窗口");
            WriteStatus("--diagnose-launch：显示并记录启动路径和环境，不启动游戏");
            WriteStatus("--self-test：执行框架和插件发现自测，不接触游戏");
        }

        static class NativeConsole
        {
            const int AttachParentProcess = -1;
            const uint MessageBoxError = 0x00000010;
            const uint MessageBoxInformation = 0x00000040;

            [DllImport("kernel32.dll", SetLastError = true)]
            static extern bool AllocConsole();

            [DllImport("kernel32.dll")]
            static extern IntPtr GetConsoleWindow();

            [DllImport("kernel32.dll", SetLastError = true)]
            static extern bool AttachConsole(int processId);

            [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

            public static bool HasConsole { get { return GetConsoleWindow() != IntPtr.Zero; } }

            public static void EnsureConsole()
            {
                if (HasConsole) return;
                if (!AttachConsole(AttachParentProcess)) AllocConsole();
            }

            public static void ShowError(string message)
            {
                try { MessageBox(IntPtr.Zero, message, "SoD2SE 启动失败", MessageBoxError); }
                catch (DllNotFoundException) { }
                catch (EntryPointNotFoundException) { }
            }

            public static void ShowInfo(string message, string caption)
            {
                try { MessageBox(IntPtr.Zero, message, caption, MessageBoxInformation); }
                catch (DllNotFoundException) { }
                catch (EntryPointNotFoundException) { }
            }
        }

        static class VirtualFileSystem
        {
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
            public static bool IsLoaded { get { return GetModuleHandle("usvfs_x64.dll") != IntPtr.Zero; } }
            public static bool IsLoadedIn(Process process)
            {
                foreach (ProcessModule module in process.Modules)
                    if (String.Equals(module.ModuleName, "usvfs_x64.dll", StringComparison.OrdinalIgnoreCase)) return true;
                return false;
            }
        }

        static class DirectProcessLauncher
        {
            const uint CreateUnicodeEnvironment = 0x00000400;

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            struct StartupInfo
            {
                public int cb;
                public string reserved;
                public string desktop;
                public string title;
                public int x;
                public int y;
                public int xSize;
                public int ySize;
                public int xCountChars;
                public int yCountChars;
                public int fillAttribute;
                public int flags;
                public short showWindow;
                public short reserved2;
                public IntPtr reserved2Ptr;
                public IntPtr standardInput;
                public IntPtr standardOutput;
                public IntPtr standardError;
            }

            [StructLayout(LayoutKind.Sequential)]
            struct ProcessInformation
            {
                public IntPtr process;
                public IntPtr thread;
                public int processId;
                public int threadId;
            }

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            static extern bool CreateProcess(
                string applicationName,
                StringBuilder commandLine,
                IntPtr processAttributes,
                IntPtr threadAttributes,
                bool inheritHandles,
                uint creationFlags,
                IntPtr environment,
                string currentDirectory,
                ref StartupInfo startupInfo,
                out ProcessInformation processInformation);

            [DllImport("kernel32.dll", SetLastError = true)]
            static extern bool CloseHandle(IntPtr handle);

            public static int Start(string executable, string arguments, string workingDirectory)
            {
                var commandLine = new StringBuilder(Quote(executable) + (String.IsNullOrWhiteSpace(arguments) ? "" : " " + arguments));
                var startup = new StartupInfo { cb = Marshal.SizeOf(typeof(StartupInfo)) };
                ProcessInformation information;
                // Inherit the parent's environment intact, including MO2/USVFS.
                // Do not reconstruct or filter the environment block.
                if (!CreateProcess(null, commandLine, IntPtr.Zero, IntPtr.Zero, false, CreateUnicodeEnvironment, IntPtr.Zero, workingDirectory, ref startup, out information))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法直接创建游戏进程。");
                CloseHandle(information.thread);
                CloseHandle(information.process);
                return information.processId;
            }

            static string Quote(string value)
            {
                return "\"" + value.Replace("\"", "\\\"") + "\"";
            }
        }
    }
}
