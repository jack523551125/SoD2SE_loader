using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Globalization;
using Microsoft.Win32;
using System.Text;
using System.Text.RegularExpressions;

namespace SoD2SE
{
    public interface IMcmConfigurable
    {
        void RegisterMcm(McmRegistry registry);
    }

    public enum McmOptionType
    {
        Boolean = 0,
        Integer = 1,
        IntegerInput = 2
    }

    public enum McmLanguage
    {
        Chinese = 1,
        English = 2
    }

    // A small, versioned hand-off between managed Mods and the existing native
    // renderer.  The renderer never owns progression state; it only displays
    // these immutable cards and sends a nonce-protected selection back.
    public sealed class McmChoiceCard
    {
        public string Id { get; private set; }
        public string Title { get; private set; }
        public string Description { get; private set; }
        public int CurrentValue { get; private set; }
        public int NextValue { get; private set; }

        public McmChoiceCard(string id, string title, string description, int currentValue, int nextValue)
        {
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentException("选择卡片 ID 不能为空。", "id");
            Id = id;
            Title = title ?? "";
            Description = description ?? "";
            CurrentValue = currentValue;
            NextValue = nextValue;
        }
    }

    public sealed class McmChoiceState
    {
        public int Level { get; private set; }
        public int PendingLevels { get; private set; }
        public int Nonce { get; private set; }
        public string Character { get; private set; }
        public string Experience { get; private set; }
        public string NextLevelExperience { get; private set; }
        public IList<McmChoiceCard> Cards { get; private set; }
        public bool Visible { get { return Cards.Count > 0 && Nonce != 0; } }

        public McmChoiceState(int level, int pendingLevels, int nonce, IEnumerable<McmChoiceCard> cards)
            : this(level, pendingLevels, nonce, "", "", "", cards)
        {
        }

        public McmChoiceState(int level, int pendingLevels, int nonce, string character,
            string experience, string nextLevelExperience, IEnumerable<McmChoiceCard> cards)
        {
            var copy = (cards ?? Enumerable.Empty<McmChoiceCard>()).Where(item => item != null).Take(3).ToList();
            Level = Math.Max(0, level);
            PendingLevels = Math.Max(0, pendingLevels);
            Nonce = nonce;
            Character = character ?? "";
            Experience = experience ?? "";
            NextLevelExperience = nextLevelExperience ?? "";
            Cards = copy.AsReadOnly();
        }

        public static McmChoiceState Empty { get { return new McmChoiceState(0, 0, 0, null); } }
    }

    public sealed class McmChoiceSelection
    {
        public int Nonce { get; private set; }
        public int Index { get; private set; }
        internal McmChoiceSelection(int nonce, int index) { Nonce = nonce; Index = index; }
    }

    public static class McmChoiceBus
    {
        static readonly object sync = new object();
        static McmChoiceState state = McmChoiceState.Empty;
        static bool overlayVisible;
        public static event Action<McmChoiceSelection> SelectionRequested;
        public static event Action<bool> OverlayVisibilityChanged;

        public static McmChoiceState Snapshot()
        {
            lock (sync) return Clone(state);
        }

        public static void Publish(McmChoiceState next)
        {
            if (next == null) throw new ArgumentNullException("next");
            lock (sync) state = Clone(next);
        }

        public static bool TrySelect(int nonce, int index)
        {
            Action<McmChoiceSelection> handler;
            lock (sync)
            {
                if (!state.Visible || state.Nonce != nonce || index < 0 || index >= state.Cards.Count) return false;
                handler = SelectionRequested;
            }
            if (handler == null) return false;
            try
            {
                handler(new McmChoiceSelection(nonce, index));
                lock (sync) return !state.Visible || state.Nonce != nonce;
            }
            catch (Exception error) { Console.Error.WriteLine("[MCM] 选择回调失败：" + error.Message); return false; }
        }

        public static void Clear(int nonce)
        {
            lock (sync) if (state.Nonce == nonce) state = McmChoiceState.Empty;
        }

        public static bool OverlayVisible
        {
            get { lock (sync) return overlayVisible; }
        }

        // Pending choices can exist while the player is still in gameplay;
        // this flag reports whether the renderer is actually open.
        public static void SetOverlayVisible(bool visible)
        {
            Action<bool> handler = null;
            lock (sync)
            {
                if (overlayVisible == visible) return;
                overlayVisible = visible;
                handler = OverlayVisibilityChanged;
            }
            if (handler != null)
            {
                try { handler(visible); }
                catch (Exception error) { Console.Error.WriteLine("[MCM] 菜单可见性回调失败：" + error.Message); }
            }
        }

        static McmChoiceState Clone(McmChoiceState value)
        {
            return new McmChoiceState(value.Level, value.PendingLevels, value.Nonce, value.Character,
                value.Experience, value.NextLevelExperience,
                value.Cards.Select(item => new McmChoiceCard(item.Id, item.Title, item.Description, item.CurrentValue, item.NextValue)));
        }
    }

    public sealed class McmLocalizedText
    {
        public string Chinese { get; private set; }
        public string English { get; private set; }

        public McmLocalizedText(string fallback)
            : this(fallback, fallback)
        {
        }

        public McmLocalizedText(string chinese, string english)
        {
            Chinese = chinese ?? "";
            English = english ?? "";
        }

        public string Resolve(McmLanguage language)
        {
            return language == McmLanguage.Chinese
                ? (String.IsNullOrEmpty(Chinese) ? English : Chinese)
                : (String.IsNullOrEmpty(English) ? Chinese : English);
        }
    }

    public sealed class McmLanguageChangedEventArgs : EventArgs
    {
        public McmLanguage Previous { get; private set; }
        public McmLanguage Current { get; private set; }

        internal McmLanguageChangedEventArgs(McmLanguage previous, McmLanguage current)
        {
            Previous = previous;
            Current = current;
        }
    }

    public sealed class McmOptionSnapshot
    {
        public string Id { get; private set; }
        public string Label { get; private set; }
        public string Description { get; private set; }
        public McmOptionType Type { get; private set; }
        public bool RequiresRestart { get; private set; }
        public bool BoolValue { get; private set; }
        public int IntValue { get; private set; }
        public int Minimum { get; private set; }
        public int Maximum { get; private set; }

        internal McmOptionSnapshot(McmOption option)
        {
            Id = option.Id;
            Label = option.Label;
            Description = option.Description;
            Type = option.Type;
            RequiresRestart = option.RequiresRestart;
            BoolValue = option.BoolValue;
            IntValue = option.IntValue;
            Minimum = option.Minimum;
            Maximum = option.Maximum;
        }
    }

    public sealed class McmPageSnapshot
    {
        public string Id { get; private set; }
        public string Name { get; private set; }
        public string Description { get; private set; }
        public bool Loaded { get; private set; }
        public IList<McmOptionSnapshot> Options { get; private set; }

        internal McmPageSnapshot(McmPage page, bool loaded)
        {
            Id = page.Id;
            Name = page.Name;
            Description = page.Description;
            Loaded = loaded;
            Options = page.Options.Select(item => new McmOptionSnapshot(item)).ToList();
        }
    }

    public sealed class McmPage
    {
        readonly McmRegistry registry;
        readonly List<McmOption> options = new List<McmOption>();

        readonly McmLocalizedText localizedName;
        readonly McmLocalizedText localizedDescription;

        internal McmPage(McmRegistry owner, string id, McmLocalizedText name, McmLocalizedText description)
        {
            registry = owner;
            Id = id;
            localizedName = name;
            localizedDescription = description ?? new McmLocalizedText("", "");
        }

        public string Id { get; private set; }
        public string Name { get { return localizedName.Resolve(registry.Language); } }
        public string Description { get { return localizedDescription.Resolve(registry.Language); } }
        internal IList<McmOption> Options { get { return options; } }

        public McmPage AddBool(string id, string label, bool defaultValue, string description, bool requiresRestart)
        {
            return AddBool(id, new McmLocalizedText(label, label), defaultValue,
                new McmLocalizedText(description ?? "", description ?? ""), requiresRestart);
        }

        public McmPage AddBool(string id, McmLocalizedText label, bool defaultValue,
            McmLocalizedText description, bool requiresRestart)
        {
            if (options.Any(item => String.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("MCM option ID duplicated: " + id);
            options.Add(registry.CreateOption(this, id, label, description, McmOptionType.Boolean,
                defaultValue ? "true" : "false", 0, 1, requiresRestart));
            return this;
        }

        public McmPage AddBool(string id, string label, bool defaultValue)
        {
            return AddBool(id, label, defaultValue, "", true);
        }

        public McmPage AddInt(string id, string label, int defaultValue, int minimum, int maximum,
            string description, bool requiresRestart)
        {
            return AddInt(id, new McmLocalizedText(label, label), defaultValue, minimum, maximum,
                new McmLocalizedText(description ?? "", description ?? ""), requiresRestart);
        }

        public McmPage AddInt(string id, McmLocalizedText label, int defaultValue, int minimum, int maximum,
            McmLocalizedText description, bool requiresRestart)
        {
            if (options.Any(item => String.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("MCM option ID duplicated: " + id);
            if (minimum > maximum) throw new ArgumentException("MCM integer range is invalid.");
            defaultValue = Math.Max(minimum, Math.Min(maximum, defaultValue));
            options.Add(registry.CreateOption(this, id, label, description, McmOptionType.Integer,
                defaultValue.ToString(CultureInfo.InvariantCulture), minimum, maximum, requiresRestart));
            return this;
        }

        public McmPage AddIntInput(string id, string label, int defaultValue, int minimum, int maximum,
            string description, bool requiresRestart)
        {
            return AddIntInput(id, new McmLocalizedText(label, label), defaultValue, minimum, maximum,
                new McmLocalizedText(description ?? "", description ?? ""), requiresRestart);
        }

        public McmPage AddIntInput(string id, McmLocalizedText label, int defaultValue, int minimum, int maximum,
            McmLocalizedText description, bool requiresRestart)
        {
            if (options.Any(item => String.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("MCM option ID duplicated: " + id);
            if (minimum > maximum) throw new ArgumentException("MCM integer range is invalid.");
            defaultValue = Math.Max(minimum, Math.Min(maximum, defaultValue));
            options.Add(registry.CreateOption(this, id, label, description, McmOptionType.IntegerInput,
                defaultValue.ToString(CultureInfo.InvariantCulture), minimum, maximum, requiresRestart));
            return this;
        }

        public McmPage AddInt(string id, string label, int defaultValue, int minimum, int maximum)
        {
            return AddInt(id, label, defaultValue, minimum, maximum, "", true);
        }
    }

    public sealed class McmOption
    {
        readonly McmRegistry registry;
        readonly McmLocalizedText localizedLabel;
        readonly McmLocalizedText localizedDescription;

        internal McmOption(McmRegistry owner, string id, McmLocalizedText label, McmLocalizedText description, McmOptionType type,
            string value, string defaultValue, int minimum, int maximum, bool requiresRestart)
        {
            registry = owner;
            Id = id;
            localizedLabel = label;
            localizedDescription = description ?? new McmLocalizedText("", "");
            Type = type;
            Value = value;
            DefaultValue = defaultValue;
            Minimum = minimum;
            Maximum = maximum;
            RequiresRestart = requiresRestart;
        }

        public string Id { get; private set; }
        public string Label { get { return localizedLabel.Resolve(registry.Language); } }
        public string Description { get { return localizedDescription.Resolve(registry.Language); } }
        public McmOptionType Type { get; private set; }
        public bool RequiresRestart { get; private set; }
        internal string Value { get; set; }
        internal string DefaultValue { get; private set; }
        public bool BoolValue { get { return String.Equals(Value, "true", StringComparison.OrdinalIgnoreCase); } }
        public int IntValue
        {
            get
            {
                int value;
                return Int32.TryParse(Value, out value) ? Math.Max(Minimum, Math.Min(Maximum, value)) : Minimum;
            }
        }
        public int Minimum { get; private set; }
        public int Maximum { get; private set; }
    }

    public sealed class McmRegistry
    {
        static readonly object StaticSync = new object();
        static McmRegistry current;
        readonly object sync = new object();
        readonly Dictionary<string, McmPage> pages = new Dictionary<string, McmPage>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, bool> loaded = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly string gameDirectory;
        McmLanguage detectedLanguage = McmLanguage.English;

        public static McmRegistry Current
        {
            get { lock (StaticSync) return current; }
        }

        public string ConfigPath { get; private set; }

        // Mods may observe this interface without depending on the native MCM
        // renderer. A mod can use the detected game language for its own UI, or
        // ignore it and provide one fallback string through McmLocalizedText.
        public event EventHandler<McmLanguageChangedEventArgs> LanguageChanged
        {
            add { }
            remove { }
        }

        public int ShortcutKey { get { lock (sync) return ReadNumber("mcm.shortcut-key", McmKeys.MenuKey); } }
        public int ShortcutModifiers { get { lock (sync) return ReadNumber("mcm.shortcut-modifiers", McmKeys.MenuModifiers); } }
        public int ChoiceShortcutKey { get { lock (sync) return ReadNumber("mcm.choice-shortcut-key", McmKeys.ChoiceKey); } }
        public int ChoiceShortcutModifiers { get { lock (sync) return ReadNumber("mcm.choice-shortcut-modifiers", McmKeys.ChoiceModifiers); } }
        public McmLanguage Language { get { lock (sync) return detectedLanguage; } }
        public bool LanguageWasAutoDetected { get { lock (sync) return true; } }

        int ReadNumber(string key, int fallback)
        {
            string raw;
            int number;
            return values.TryGetValue(key, out raw) && Int32.TryParse(raw, out number) ? number : fallback;
        }

        public static bool ValidShortcut(int key, int modifiers)
        {
            // The accepted keys live in McmKeys so the recorder in the native
            // host and this validator are the same table.
            return McmKeys.ValidKey(key, modifiers);
        }

        public void SetShortcut(int key, int modifiers)
        {
            if (!ValidShortcut(key, modifiers)) throw new ArgumentException("快捷键无效：支持 Ctrl/Alt/Shift 加 F1–F12、字母、数字或导航键；保留 Alt+F4 和 Esc。");
            lock (sync)
            {
                string oldKey, oldModifiers;
                values.TryGetValue("mcm.shortcut-key", out oldKey);
                values.TryGetValue("mcm.shortcut-modifiers", out oldModifiers);
                values["mcm.shortcut-key"] = key.ToString();
                values["mcm.shortcut-modifiers"] = modifiers.ToString();
                try { SaveLocked(); }
                catch
                {
                    if (oldKey == null) values.Remove("mcm.shortcut-key"); else values["mcm.shortcut-key"] = oldKey;
                    if (oldModifiers == null) values.Remove("mcm.shortcut-modifiers"); else values["mcm.shortcut-modifiers"] = oldModifiers;
                    throw;
                }
            }
        }

        public void SetChoiceShortcut(int key, int modifiers)
        {
            if (!ValidShortcut(key, modifiers)) throw new ArgumentException("升级界面快捷键无效。");
            lock (sync)
            {
                string oldKey, oldModifiers;
                values.TryGetValue("mcm.choice-shortcut-key", out oldKey);
                values.TryGetValue("mcm.choice-shortcut-modifiers", out oldModifiers);
                values["mcm.choice-shortcut-key"] = key.ToString(CultureInfo.InvariantCulture);
                values["mcm.choice-shortcut-modifiers"] = modifiers.ToString(CultureInfo.InvariantCulture);
                try { SaveLocked(); }
                catch
                {
                    if (oldKey == null) values.Remove("mcm.choice-shortcut-key"); else values["mcm.choice-shortcut-key"] = oldKey;
                    if (oldModifiers == null) values.Remove("mcm.choice-shortcut-modifiers"); else values["mcm.choice-shortcut-modifiers"] = oldModifiers;
                    throw;
                }
            }
        }

        // Kept as a binary-compatible shim for older third-party plugins. The
        // MCM no longer accepts a language override; game language is detected
        // during initialization and is read-only for this process.
        [Obsolete("MCM language follows the game language and cannot be selected manually.", false)]
        public void SetLanguage(McmLanguage language)
        {
            throw new NotSupportedException("MCM language follows the detected game language.");
        }

        public static McmRegistry Initialize(string gameDirectory)
        {
            lock (StaticSync)
            {
                current = new McmRegistry(gameDirectory);
                current.Load();
                return current;
            }
        }

        McmRegistry(string gameDirectory)
        {
            this.gameDirectory = gameDirectory ?? "";
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (String.IsNullOrWhiteSpace(local)) local = String.IsNullOrWhiteSpace(gameDirectory) ? AppDomain.CurrentDomain.BaseDirectory : gameDirectory;
            var overridePath = Environment.GetEnvironmentVariable("SOD2SE_MCM_CONFIG");
            ConfigPath = String.IsNullOrWhiteSpace(overridePath)
                ? Path.Combine(local, FrameworkInfo.GameDataFolderName, "SoD2SE", "mcm.ini")
                : Path.GetFullPath(overridePath);
        }

        public McmPage RegisterPlugin(string id, string name, string description)
        {
            return RegisterPlugin(id, new McmLocalizedText(name, name), new McmLocalizedText(description ?? "", description ?? ""));
        }

        public McmPage RegisterPlugin(string id, McmLocalizedText name, McmLocalizedText description)
        {
            ValidateId(id);
            lock (sync)
            {
                if (pages.ContainsKey(id)) throw new InvalidOperationException("MCM 插件 ID 重复：" + id);
                if (name == null) name = new McmLocalizedText(id, id);
                var page = new McmPage(this, id, name, description ?? new McmLocalizedText("", ""));
                pages.Add(id, page);
                loaded[id] = false;
                return page;
            }
        }

        internal McmOption CreateOption(McmPage page, string id, McmLocalizedText label, McmLocalizedText description,
            McmOptionType type, string defaultValue, int minimum, int maximum, bool requiresRestart)
        {
            ValidateId(id);
            if (label == null || (String.IsNullOrWhiteSpace(label.Chinese) && String.IsNullOrWhiteSpace(label.English))) throw new ArgumentException("MCM option label cannot be empty.");
            lock (sync)
            {
                string key = page.Id + "." + id;
                string value;
                if (!values.TryGetValue(key, out value))
                {
                    value = defaultValue;
                    values[key] = value;
                }
                if (type == McmOptionType.Boolean)
                    value = ParseBool(value, defaultValue == "true") ? "true" : "false";
                else
                {
                    int parsed;
                    if (!Int32.TryParse(value, out parsed)) parsed = Int32.Parse(defaultValue);
                    value = Math.Max(minimum, Math.Min(maximum, parsed)).ToString(CultureInfo.InvariantCulture);
                }
                values[key] = value;
                return new McmOption(this, id, label, description, type, value, defaultValue, minimum, maximum, requiresRestart);
            }
        }

        public bool IsEnabled(string pluginId)
        {
            lock (sync)
            {
                McmPage page;
                if (!pages.TryGetValue(pluginId, out page)) return true;
                var option = page.Options.FirstOrDefault(item => String.Equals(item.Id, "enabled", StringComparison.OrdinalIgnoreCase));
                return option == null || option.BoolValue;
            }
        }

        public void SetBool(string pageId, string optionId, bool value)
        {
            lock (sync)
            {
                McmPage page;
                if (!pages.TryGetValue(pageId, out page)) throw new KeyNotFoundException("MCM 页面不存在：" + pageId);
                var option = page.Options.FirstOrDefault(item => String.Equals(item.Id, optionId, StringComparison.OrdinalIgnoreCase));
                if (option == null || option.Type != McmOptionType.Boolean) throw new KeyNotFoundException("MCM 布尔选项不存在：" + pageId + "." + optionId);
                SetAndSave(pageId, option, value ? "true" : "false");
            }
        }

        public void SetInt(string pageId, string optionId, int value)
        {
            lock (sync)
            {
                McmPage page;
                if (!pages.TryGetValue(pageId, out page)) throw new KeyNotFoundException("MCM 页面不存在：" + pageId);
                var option = page.Options.FirstOrDefault(item => String.Equals(item.Id, optionId, StringComparison.OrdinalIgnoreCase));
                if (option == null || (option.Type != McmOptionType.Integer && option.Type != McmOptionType.IntegerInput)) throw new KeyNotFoundException("MCM 整数选项不存在：" + pageId + "." + optionId);
                SetAndSave(pageId, option, Math.Max(option.Minimum, Math.Min(option.Maximum, value)).ToString(CultureInfo.InvariantCulture));
            }
        }

        public IList<McmPageSnapshot> Snapshot()
        {
            lock (sync)
                return pages.Values.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(item => new McmPageSnapshot(item, loaded.ContainsKey(item.Id) && loaded[item.Id])).ToList();
        }

        public void MarkLoaded(string id, bool value)
        {
            lock (sync) { if (pages.ContainsKey(id)) loaded[id] = value; }
        }

        public void ResetAll()
        {
            lock (sync)
            {
                var previous = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
                foreach (var page in pages.Values)
                    foreach (var option in page.Options)
                    {
                        option.Value = option.DefaultValue;
                        values[page.Id + "." + option.Id] = option.DefaultValue;
                    }
                values["mcm.choice-shortcut-key"] = McmKeys.ChoiceKey.ToString(CultureInfo.InvariantCulture);
                values["mcm.choice-shortcut-modifiers"] = McmKeys.ChoiceModifiers.ToString(CultureInfo.InvariantCulture);
                try { SaveLocked(); }
                catch
                {
                    values.Clear();
                    foreach (var pair in previous) values[pair.Key] = pair.Value;
                    foreach (var page in pages.Values)
                        foreach (var option in page.Options) option.Value = values[page.Id + "." + option.Id];
                    throw;
                }
            }
        }

        void Load()
        {
            lock (sync)
            {
                values.Clear();
                try
                {
                    if (File.Exists(ConfigPath)) foreach (var raw in File.ReadAllLines(ConfigPath, Encoding.UTF8))
                    {
                        var line = raw.Trim();
                        if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                        int split = line.IndexOf('=');
                        if (split <= 0) continue;
                        values[line.Substring(0, split).Trim()] = line.Substring(split + 1).Trim();
                    }
                    if (!ValidShortcut(ShortcutKey, ShortcutModifiers))
                    {
                        values["mcm.shortcut-key"] = McmKeys.MenuKey.ToString(CultureInfo.InvariantCulture);
                        values["mcm.shortcut-modifiers"] = McmKeys.MenuModifiers.ToString(CultureInfo.InvariantCulture);
                    }
                    if (!ValidShortcut(ChoiceShortcutKey, ChoiceShortcutModifiers))
                    {
                        values["mcm.choice-shortcut-key"] = McmKeys.ChoiceKey.ToString(CultureInfo.InvariantCulture);
                        values["mcm.choice-shortcut-modifiers"] = McmKeys.ChoiceModifiers.ToString(CultureInfo.InvariantCulture);
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                EnsureLanguageLocked();
            }
        }

        void EnsureLanguageLocked()
        {
            detectedLanguage = McmLanguageDetector.Detect(gameDirectory);
            // 0.6.x persisted a manual selection. It is intentionally discarded
            // so a stale value can never override the current game installation.
            var removedLegacyValue = values.Remove("mcm.language");
            var removedLegacySource = values.Remove("mcm.language-source");
            if (!removedLegacyValue && !removedLegacySource) return;
            try { SaveLocked(); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        void SaveLocked()
        {
            var temporary = ConfigPath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                var directory = Path.GetDirectoryName(ConfigPath);
                if (!String.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                var lines = new List<string> { "# SoD2SE MCM settings; generated file." };
                lines.AddRange(values.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(item => item.Key + "=" + item.Value));
                File.WriteAllLines(temporary, lines, new UTF8Encoding(false));
                if (File.Exists(ConfigPath)) File.Replace(temporary, ConfigPath, null);
                else File.Move(temporary, ConfigPath);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        void SetAndSave(string pageId, McmOption option, string value)
        {
            var previous = option.Value;
            option.Value = value;
            values[pageId + "." + option.Id] = value;
            try { SaveLocked(); }
            catch
            {
                option.Value = previous;
                values[pageId + "." + option.Id] = previous;
                throw;
            }
        }

        static bool ParseBool(string value, bool fallback)
        {
            bool result;
            return Boolean.TryParse(value, out result) ? result : fallback;
        }

        static void ValidateId(string value)
        {
            if (!FrameworkInfo.IsValidPluginId(value)) throw new ArgumentException("MCM ID 无效：" + value);
        }
    }

    internal static class McmLanguageDetector
    {
        public static McmLanguage Detect(string gameDirectory)
        {
            string value;
            McmLanguage language;
            // A game-specific Unreal config override is the closest available
            // indication of what the game will actually request at runtime.
            foreach (var path in ConfigCandidates(gameDirectory))
                if (TryFile(path, out value) && TryLanguage(value, out language)) return language;
            // Steam records the language mounted for this game in the app
            // manifest. Prefer it over the global Steam client UI language.
            foreach (var path in ManifestCandidates(gameDirectory))
                if (TryManifest(path, out value) && TryLanguage(value, out language)) return language;
            if (TryRegistry("Software\\Valve\\Steam\\Apps\\" + FrameworkInfo.SteamAppId, "Language", out value) && TryLanguage(value, out language)) return language;
            if (TryRegistry("Software\\WOW6432Node\\Valve\\Steam\\Apps\\" + FrameworkInfo.SteamAppId, "Language", out value) && TryLanguage(value, out language)) return language;
            if (TryRegistry("Software\\Valve\\Steam", "Language", out value) && TryLanguage(value, out language)) return language;
            if (TryRegistry("Software\\WOW6432Node\\Valve\\Steam", "Language", out value) && TryLanguage(value, out language)) return language;
            return CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                ? McmLanguage.Chinese : McmLanguage.English;
        }

        static IEnumerable<string> ManifestCandidates(string gameDirectory)
        {
            if (String.IsNullOrWhiteSpace(gameDirectory)) yield break;
            string fullPath;
            try { fullPath = Path.GetFullPath(gameDirectory); }
            catch (ArgumentException) { yield break; }
            catch (PathTooLongException) { yield break; }
            DirectoryInfo current;
            try { current = new DirectoryInfo(fullPath); }
            catch (ArgumentException) { yield break; }
            catch (PathTooLongException) { yield break; }
            for (int i = 0; current != null && i < 8; i++, current = current.Parent)
            {
                if (!String.Equals(current.Name, "common", StringComparison.OrdinalIgnoreCase)) continue;
                var steamApps = current.Parent;
                if (steamApps != null)
                    yield return Path.Combine(steamApps.FullName, "appmanifest_" + FrameworkInfo.SteamAppId + ".acf");
            }
        }

        static bool TryManifest(string path, out string value)
        {
            value = null;
            try
            {
                if (!File.Exists(path)) return false;
                var contents = File.ReadAllText(path, Encoding.UTF8);
                // MountedConfig reflects the language actually present on disk;
                // UserConfig is the next-best choice during an install/update.
                foreach (var section in new[] { "MountedConfig", "UserConfig" })
                {
                    var match = Regex.Match(contents,
                        "\"" + section + "\"\\s*\\{[^}]*\"language\"\\s*\"([^\"]+)\"",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                    if (!match.Success) continue;
                    value = match.Groups[1].Value.Trim();
                    if (!String.IsNullOrWhiteSpace(value)) return true;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return false;
        }

        static IEnumerable<string> ConfigCandidates(string gameDirectory)
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!String.IsNullOrWhiteSpace(local))
            {
                yield return Path.Combine(local, FrameworkInfo.GameDataFolderName, "Saved", "Config", "WindowsNoEditor", "GameUserSettings.ini");
                yield return Path.Combine(local, FrameworkInfo.GameDataFolderName, "Saved", "Config", "WindowsNoEditor", "Game.ini");
            }
            if (!String.IsNullOrWhiteSpace(gameDirectory))
            {
                yield return Path.Combine(gameDirectory, "Saved", "Config", "WindowsNoEditor", "GameUserSettings.ini");
                yield return Path.Combine(gameDirectory, FrameworkInfo.GameDataFolderName, "Saved", "Config", "WindowsNoEditor", "GameUserSettings.ini");
            }
        }

        static bool TryRegistry(string path, string name, out string value)
        {
            return TryRegistryRoot(Registry.CurrentUser, path, name, out value) ||
                TryRegistryRoot(Registry.LocalMachine, path, name, out value);
        }

        static bool TryRegistryRoot(RegistryKey root, string path, string name, out string value)
        {
            value = null;
            try
            {
                using (var key = root.OpenSubKey(path))
                    value = key == null ? null : Convert.ToString(key.GetValue(name, null), CultureInfo.InvariantCulture);
                return !String.IsNullOrWhiteSpace(value);
            }
            catch (System.Security.SecurityException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        static bool TryFile(string path, out string value)
        {
            value = null;
            try
            {
                if (!File.Exists(path)) return false;
                foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    var split = line.IndexOf('=');
                    if (split <= 0) continue;
                    var key = line.Substring(0, split).Trim();
                    if (key.IndexOf("language", StringComparison.OrdinalIgnoreCase) < 0 &&
                        key.IndexOf("locale", StringComparison.OrdinalIgnoreCase) < 0 &&
                        key.IndexOf("culture", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    value = line.Substring(split + 1).Trim();
                    if (!String.IsNullOrWhiteSpace(value)) return true;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return false;
        }

        static bool TryLanguage(string value, out McmLanguage language)
        {
            value = (value ?? "").Trim().ToLowerInvariant().Replace('_', '-');
            if (value == "zh" || value.StartsWith("zh-", StringComparison.Ordinal) ||
                value == "chinese" || value == "schinese" || value == "tchinese" ||
                value == "chs" || value == "cht")
            { language = McmLanguage.Chinese; return true; }
            if (value == "en" || value.StartsWith("en-", StringComparison.Ordinal) || value == "english")
            { language = McmLanguage.English; return true; }
            language = McmLanguage.English;
            return false;
        }
    }
}
