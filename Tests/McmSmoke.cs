using System;
using System.IO;
using System.Linq;
using SoD2SE;

static class McmSmoke
{
    static int Main()
    {
        var path = Path.Combine(Path.GetTempPath(), "sod2se-mcm-smoke-" + Guid.NewGuid().ToString("N") + ".ini");
        var install = Path.Combine(Path.GetTempPath(), "sod2se-mcm-game-" + Guid.NewGuid().ToString("N"));
        var game = Path.Combine(install, "steamapps", "common", "StateOfDecay2");
        var manifest = Path.Combine(install, "steamapps", "appmanifest_495420.acf");
        try
        {
            Directory.CreateDirectory(game);
            File.WriteAllText(manifest, "\"AppState\"\r\n{\r\n\t\"MountedConfig\"\r\n\t{\r\n\t\t\"language\"\t\"schinese\"\r\n\t}\r\n}\r\n");
            File.WriteAllText(path, "mcm.language=2\r\nmcm.language-source=manual\r\nmcm.shortcut-key=77\r\n");
            Environment.SetEnvironmentVariable("SOD2SE_MCM_CONFIG", path);
            var registry = McmRegistry.Initialize(game);
            if (registry.Language != McmLanguage.Chinese) throw new Exception("game app manifest language was not detected");
            if (!registry.LanguageWasAutoDetected) throw new Exception("language was not reported as auto-detected");
            var cleaned = File.ReadAllText(path);
            if (cleaned.Contains("mcm.language=") || cleaned.Contains("mcm.language-source=")) throw new Exception("legacy manual language was not removed");
            if (!cleaned.Contains("mcm.shortcut-key=77")) throw new Exception("removing the legacy language damaged other MCM settings");
            if (new McmLocalizedText("fallback").Resolve(McmLanguage.Chinese) != "fallback" ||
                new McmLocalizedText("fallback").Resolve(McmLanguage.English) != "fallback") throw new Exception("single-language fallback failed");
            registry.RegisterPlugin("mcm-smoke", new McmLocalizedText("测试页面", "Smoke Page"), new McmLocalizedText("中文说明", "English description"))
                .AddBool("enabled", "Enabled", true, "restart", true)
                .AddInt("limit", new McmLocalizedText("数值", "Limit"), 4, 1, 16, new McmLocalizedText("数字", "Number"), false);
            if (!registry.IsEnabled("mcm-smoke")) throw new Exception("default bool is false");
            registry.SetBool("mcm-smoke", "enabled", false);
            registry.SetInt("mcm-smoke", "limit", 99);
            registry.SetShortcut(77, 5);
            registry.SetChoiceShortcut(113, 0);
            registry = McmRegistry.Initialize(game);
            registry.RegisterPlugin("mcm-smoke", new McmLocalizedText("测试页面", "Smoke Page"), new McmLocalizedText("中文说明", "English description"))
                .AddBool("enabled", "Enabled", true, "restart", true)
                .AddInt("limit", new McmLocalizedText("数值", "Limit"), 4, 1, 16, new McmLocalizedText("数字", "Number"), false);
            var page = registry.Snapshot()[0];
            if (page.Loaded) throw new Exception("plugin was marked loaded before initialization");
            registry.MarkLoaded("mcm-smoke", true);
            page = registry.Snapshot().Single(p => p.Id == "mcm-smoke");
            if (!page.Loaded) throw new Exception("successful plugin initialization was not reflected as loaded");
            registry.MarkLoaded("mcm-smoke", false);
            page = registry.Snapshot().Single(p => p.Id == "mcm-smoke");
            if (page.Loaded) throw new Exception("plugin shutdown was not reflected as unloaded");
            if (registry.ShortcutKey != 77 || registry.ShortcutModifiers != 5) throw new Exception("shortcut did not persist");
            if (registry.ChoiceShortcutKey != 113 || registry.ChoiceShortcutModifiers != 0) throw new Exception("choice shortcut did not persist");
            bool rejected = false;
            try { registry.SetShortcut(115, 2); } catch (ArgumentException) { rejected = true; }
            if (!rejected || registry.ShortcutKey != 77) throw new Exception("reserved Alt+F4 accepted");
            if (page.Options[0].BoolValue || page.Options[1].IntValue != 16) throw new Exception("values did not persist");
            File.WriteAllText(manifest, "\"AppState\"\r\n{\r\n\t\"MountedConfig\"\r\n\t{\r\n\t\t\"language\"\t\"english\"\r\n\t}\r\n}\r\n");
            registry = McmRegistry.Initialize(game);
            registry.RegisterPlugin("mcm-smoke", new McmLocalizedText("测试页面", "Smoke Page"), new McmLocalizedText("中文说明", "English description"))
                .AddBool("enabled", "Enabled", true, "restart", true)
                .AddInt("limit", new McmLocalizedText("数值", "Limit"), 4, 1, 16, new McmLocalizedText("数字", "Number"), false);
            if (!registry.LanguageWasAutoDetected || registry.Language != McmLanguage.English) throw new Exception("game language change was not applied on the next initialization");
            page = registry.Snapshot().Single(p => p.Id == "mcm-smoke");
            if (page.Name != "Smoke Page" || page.Description != "English description" || page.Options[1].Label != "Limit") throw new Exception("English localization did not switch");
            File.WriteAllText(manifest, "\"AppState\"\r\n{\r\n\t\"MountedConfig\"\r\n\t{\r\n\t\t\"language\"\t\"schinese\"\r\n\t}\r\n}\r\n");
            registry = McmRegistry.Initialize(game);
            registry.RegisterPlugin("mcm-smoke", new McmLocalizedText("测试页面", "Smoke Page"), new McmLocalizedText("中文说明", "English description"))
                .AddBool("enabled", "Enabled", true, "restart", true)
                .AddInt("limit", new McmLocalizedText("数值", "Limit"), 4, 1, 16, new McmLocalizedText("数字", "Number"), false);
            if (registry.Language != McmLanguage.Chinese) throw new Exception("game language change back to Chinese was not applied");
            bool overlayChanged = false;
            Action<bool> overlayHandler = delegate(bool visible) { overlayChanged = visible; };
            McmChoiceBus.OverlayVisibilityChanged += overlayHandler;
            McmChoiceBus.SetOverlayVisible(true);
            if (!McmChoiceBus.OverlayVisible || !overlayChanged) throw new Exception("overlay visibility was not published");
            McmChoiceBus.SetOverlayVisible(false);
            McmChoiceBus.OverlayVisibilityChanged -= overlayHandler;
            page = registry.Snapshot().Single(p => p.Id == "mcm-smoke");
            if (page.Name != "测试页面" || page.Options[1].Label != "数值") throw new Exception("Chinese localization did not switch back");
            registry.ResetAll();
            page = registry.Snapshot().Single(p => p.Id == "mcm-smoke");
            if (!page.Options[0].BoolValue || page.Options[1].IntValue != 4) throw new Exception("reset did not restore defaults");
            rejected = false;
            try { registry.Snapshot(); registry.RegisterPlugin("mcm-smoke", "duplicate", ""); }
            catch (InvalidOperationException) { rejected = true; }
            if (!rejected) throw new Exception("duplicate page accepted");
            File.Delete(path);
            Directory.CreateDirectory(path);
            rejected = false;
            try { registry.SetBool("mcm-smoke", "enabled", false); }
            catch (IOException) { rejected = true; }
            if (!rejected || !registry.IsEnabled("mcm-smoke")) throw new Exception("failed save did not roll back");
            Directory.Delete(path);
            Console.WriteLine("PASS: MCM game-language detection, legacy override removal, persistence, plugin load status, clamping, shortcut validation, duplicate rejection, failed-save rollback and reset");
            return 0;
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(install)) Directory.Delete(install, true);
            Environment.SetEnvironmentVariable("SOD2SE_MCM_CONFIG", null);
        }
    }
}
