using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;

// Run inside a private USVFS instance; never connects to the real game.
static class LoaderVfsSmoke
{
    static int Main(string[] args)
    {
        try
        {
            if (args[0] == "child")
            {
                for (int i = 2; i < args.Length; i++)
                    if (File.ReadAllText(args[i]) != "sod2-usvfs-probe")
                        throw new Exception("Virtual content mismatch: " + args[i]);
                File.WriteAllText(args[1], "PASS");
                Thread.Sleep(2500);
                return 0;
            }
            var program = Assembly.LoadFrom(args[0]).GetType("SoD2SE.Loader.Program", true);
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            var background = program.GetMethod("ShouldStartBackgroundWorker", flags);
            if ((bool)background.Invoke(null, new object[] { new string[0] }))
                throw new Exception("USVFS must keep the original loader process alive");
            var parse = program.GetMethod("GetGameLaunchArguments", flags);
            Environment.SetEnvironmentVariable("SOD2_GAME_ARGS", "-environment");
            if ((string)parse.Invoke(null, new object[] { new[] { "--game-args", "" } }) != "")
                throw new Exception("Explicit empty game arguments lost");
            if ((string)parse.Invoke(null, new object[] { new[] { "--game-args=-steamlaunch -foo=\"a b\"" } }) != "-steamlaunch -foo=\"a b\"")
                throw new Exception("Game arguments were changed");
            var quote = program.GetMethod("QuoteProcessArgument", flags);
            string command = "child";
            for (int i = 1; i < args.Length; i++)
                command += " " + (string)quote.Invoke(null, new object[] { args[i] });
            var start = program.GetMethod("StartDirectGame", flags);
            using (var child = (Process)start.Invoke(null, new object[] {
                Assembly.GetExecutingAssembly().Location, true, command, true }))
            {
                if (!child.WaitForExit(15000) || child.ExitCode != 0)
                    throw new Exception("Virtual child failed");
            }
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "failure.txt"), error.ToString());
            return 1;
        }
    }
}
