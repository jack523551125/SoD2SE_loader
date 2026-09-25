using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace SoD2SE
{
    // Kinds and tones are part of the MCM ABI (see Native/McmProtocol.h).
    public enum UiNodeKind
    {
        Section = 0,
        Text = 1,
        KeyValue = 2,
        Progress = 3,
        Row = 4,
        Button = 5,
        Note = 6,
        Separator = 7
    }

    public enum UiTone
    {
        Normal = 0,
        Positive = 1,
        Warning = 2,
        Danger = 3,
        Muted = 4,
        Accent = 5
    }

    // One immutable row of a screen.  Mods never draw; they describe rows and
    // the native host owns spacing, tone, input and the window shell.
    public sealed class UiNode
    {
        public UiNodeKind Kind { get; private set; }
        public UiTone Tone { get; private set; }
        public string Label { get; private set; }
        public string Value { get; private set; }
        public string Description { get; private set; }
        public string Action { get; private set; }
        public int Current { get; private set; }
        public int Maximum { get; private set; }

        public UiNode(UiNodeKind kind, string label, string value, string description, UiTone tone,
            string action, int current, int maximum)
        {
            Kind = kind;
            Label = label ?? "";
            Value = value ?? "";
            Description = description ?? "";
            Tone = tone;
            Action = action ?? "";
            Current = current;
            Maximum = maximum;
        }

        internal UiNode Clone()
        {
            return new UiNode(Kind, Label, Value, Description, Tone, Action, Current, Maximum);
        }
    }

    // A read-only view of one screen for the native host.  The owning surface is
    // kept internal so callers cannot mutate live state through a snapshot.
    public sealed class UiSurfaceSnapshot
    {
        public string Id { get; private set; }
        public string Title { get; private set; }
        public string Subtitle { get; private set; }
        public int ShortcutKey { get; private set; }
        public int ShortcutModifiers { get; private set; }
        public int Priority { get; private set; }
        public int Revision { get; private set; }
        public int NodeStart { get; private set; }
        public int NodeCount { get; private set; }
        public bool Modal { get; private set; }
        public bool WantsOpen { get; private set; }
        public bool IsOpen { get; private set; }
        public string ShortcutText { get; private set; }
        public IList<UiNode> Nodes { get; private set; }
        internal UiSurface Owner { get; private set; }
        public int WantRevision { get; private set; }

        internal UiSurfaceSnapshot(UiSurface owner, string title, string subtitle, int shortcutKey, int shortcutModifiers,
            int priority, int revision, int nodeStart, bool modal, bool wantsOpen, bool isOpen, int wantRevision, IList<UiNode> nodes)
        {
            Owner = owner;
            Id = owner.Id;
            Title = title ?? "";
            Subtitle = subtitle ?? "";
            ShortcutKey = shortcutKey;
            ShortcutModifiers = shortcutModifiers;
            Priority = priority;
            Revision = revision;
            NodeStart = nodeStart;
            NodeCount = nodes.Count;
            Modal = modal;
            WantsOpen = wantsOpen;
            IsOpen = isOpen;
            WantRevision = wantRevision;
            ShortcutText = UiShortcut.Describe(shortcutKey, shortcutModifiers);
            Nodes = nodes;
        }
    }

    public static class UiShortcut
    {
        public static string Describe(int key, int modifiers)
        {
            var text = new StringBuilder();
            if ((modifiers & McmKeys.ControlModifier) != 0) text.Append("Ctrl + ");
            if ((modifiers & McmKeys.AltModifier) != 0) text.Append("Alt + ");
            if ((modifiers & McmKeys.ShiftModifier) != 0) text.Append("Shift + ");
            if (McmKeys.IsFunctionKey(key)) text.Append("F").Append(McmKeys.FunctionKeyNumber(key));
            else if (key >= McmKeys.DigitKeyFirst && key <= McmKeys.DigitKeyLast) text.Append((char)key);
            else if (key >= McmKeys.LetterKeyFirst && key <= McmKeys.LetterKeyLast) text.Append((char)key);
            else if (key >= McmKeys.NavigationKeyFirst && key <= McmKeys.NavigationKeyLast)
                text.Append("Nav").Append(McmKeys.NavigationKeyNumber(key));
            else if (key == McmKeys.InsertKey) text.Append("Insert");
            else if (key == McmKeys.DeleteKey) text.Append("Delete");
            else if (key == McmKeys.TabKey) text.Append("Tab");
            else text.Append("Key ").Append(key.ToString(CultureInfo.InvariantCulture));
            return text.ToString();
        }
    }

    // A screen owned by one mod.  Build it once with a callback and the
    // registry re-evaluates it before every publish, so live values such as
    // experience or a buff count stay current without any refresh plumbing.
    public sealed class UiSurface
    {
        readonly object sync = new object();
        readonly UiRegistry registry;
        readonly McmLocalizedText title;
        readonly McmLocalizedText subtitle;
        readonly List<UiNode> staged = new List<UiNode>();
        readonly List<UiNode> published = new List<UiNode>();
        Action<UiSurface> build;
        bool wantsOpen, isOpen;
        int wantRevision;
        int priority;
        bool modal;
        int revision;
        string lastError = "";

        internal UiSurface(UiRegistry owner, string id, McmLocalizedText title, McmLocalizedText subtitle)
        {
            registry = owner;
            Id = id;
            this.title = title;
            this.subtitle = subtitle;
        }

        public string Id { get; private set; }
        public string Title { get { return title.Resolve(registry.Language); } }
        public string Subtitle { get { return subtitle.Resolve(registry.Language); } }
        public int ShortcutKey { get { return registry.SurfaceShortcutKey(Id); } }
        public int ShortcutModifiers { get { return registry.SurfaceShortcutModifiers(Id); } }
        public string ShortcutText { get { return UiShortcut.Describe(ShortcutKey, ShortcutModifiers); } }
        public int Priority { get { lock (sync) return priority; } }
        public bool Modal { get { lock (sync) return modal; } }
        public int Revision { get { lock (sync) return revision; } }
        public bool IsOpen { get { lock (sync) return isOpen; } }
        public bool IsOpenRequested { get { lock (sync) return wantsOpen; } }
        public string LastError { get { lock (sync) return lastError; } }

        // Raised when the player activates a Button row.  The callback runs on
        // the framework publish thread; take your own lock before touching state.
        public event Action<string> ActionRequested;

        public UiSurface SetShortcut(int key, int modifiers)
        {
            registry.SetSurfaceShortcut(Id, key, modifiers);
            return this;
        }

        public UiSurface SetPriority(int value)
        {
            lock (sync) priority = value;
            return this;
        }

        // A modal screen is never replaced by the settings panel or another
        // screen while it is open.
        public UiSurface SetModal(bool value)
        {
            lock (sync) modal = value;
            return this;
        }

        // Re-evaluated before every publish.  Wrap live values in your own lock.
        public UiSurface SetBuild(Action<UiSurface> value)
        {
            lock (sync) build = value;
            return this;
        }

        // Level-triggered request: ask once when the screen should appear, and
        // the host reports the real state back through IsOpen.
        public void Open()
        {
            lock (sync) { if (wantsOpen) return; wantsOpen = true; wantRevision++; }
        }

        public void Close()
        {
            lock (sync) { if (!wantsOpen) return; wantsOpen = false; wantRevision++; }
        }

        public void Clear()
        {
            lock (sync) staged.Clear();
        }

        public UiSurface Section(string label)
        {
            return Add(new UiNode(UiNodeKind.Section, label, "", "", UiTone.Accent, "", 0, 0));
        }

        public UiSurface Text(string text, UiTone tone = UiTone.Normal)
        {
            return Add(new UiNode(UiNodeKind.Text, text, "", "", tone, "", 0, 0));
        }

        public UiSurface KeyValue(string label, string value, string description = null, UiTone tone = UiTone.Normal)
        {
            return Add(new UiNode(UiNodeKind.KeyValue, label, value, description, tone, "", 0, 0));
        }

        public UiSurface Progress(string label, int current, int maximum, string value = null, string description = null)
        {
            return Add(new UiNode(UiNodeKind.Progress, label, value, description, UiTone.Accent, "", current, maximum));
        }

        public UiSurface Row(string label, string value, string description = null, UiTone tone = UiTone.Normal)
        {
            return Add(new UiNode(UiNodeKind.Row, label, value, description, tone, "", 0, 0));
        }

        public UiSurface Button(string label, string action, string description = null)
        {
            if (String.IsNullOrWhiteSpace(action)) throw new ArgumentException("UI 按钮必须提供动作标识。", "action");
            return Add(new UiNode(UiNodeKind.Button, label, "", description, UiTone.Accent, action, 0, 0));
        }

        public UiSurface Note(string text, UiTone tone = UiTone.Muted)
        {
            return Add(new UiNode(UiNodeKind.Note, "", "", text, tone, "", 0, 0));
        }

        public UiSurface Separator()
        {
            return Add(new UiNode(UiNodeKind.Separator, "", "", "", UiTone.Normal, "", 0, 0));
        }

        UiSurface Add(UiNode node)
        {
            lock (sync)
            {
                if (staged.Count >= UiRegistry.MaxNodesPerSurface) return this;
                staged.Add(node);
            }
            return this;
        }

        // Commits the rows built since the last Clear().  A surface with a
        // build callback never needs to call this itself.
        public void Publish()
        {
            lock (sync)
            {
                published.Clear();
                published.AddRange(staged);
                staged.Clear();
                revision++;
            }
        }

        internal void Refresh()
        {
            Action<UiSurface> callback;
            lock (sync) callback = build;
            if (callback == null) return;
            try
            {
                Clear();
                callback(this);
                Publish();
                lock (sync) lastError = "";
            }
            catch (Exception error)
            {
                Clear();
                lock (sync) lastError = error.Message;
                Console.Error.WriteLine("[UI] " + Id + " 构建失败：" + error.Message);
            }
        }

        internal void SyncHostState(int seenRevision, bool open)
        {
            lock (sync)
            {
                isOpen = open;
                // The host changed the state on its own (shortcut or Esc) and
                // has already acted on our latest request; mirror it.
                if (seenRevision == wantRevision && wantsOpen != open) wantsOpen = open;
            }
        }

        internal void SnapshotState(out bool wants, out bool open, out int revisionValue, out int requestRevision,
            out int priorityValue, out bool modalValue, out string error)
        {
            lock (sync)
            {
                wants = wantsOpen;
                open = isOpen;
                revisionValue = revision;
                requestRevision = wantRevision;
                priorityValue = priority;
                modalValue = modal;
                error = lastError;
            }
        }

        internal IList<UiNode> CopyNodes()
        {
            lock (sync) return published.Select(item => item.Clone()).ToList();
        }

        internal void DispatchAction(string action)
        {
            var handler = ActionRequested;
            if (handler == null) return;
            try { handler(action); }
            catch (Exception error) { Console.Error.WriteLine("[UI] " + Id + " 动作 " + action + " 失败：" + error.Message); }
        }
    }

    // Owns every registered screen and persists per-screen shortcuts.  The
    // settings menu stays in MCM; this registry is what makes a gameplay screen
    // part of the game instead of a config page.
    public sealed class UiRegistry
    {
        public const int MaxSurfaces = 6;
        public const int MaxNodes = 96;
        public const int MaxNodesPerSurface = 24;

        // F2..F7 are the sensible defaults for mod screens.
        static readonly int[] DefaultShortcutKeys = McmKeys.DefaultSurfaceKeys(MaxSurfaces);
        static readonly object StaticSync = new object();
        static UiRegistry current;

        readonly object sync = new object();
        readonly List<UiSurface> surfaces = new List<UiSurface>();
        readonly Dictionary<string, int> shortcutKeys = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, int> shortcutModifiers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> stored = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string choiceOwner;
        int choiceRequest;

        public static UiRegistry Current { get { lock (StaticSync) return current; } }
        public string ConfigPath { get; private set; }
        public event EventHandler<McmLanguageChangedEventArgs> LanguageChanged;

        public static UiRegistry Initialize(string gameDirectory)
        {
            lock (StaticSync)
            {
                current = new UiRegistry(gameDirectory);
                current.Load();
                return current;
            }
        }

        UiRegistry(string gameDirectory)
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (String.IsNullOrWhiteSpace(local)) local = String.IsNullOrWhiteSpace(gameDirectory) ? AppDomain.CurrentDomain.BaseDirectory : gameDirectory;
            var overridePath = Environment.GetEnvironmentVariable("SOD2SE_UI_CONFIG");
            ConfigPath = String.IsNullOrWhiteSpace(overridePath)
                ? Path.Combine(local, FrameworkInfo.GameDataFolderName, "SoD2SE", "ui.ini")
                : Path.GetFullPath(overridePath);
            var mcm = McmRegistry.Current;
            if (mcm != null) mcm.LanguageChanged += OnMcmLanguageChanged;
        }

        void OnMcmLanguageChanged(object sender, McmLanguageChangedEventArgs args)
        {
            var handler = LanguageChanged;
            if (handler == null) return;
            try { handler(this, args); }
            catch (Exception error) { Console.Error.WriteLine("[UI] 语言变更回调失败：" + error.Message); }
        }

        public McmLanguage Language
        {
            get
            {
                var mcm = McmRegistry.Current;
                return mcm == null ? McmLanguage.English : mcm.Language;
            }
        }

        public UiSurface RegisterSurface(string id, string title, string subtitle = null, Action<UiSurface> build = null)
        {
            return RegisterSurface(id, new McmLocalizedText(title, title),
                new McmLocalizedText(subtitle ?? "", subtitle ?? ""), build);
        }

        public UiSurface RegisterSurface(string id, McmLocalizedText title, McmLocalizedText subtitle, Action<UiSurface> build = null)
        {
            if (!FrameworkInfo.IsValidPluginId(id)) throw new ArgumentException("UI 界面 ID 无效：" + id, "id");
            if (title == null || (String.IsNullOrWhiteSpace(title.Chinese) && String.IsNullOrWhiteSpace(title.English)))
                throw new ArgumentException("UI 界面标题不能为空。", "title");
            lock (sync)
            {
                if (surfaces.Count >= MaxSurfaces) throw new InvalidOperationException("UI 框架最多注册 " + MaxSurfaces + " 个界面。");
                if (surfaces.Any(item => String.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("UI 界面 ID 重复：" + id);
                var surface = new UiSurface(this, id, title, subtitle ?? new McmLocalizedText("", ""));
                var key = ResolveShortcutLocked(id);
                shortcutKeys[id] = key.Key;
                shortcutModifiers[id] = key.Value;
                surfaces.Add(surface);
                if (build != null) surface.SetBuild(build);
                return surface;
            }
        }

        public int SurfaceShortcutKey(string id)
        {
            lock (sync) { int value; return shortcutKeys.TryGetValue(id ?? "", out value) ? value : 0; }
        }

        // Declares which screen the "show upgrade" key brings forward, so a
        // pending prompt and its progress screen can share one key.
        public void SetChoiceOwner(string surfaceId)
        {
            lock (sync)
            {
                if (surfaceId != null && !surfaces.Any(item => String.Equals(item.Id, surfaceId, StringComparison.OrdinalIgnoreCase)))
                    throw new KeyNotFoundException("UI 界面不存在：" + surfaceId);
                choiceOwner = surfaceId;
            }
        }

        public int ChoiceOwnerIndex()
        {
            lock (sync)
            {
                if (String.IsNullOrWhiteSpace(choiceOwner)) return -1;
                for (int i = 0; i < surfaces.Count; ++i)
                    if (String.Equals(surfaces[i].Id, choiceOwner, StringComparison.OrdinalIgnoreCase)) return i;
                return -1;
            }
        }

        // Asks the host to show the upgrade prompt again after the player
        // dismissed it.  A new value is an edge, so calling this repeatedly is
        // harmless but only the first call has an effect.
        public void RequestChoicePrompt()
        {
            lock (sync) { if (choiceRequest < Int32.MaxValue) choiceRequest++; }
        }

        public int ChoiceRequest { get { lock (sync) return choiceRequest; } }

        public void UnregisterSurface(string id)
        {
            lock (sync)
            {
                var surface = surfaces.FirstOrDefault(item => String.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
                if (surface == null) return;
                surfaces.Remove(surface);
                shortcutKeys.Remove(id);
                shortcutModifiers.Remove(id);
                if (String.Equals(choiceOwner, id, StringComparison.OrdinalIgnoreCase)) choiceOwner = null;
            }
        }

        public int SurfaceShortcutModifiers(string id)
        {
            lock (sync) { int value; return shortcutModifiers.TryGetValue(id ?? "", out value) ? value : 0; }
        }

        public void SetSurfaceShortcut(string id, int key, int modifiers)
        {
            // 0/0 means "this screen has no key of its own"; it is opened by the
            // feature that owns it instead.
            if (!(key == 0 && modifiers == 0) && !McmRegistry.ValidShortcut(key, modifiers))
                throw new ArgumentException("界面快捷键无效：支持 Ctrl/Alt/Shift 加 F1–F12、字母、数字或导航键；保留 Alt+F4 和 Esc。");
            lock (sync)
            {
                if (!surfaces.Any(item => String.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase)))
                    throw new KeyNotFoundException("UI 界面不存在：" + id);
                if (!(key == 0 && modifiers == 0))
                foreach (var pair in shortcutKeys)
                {
                    int existing;
                    if (String.Equals(pair.Key, id, StringComparison.OrdinalIgnoreCase)) continue;
                    if (pair.Value != key) continue;
                    if (shortcutModifiers.TryGetValue(pair.Key, out existing) && existing == modifiers)
                        throw new ArgumentException("界面快捷键已被其他界面占用。");
                }
                var oldKey = SurfaceShortcutKey(id);
                var oldModifiers = SurfaceShortcutModifiers(id);
                shortcutKeys[id] = key;
                shortcutModifiers[id] = modifiers;
                stored["shortcut." + id + ".key"] = key.ToString(CultureInfo.InvariantCulture);
                stored["shortcut." + id + ".modifiers"] = modifiers.ToString(CultureInfo.InvariantCulture);
                try { SaveLocked(); }
                catch
                {
                    shortcutKeys[id] = oldKey;
                    shortcutModifiers[id] = oldModifiers;
                    throw;
                }
            }
        }

        // Called by the bridge after it publishes: the host reports which
        // request revision it acted on and the resulting state.
        public void ApplyHostState(UiSurfaceSnapshot snapshot, int seenRevision, bool open)
        {
            if (snapshot == null || snapshot.Owner == null) return;
            snapshot.Owner.SyncHostState(seenRevision, open);
        }

        // Activates a Button row the player just pressed.  The revision check
        // rejects a click that arrives after the mod rebuilt the screen.
        public bool TryInvokeAction(UiSurfaceSnapshot surface, int nodeIndex, int revision)
        {
            if (surface == null) return false;
            if (revision != surface.Revision) return false;
            if (nodeIndex < 0 || nodeIndex >= surface.Nodes.Count) return false;
            var node = surface.Nodes[nodeIndex];
            if (node.Kind != UiNodeKind.Button || node.Action.Length == 0) return false;
            surface.Owner.DispatchAction(node.Action);
            return true;
        }

        public IList<UiSurfaceSnapshot> Snapshot()
        {
            List<UiSurface> copy;
            lock (sync) copy = surfaces.ToList();
            var result = new List<UiSurfaceSnapshot>(copy.Count);
            int nodeStart = 0;
            foreach (var surface in copy)
            {
                surface.Refresh();
                var nodes = surface.CopyNodes();
                if (nodeStart + nodes.Count > MaxNodes) nodes = nodes.Take(Math.Max(0, MaxNodes - nodeStart)).ToList();
                bool wants, open;
                string error;
                int revision, requestRevision, priority;
                bool modal;
                surface.SnapshotState(out wants, out open, out revision, out requestRevision, out priority, out modal, out error);
                int key, modifiers;
                lock (sync) { key = SurfaceShortcutKey(surface.Id); modifiers = SurfaceShortcutModifiers(surface.Id); }
                result.Add(new UiSurfaceSnapshot(surface, surface.Title, surface.Subtitle, key, modifiers, priority,
                    revision, nodeStart, modal, wants, open, requestRevision, nodes));
                nodeStart += nodes.Count;
            }
            return result;
        }

        // Keeps the same screens across a reload and returns whether anything moved.
        public string Status()
        {
            lock (sync)
            {
                var errors = surfaces.Where(item => !String.IsNullOrEmpty(item.LastError)).ToList();
                if (errors.Count > 0)
                    return String.Join("；", errors.Select(item => item.Id + "：" + item.LastError).ToArray());
                return "";
            }
        }

        KeyValuePair<int, int> ResolveShortcutLocked(string id)
        {
            string rawKey, rawModifiers;
            int savedKey, savedModifiers;
            if (stored.TryGetValue("shortcut." + id + ".key", out rawKey) &&
                stored.TryGetValue("shortcut." + id + ".modifiers", out rawModifiers) &&
                Int32.TryParse(rawKey, NumberStyles.Integer, CultureInfo.InvariantCulture, out savedKey) &&
                Int32.TryParse(rawModifiers, NumberStyles.Integer, CultureInfo.InvariantCulture, out savedModifiers))
            {
                if (savedKey == 0 && savedModifiers == 0) return new KeyValuePair<int, int>(0, 0);
                if (McmRegistry.ValidShortcut(savedKey, savedModifiers))
                    return new KeyValuePair<int, int>(savedKey, savedModifiers);
            }
            var used = new HashSet<int>(shortcutKeys.Values);
            var mcm = McmRegistry.Current;
            if (mcm != null)
            {
                if (mcm.ShortcutModifiers == 0) used.Add(mcm.ShortcutKey);
                if (mcm.ChoiceShortcutModifiers == 0) used.Add(mcm.ChoiceShortcutKey);
            }
            foreach (var candidate in DefaultShortcutKeys)
                if (!used.Contains(candidate)) return new KeyValuePair<int, int>(candidate, 0);
            return new KeyValuePair<int, int>(0, 0);
        }

        void Load()
        {
            try
            {
                if (!File.Exists(ConfigPath)) return;
                foreach (var raw in File.ReadAllLines(ConfigPath, Encoding.UTF8))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                    int split = line.IndexOf('=');
                    if (split <= 0) continue;
                    stored[line.Substring(0, split).Trim()] = line.Substring(split + 1).Trim();
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        void SaveLocked()
        {
            var directory = Path.GetDirectoryName(ConfigPath);
            if (!String.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            var temporary = ConfigPath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                var lines = new List<string> { "# SoD2SE module screen shortcuts; generated file." };
                lines.AddRange(stored.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(item => item.Key + "=" + item.Value));
                File.WriteAllLines(temporary, lines, new UTF8Encoding(false));
                if (File.Exists(ConfigPath)) File.Replace(temporary, ConfigPath, null);
                else File.Move(temporary, ConfigPath);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
