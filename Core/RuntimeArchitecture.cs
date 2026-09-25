using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace SoD2SE
{
    // Stable names are part of the public Core contract.  A version-specific
    // Game API declares which of these capabilities it can provide.
    public static class GameCapabilityIds
    {
        public const string FollowersQuantity = "sod2.followers.quantity";
        public const string CommunityRecruitment = "sod2.community.recruitment";
        public const string MeleeActionClock = "sod2.melee.action-clock";
        public const string MeleeAnimationClock = "sod2.melee.animation-clock";
        public const string McmOverlay = "sod2.mcm.overlay";
        public const string RogueliteKillEvents = "sod2.roguelite.kill-events";
        public const string SurvivorIdentity = "sod2.survivor.identity";
        public const string SurvivorAttributes = "sod2.survivor.attributes";
        public const string SinglePlayerPause = "sod2.session.single-player-pause";
        public const string NativeProgressionUi = "sod2.ui.native-progression";
    }

    public static class GameEventIds
    {
        public const string SessionReady = "game.session.ready";
        public const string SessionStopping = "game.session.stopping";
        public const string CapabilityChanged = "game.capability.changed";
        public const string PropertyChanged = "game.property.changed";
        public const string MeleeActionTick = "melee.action.tick";
        public const string MeleeAnimationTick = "melee.animation.tick";
        public const string RecruitmentGate = "community.recruitment.gate";
        public const string FollowerGate = "followers.quantity.gate";
        public const string SurvivorKill = "survivor.kill";
        public const string SurvivorChanged = "survivor.changed";
        public const string SessionModeChanged = "game.session.mode-changed";
    }

    public sealed class GameTarget
    {
        public string GameId { get; private set; }
        public string BuildId { get; private set; }
        public string ModuleName { get; private set; }
        public string Sha256 { get; private set; }

        public GameTarget(string gameId, string buildId, string moduleName, string sha256)
        {
            if (String.IsNullOrWhiteSpace(gameId) || String.IsNullOrWhiteSpace(buildId) ||
                String.IsNullOrWhiteSpace(moduleName) || !FrameworkInfo.IsSha256(sha256))
                throw new ArgumentException("游戏目标描述不完整。");
            GameId = gameId;
            BuildId = buildId;
            ModuleName = moduleName;
            Sha256 = sha256.ToUpperInvariant();
        }

        public override string ToString()
        {
            return GameId + "/" + BuildId + " (" + ModuleName + ", " + Sha256 + ")";
        }
    }

    public sealed class CapabilitySnapshot
    {
        public string Id { get; private set; }
        public bool Available { get; private set; }
        public string Reason { get; private set; }

        internal CapabilitySnapshot(string id, bool available, string reason)
        {
            Id = id;
            Available = available;
            Reason = reason ?? "";
        }
    }

    public sealed class CapabilitySet
    {
        readonly object sync = new object();
        readonly Dictionary<string, CapabilitySnapshot> values = new Dictionary<string, CapabilitySnapshot>(StringComparer.OrdinalIgnoreCase);

        public void Declare(string id, bool available, string reason)
        {
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentException("能力 ID 不能为空。", "id");
            lock (sync) values[id] = new CapabilitySnapshot(id, available, reason);
        }

        public bool IsAvailable(string id)
        {
            CapabilitySnapshot value;
            lock (sync) return values.TryGetValue(id ?? "", out value) && value.Available;
        }

        public string Reason(string id)
        {
            CapabilitySnapshot value;
            lock (sync) return values.TryGetValue(id ?? "", out value) ? value.Reason : "能力未声明。";
        }

        public IList<CapabilitySnapshot> Snapshot()
        {
            lock (sync) return values.Values.OrderBy(item => item.Id, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public void DisableAll(string reason)
        {
            lock (sync)
            {
                var ids = values.Keys.ToArray();
                foreach (var id in ids) values[id] = new CapabilitySnapshot(id, false, reason);
            }
        }
    }

    // Events carry identifiers and values only.  They deliberately do not
    // expose UObject pointers or process addresses to managed Mod code.
    public struct GameObjectToken : IEquatable<GameObjectToken>
    {
        public ulong Value { get; private set; }
        public uint Generation { get; private set; }
        public string Kind { get; private set; }

        public GameObjectToken(ulong value, uint generation, string kind)
        {
            this = new GameObjectToken();
            Value = value;
            Generation = generation;
            Kind = kind ?? "";
        }

        public bool Equals(GameObjectToken other)
        {
            return Value == other.Value && Generation == other.Generation &&
                String.Equals(Kind, other.Kind, StringComparison.Ordinal);
        }

        public override bool Equals(object obj) { return obj is GameObjectToken && Equals((GameObjectToken)obj); }
        public override int GetHashCode() { return Value.GetHashCode() ^ Generation.GetHashCode() ^ (Kind ?? "").GetHashCode(); }
        public static bool operator ==(GameObjectToken left, GameObjectToken right) { return left.Equals(right); }
        public static bool operator !=(GameObjectToken left, GameObjectToken right) { return !left.Equals(right); }
    }

    public struct GameEvent
    {
        public string Id;
        public string Source;
        public long Sequence;
        public long Timestamp;
        public GameObjectToken Subject;
        public int Code;
        public int Flags;
        public float Value;
        public string Text;

        public GameEvent(string id, string source, long sequence, long timestamp, GameObjectToken subject,
            int code, int flags, float value, string text)
        {
            Id = id ?? "";
            Source = source ?? "";
            Sequence = sequence;
            Timestamp = timestamp;
            Subject = subject;
            Code = code;
            Flags = flags;
            Value = value;
            Text = text ?? "";
        }
    }

    public enum SurvivorKillCategory
    {
        Ordinary = 0,
        Plague = 1,
        Screamer = 2,
        Bloater = 3,
        Feral = 4,
        Juggernaut = 5,
        Unknown = 255
    }

    // This is the additive, typed contract used by the progression Mod.  A
    // native producer must prove all fields before publishing; a missing
    // killer or unknown category is intentionally not converted into XP.
    public struct SurvivorKillEvent
    {
        public long EventId;
        public GameObjectToken Killer;
        public GameObjectToken Victim;
        public string KillerPersistentId;
        public SurvivorKillCategory Category;
        public bool VictimHasPlague;
        public bool ConfirmedPlayerCommunityMember;
        public long Timestamp;

        public SurvivorKillEvent(long eventId, GameObjectToken killer, GameObjectToken victim,
            SurvivorKillCategory category, bool victimHasPlague, bool confirmedPlayerCommunityMember, long timestamp)
            : this(eventId, killer, victim, "", category, victimHasPlague, confirmedPlayerCommunityMember, timestamp)
        {
        }

        public SurvivorKillEvent(long eventId, GameObjectToken killer, GameObjectToken victim, string killerPersistentId,
            SurvivorKillCategory category, bool victimHasPlague, bool confirmedPlayerCommunityMember, long timestamp)
        {
            EventId = eventId;
            Killer = killer;
            Victim = victim;
            KillerPersistentId = killerPersistentId ?? "";
            Category = category;
            VictimHasPlague = victimHasPlague;
            ConfirmedPlayerCommunityMember = confirmedPlayerCommunityMember;
            Timestamp = timestamp;
        }
    }

    public interface ISurvivorKillEventSource
    {
        IDisposable Subscribe(Action<SurvivorKillEvent> handler);
    }

    public sealed class SurvivorContext
    {
        public string PersistentId { get; private set; }
        public string DisplayName { get; private set; }
        public GameObjectToken RuntimeToken { get; private set; }
        public bool IsSinglePlayer { get; private set; }
        public bool IsPlayerCommunityMember { get; private set; }

        public SurvivorContext(string persistentId, GameObjectToken runtimeToken, bool isSinglePlayer, bool isPlayerCommunityMember)
            : this(persistentId, "", runtimeToken, isSinglePlayer, isPlayerCommunityMember)
        {
        }

        public SurvivorContext(string persistentId, string displayName, GameObjectToken runtimeToken,
            bool isSinglePlayer, bool isPlayerCommunityMember)
        {
            PersistentId = persistentId ?? "";
            DisplayName = displayName ?? "";
            RuntimeToken = runtimeToken;
            IsSinglePlayer = isSinglePlayer;
            IsPlayerCommunityMember = isPlayerCommunityMember;
        }
    }

    public interface ISurvivorContextSource
    {
        bool TryGetCurrent(out SurvivorContext context);
        IDisposable Subscribe(Action<SurvivorContext> handler);
    }

    // A game-provided pause lease. The caller owns only its returned lease;
    // releasing it must not resume a pause acquired by another Mod.
    public interface ISinglePlayerPauseSource
    {
        bool TryAcquirePause(string owner, out IDisposable lease, out string reason);
    }

    public interface IGamePauseService
    {
        bool IsAvailable { get; }
        string Reason { get; }
        bool TryAcquire(string owner, out IDisposable lease, out string reason);
    }

    sealed class UnavailablePauseService : IGamePauseService
    {
        readonly string reason;
        public UnavailablePauseService(string reason) { this.reason = reason ?? "游戏暂停能力不可用。"; }
        public bool IsAvailable { get { return false; } }
        public string Reason { get { return reason; } }
        public bool TryAcquire(string owner, out IDisposable lease, out string failure)
        {
            lease = null; failure = reason; return false;
        }
    }

    sealed class SessionPauseService : IGamePauseService
    {
        readonly ISinglePlayerPauseSource source;
        public SessionPauseService(ISinglePlayerPauseSource source) { this.source = source; }
        public bool IsAvailable { get { return source != null; } }
        public string Reason { get { return IsAvailable ? "" : "游戏没有提供暂停租约。"; } }
        public bool TryAcquire(string owner, out IDisposable lease, out string failure)
        {
            lease = null; failure = "";
            if (!FrameworkInfo.IsValidPluginId(owner)) { failure = "暂停租约所有者无效。"; return false; }
            try
            {
                IDisposable inner;
                if (!source.TryAcquirePause(owner, out inner, out failure) || inner == null)
                {
                    if (String.IsNullOrWhiteSpace(failure)) failure = "游戏拒绝了暂停租约。";
                    return false;
                }
                lease = new OwnedPauseLease(owner, inner);
                return true;
            }
            catch (Exception error) { failure = "取得暂停租约失败：" + error.Message; return false; }
        }
    }

    sealed class OwnedPauseLease : IDisposable
    {
        readonly string owner;
        readonly IDisposable inner;
        int disposed;
        public OwnedPauseLease(string owner, IDisposable inner) { this.owner = owner; this.inner = inner; }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            try { inner.Dispose(); }
            catch (Exception error) { Trace.WriteLine("暂停租约释放失败（" + owner + "）：" + error.Message); }
        }
    }

    static class PauseServiceFactory
    {
        public static IGamePauseService For(IGameSession session)
        {
            var source = session as ISinglePlayerPauseSource;
            return source == null
                ? (IGamePauseService)new UnavailablePauseService("游戏会话没有提供已验证的单人暂停接口。")
                : new SessionPauseService(source);
        }
    }

    public interface IGameEventBus
    {
        IDisposable Subscribe(string id, Action<GameEvent> handler);
        void Publish(GameEvent value);
    }

    sealed class EventSubscription : IDisposable
    {
        readonly Action dispose;
        int disposed;
        public EventSubscription(Action dispose) { this.dispose = dispose; }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0 && dispose != null) dispose();
        }
    }

    public sealed class GameEventBus : IGameEventBus
    {
        readonly object sync = new object();
        Dictionary<string, Action<GameEvent>[]> subscribers = new Dictionary<string, Action<GameEvent>[]>(StringComparer.OrdinalIgnoreCase);
        long sequence;

        public IDisposable Subscribe(string id, Action<GameEvent> handler)
        {
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentException("事件 ID 不能为空。", "id");
            if (handler == null) throw new ArgumentNullException("handler");
            lock (sync)
            {
                var next = CloneSubscribers();
                Action<GameEvent>[] existing;
                if (!next.TryGetValue(id, out existing)) existing = new Action<GameEvent>[0];
                next[id] = existing.Concat(new[] { handler }).ToArray();
                subscribers = next;
            }
            return new EventSubscription(delegate { Unsubscribe(id, handler); });
        }

        public void Publish(GameEvent value)
        {
            value.Sequence = Interlocked.Increment(ref sequence);
            if (value.Timestamp == 0) value.Timestamp = Environment.TickCount;
            var current = Interlocked.CompareExchange(ref subscribers, null, null);
            Action<GameEvent>[] handlers;
            if (current == null || !current.TryGetValue(value.Id ?? "", out handlers)) return;
            foreach (var handler in handlers)
            {
                try { handler(value); }
                catch { /* A Mod must not break the game event path. */ }
            }
        }

        void Unsubscribe(string id, Action<GameEvent> handler)
        {
            lock (sync)
            {
                var next = CloneSubscribers();
                Action<GameEvent>[] existing;
                if (!next.TryGetValue(id, out existing)) return;
                next[id] = existing.Where(item => item != handler).ToArray();
                if (next[id].Length == 0) next.Remove(id);
                subscribers = next;
            }
        }

        Dictionary<string, Action<GameEvent>[]> CloneSubscribers()
        {
            return subscribers.ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
        }
    }

    public enum PropertyModifierMode
    {
        Override,
        Multiply,
        Add,
        Clamp
    }

    public sealed class PropertyModifier
    {
        public string Id { get; private set; }
        public string Owner { get; private set; }
        public string Scope { get; private set; }
        public PropertyModifierMode Mode { get; private set; }
        public int Priority { get; private set; }
        public float Value { get; internal set; }
        public float Maximum { get; internal set; }
        public bool Enabled { get; internal set; }

        internal PropertyModifier(string id, string owner, string scope, PropertyModifierMode mode, int priority, float value, float maximum, bool enabled)
        {
            Id = id;
            Owner = owner;
            Scope = scope ?? "";
            Mode = mode;
            Priority = priority;
            Value = value;
            Maximum = maximum;
            Enabled = enabled;
        }
    }

    public sealed class PropertyModifierLease : IDisposable
    {
        readonly PropertyModifierStack owner;
        readonly string key;
        readonly string modifierId;
        int disposed;

        internal PropertyModifierLease(PropertyModifierStack owner, string key, string modifierId)
        {
            this.owner = owner;
            this.key = key;
            this.modifierId = modifierId;
        }

        public void Update(float value, bool enabled)
        {
            if (Volatile.Read(ref disposed) != 0) throw new ObjectDisposedException("PropertyModifierLease");
            owner.Update(key, modifierId, value, enabled);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) owner.Remove(key, modifierId);
        }
    }

    // Modifiers are evaluated when configuration changes and published as a
    // compact snapshot.  Native/game hot paths never lock this stack.
    public sealed class PropertyModifierStack
    {
        readonly object sync = new object();
        readonly Dictionary<string, List<PropertyModifier>> values = new Dictionary<string, List<PropertyModifier>>(StringComparer.OrdinalIgnoreCase);

        public PropertyModifierLease Register(string key, string owner, PropertyModifierMode mode, int priority, float value, float maximum, bool enabled)
        {
            return Register(key, owner, "", mode, priority, value, maximum, enabled);
        }

        public PropertyModifierLease Register(string key, string owner, string scope, PropertyModifierMode mode, int priority, float value, float maximum, bool enabled)
        {
            if (String.IsNullOrWhiteSpace(key) || String.IsNullOrWhiteSpace(owner)) throw new ArgumentException("属性修改器描述不完整。");
            var modifierId = owner + ":" + Guid.NewGuid().ToString("N");
            lock (sync)
            {
                List<PropertyModifier> list;
                if (!values.TryGetValue(key, out list)) values[key] = list = new List<PropertyModifier>();
                list.Add(new PropertyModifier(modifierId, owner, scope, mode, priority, value, maximum, enabled));
            }
            return new PropertyModifierLease(this, key, modifierId);
        }

        internal void Update(string key, string modifierId, float value, bool enabled)
        {
            lock (sync)
            {
                List<PropertyModifier> list;
                if (!values.TryGetValue(key, out list)) throw new InvalidOperationException("属性修改器已经移除：" + key);
                var modifier = list.SingleOrDefault(item => item.Id == modifierId);
                if (modifier == null) throw new InvalidOperationException("属性修改器已经移除：" + key);
                modifier.Value = value;
                modifier.Enabled = enabled;
            }
        }

        internal void Remove(string key, string modifierId)
        {
            lock (sync)
            {
                List<PropertyModifier> list;
                if (!values.TryGetValue(key, out list)) return;
                list.RemoveAll(item => item.Id == modifierId);
                if (list.Count == 0) values.Remove(key);
            }
        }

        public float Evaluate(string key, float baseline)
        {
            lock (sync) return EvaluateLocked(key, baseline);
        }

        public float Evaluate(string key, string scope, float baseline)
        {
            lock (sync) return EvaluateLocked(key, baseline, scope ?? "");
        }

        public IDictionary<string, float> Snapshot(IDictionary<string, float> baselines)
        {
            return Snapshot(null, baselines);
        }

        public IDictionary<string, float> Snapshot(string scope, IDictionary<string, float> baselines)
        {
            if (baselines == null) throw new ArgumentNullException("baselines");
            var result = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            lock (sync)
                foreach (var item in baselines) result[item.Key] = EvaluateLocked(item.Key, item.Value, scope);
            return result;
        }

        float EvaluateLocked(string key, float baseline)
        {
            return EvaluateLocked(key, baseline, null);
        }

        float EvaluateLocked(string key, float baseline, string scope)
        {
            List<PropertyModifier> list;
            if (!values.TryGetValue(key ?? "", out list)) return baseline;
            var result = baseline;
            foreach (var modifier in list.Where(item => item.Enabled &&
                (item.Scope.Length == 0 || (scope != null && String.Equals(item.Scope, scope, StringComparison.OrdinalIgnoreCase))))
                .OrderBy(item => item.Priority).ThenBy(item => item.Id, StringComparer.Ordinal))
            {
                switch (modifier.Mode)
                {
                    case PropertyModifierMode.Override: result = modifier.Value; break;
                    case PropertyModifierMode.Multiply: result *= modifier.Value; break;
                    case PropertyModifierMode.Add: result += modifier.Value; break;
                    case PropertyModifierMode.Clamp:
                        result = Math.Max(modifier.Value, Math.Min(modifier.Maximum, result));
                        break;
                }
            }
            return result;
        }
    }

    public enum HookKind
    {
        BytePatch,
        NativeModule
    }

    public sealed class HookRequest
    {
        public HookKind Kind { get; private set; }
        public string Owner { get; private set; }
        public string Capability { get; private set; }
        public string ExpectedSha256 { get; private set; }
        public IList<PatchSpec> Patches { get; private set; }
        public string Library { get; private set; }
        public string EntryPoint { get; private set; }
        public string Argument { get; private set; }

        HookRequest() { }

        public static HookRequest BytePatch(string owner, string capability, string expectedSha256, IList<PatchSpec> patches)
        {
            ValidateOwnerAndCapability(owner, capability);
            if (!FrameworkInfo.IsSha256(expectedSha256)) throw new ArgumentException("Hook 目标 SHA256 无效。", "expectedSha256");
            if (patches == null || patches.Count == 0) throw new ArgumentException("Hook 补丁不能为空。", "patches");
            foreach (var patch in patches) if (patch == null) throw new ArgumentException("Hook 补丁列表包含空项目。", "patches");
            return new HookRequest {
                Kind = HookKind.BytePatch, Owner = owner, Capability = capability,
                ExpectedSha256 = expectedSha256, Patches = new List<PatchSpec>(patches)
            };
        }

        public static HookRequest NativeModule(string owner, string capability, string library, string entryPoint, string argument)
        {
            ValidateOwnerAndCapability(owner, capability);
            if (String.IsNullOrWhiteSpace(library)) throw new ArgumentException("原生 Hook 库路径不能为空。", "library");
            if (String.IsNullOrWhiteSpace(entryPoint)) throw new ArgumentException("原生 Hook 入口不能为空。", "entryPoint");
            if (String.IsNullOrWhiteSpace(argument)) throw new ArgumentException("原生 Hook 参数不能为空。", "argument");
            return new HookRequest {
                Kind = HookKind.NativeModule, Owner = owner, Capability = capability,
                Library = library, EntryPoint = entryPoint, Argument = argument
            };
        }

        static void ValidateOwnerAndCapability(string owner, string capability)
        {
            if (!FrameworkInfo.IsValidPluginId(owner)) throw new ArgumentException("Hook owner 无效。", "owner");
            if (String.IsNullOrWhiteSpace(capability)) throw new ArgumentException("Hook capability 不能为空。", "capability");
        }
    }

    public interface IHookBroker
    {
        HookLease Acquire(HookRequest request);
        int ActiveCount { get; }
    }

    public sealed class HookLease : IDisposable
    {
        readonly Action release;
        int disposed;
        public string Owner { get; private set; }
        public string Capability { get; private set; }
        public int ChangedBytes { get; private set; }
        public HookKind Kind { get; private set; }

        internal HookLease(string owner, string capability, HookKind kind, int changedBytes, Action release)
        {
            Owner = owner;
            Capability = capability;
            Kind = kind;
            ChangedBytes = changedBytes;
            this.release = release;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0 && release != null) release();
        }
    }

    sealed class HookBroker : IHookBroker
    {
        readonly IGameSession session;
        readonly IGameVersionApi api;
        readonly object sync = new object();
        readonly Dictionary<string, HookLease> active = new Dictionary<string, HookLease>(StringComparer.OrdinalIgnoreCase);

        public HookBroker(IGameSession session, IGameVersionApi api)
        {
            if (session == null) throw new ArgumentNullException("session");
            if (api == null) throw new ArgumentNullException("api");
            this.session = session;
            this.api = api;
        }
        public int ActiveCount { get { lock (sync) return active.Count; } }

        public HookLease Acquire(HookRequest request)
        {
            if (request == null) throw new ArgumentNullException("request");
            if (!FrameworkInfo.IsValidPluginId(request.Owner)) throw new ArgumentException("Hook owner 无效。", "request");
            if (String.IsNullOrWhiteSpace(request.Capability)) throw new ArgumentException("Hook capability 不能为空。", "request");
            // The compatibility GenericGameApi intentionally permits inert test
            // adapters.  Every fixed-version API must pass through its declared
            // capability gate before any process write or native load.
            if (!(api is GenericGameApi) && !api.Capabilities.IsAvailable(request.Capability))
                throw new InvalidOperationException("游戏版本 API 未提供能力 " + request.Capability + "：" + api.Capabilities.Reason(request.Capability));
            var key = request.Owner + ":" + request.Capability;
            lock (sync)
            {
                if (active.ContainsKey(key)) throw new InvalidOperationException("Hook 已由同一所有者注册：" + key);
                int changed = 0;
                bool bytePatchApplied = false;
                try
                {
                    if (request.Kind == HookKind.BytePatch)
                    {
                        changed = session.Apply(request.Owner, request.ExpectedSha256, request.Patches);
                        bytePatchApplied = true;
                    }
                    else
                    {
                        if (session.GameProcess == null || session.GameProcess.HasExited)
                            throw new InvalidOperationException("原生 Hook 需要正在运行的游戏进程。");
                        NativePluginModule.LoadAndStart(session.GameProcess, request.Library, request.EntryPoint, request.Argument);
                    }
                }
                catch
                {
                    if (bytePatchApplied)
                    {
                        try { session.Restore(request.Owner, request.Patches); }
                        catch (Exception restoreError) { Trace.WriteLine("Hook Broker 失败回滚异常：" + restoreError.Message); }
                    }
                    throw;
                }
                var lease = new HookLease(request.Owner, request.Capability, request.Kind, changed,
                    delegate { Release(key, request); });
                active.Add(key, lease);
                return lease;
            }
        }

        void Release(string key, HookRequest request)
        {
            lock (sync)
            {
                if (!active.Remove(key)) return;
                if (request.Kind == HookKind.BytePatch) session.Restore(request.Owner, request.Patches);
                // Native modules stop through their owner channel.  The broker
                // deliberately never unloads a live DLL from a game thread.
            }
        }
    }

    public interface IGameVersionApi
    {
        string Id { get; }
        GameTarget Target { get; }
        CapabilitySet Capabilities { get; }
        IList<PatchSpec> GetPatches(string capability);
        void Detect(IGameSession session);
    }

    public sealed class GenericGameApi : IGameVersionApi
    {
        readonly CapabilitySet capabilities = new CapabilitySet();
        readonly GameTarget target = new GameTarget("unknown", "unknown", "unknown.exe", FrameworkInfo.TargetSha256);

        public GenericGameApi() { capabilities.Declare("core.runtime", true, "通用核心服务可用。"); }
        public string Id { get { return "generic"; } }
        public GameTarget Target { get { return target; } }
        public CapabilitySet Capabilities { get { return capabilities; } }
        public IList<PatchSpec> GetPatches(string capability) { return new List<PatchSpec>(); }
        public void Detect(IGameSession session) { }
    }

    public sealed class GameRuntime
    {
        public IGameSession Session { get; private set; }
        public IGameVersionApi Api { get; private set; }
        public IHookBroker Hooks { get; private set; }
        public IGameEventBus Events { get; private set; }
        public PropertyModifierStack Properties { get; private set; }
        public IGamePauseService Pause { get; private set; }

        internal GameRuntime(IGameSession session, IGameVersionApi api, IHookBroker hooks, IGameEventBus events,
            PropertyModifierStack properties, IGamePauseService pause)
        {
            Session = session;
            Api = api;
            Hooks = hooks;
            Events = events;
            Properties = properties;
            Pause = pause ?? new UnavailablePauseService("游戏会话没有提供暂停服务。");
        }
    }

    public interface IRuntimeGameSession
    {
        GameRuntime Runtime { get; }
    }

    public static class GameRuntimeAccess
    {
        public static GameRuntime For(IGameSession session)
        {
            if (session == null) throw new ArgumentNullException("session");
            var runtimeSession = session as IRuntimeGameSession;
            if (runtimeSession != null) return runtimeSession.Runtime;
            // Legacy test adapters and third-party loaders still implement only
            // IGameSession.  They receive the same broker/event/property API and
            // therefore migrate without retaining process or object pointers.
            var api = new GenericGameApi();
            return new GameRuntime(session, api, new HookBroker(session, api), new GameEventBus(),
                new PropertyModifierStack(), PauseServiceFactory.For(session));
        }
    }
}
