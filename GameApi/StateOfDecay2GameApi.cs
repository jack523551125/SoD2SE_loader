using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using SoD2SE;

namespace SoD2SE.GameApi
{
    // This assembly is the only code layer that knows Update 38.2 RVAs,
    // guards, object offsets, and native hook entry points.  Core contains no
    // State of Decay 2 addresses and can be reused by another game/version.
    public sealed class NativeHookSpec
    {
        readonly byte[] guard;
        public string Id { get; private set; }
        public int Rva { get; private set; }
        public byte[] Guard { get { return (byte[])guard.Clone(); } }

        public NativeHookSpec(string id, int rva, byte[] guard)
        {
            if (String.IsNullOrWhiteSpace(id) || rva < 0 || guard == null || guard.Length == 0)
                throw new ArgumentException("原生 Hook 描述不完整。");
            Id = id;
            Rva = rva;
            this.guard = (byte[])guard.Clone();
        }
    }

    public static class StateOfDecay2Capabilities
    {
        public const string Followers = GameCapabilityIds.FollowersQuantity;
        public const string Community = GameCapabilityIds.CommunityRecruitment;
        public const string MeleeAction = GameCapabilityIds.MeleeActionClock;
        public const string MeleeAnimation = GameCapabilityIds.MeleeAnimationClock;
        public const string Mcm = GameCapabilityIds.McmOverlay;
        public const string RogueliteKillEvents = GameCapabilityIds.RogueliteKillEvents;
        public const string SurvivorIdentity = GameCapabilityIds.SurvivorIdentity;
        public const string SurvivorAttributes = GameCapabilityIds.SurvivorAttributes;
        public const string SinglePlayerPause = GameCapabilityIds.SinglePlayerPause;
        public const string NativeProgressionUi = GameCapabilityIds.NativeProgressionUi;
    }

    public sealed class StateOfDecay2GameApi : IGameVersionApi
    {
        public const string BuildId = "16535856";
        // The file name and hash of the fixed target are defined once in Core:
        // FrameworkInfo names the executable, and TargetSha256 is the hash the
        // root patch manifests are checked against.
        public const string TargetSha256 = FrameworkInfo.TargetSha256;
        public const string ShippingModule = FrameworkInfo.GameExecutableName;

        readonly GameTarget target = new GameTarget(FrameworkInfo.GameDataFolderName, BuildId, ShippingModule, TargetSha256);
        readonly CapabilitySet capabilities = new CapabilitySet();
        readonly Dictionary<string, IList<PatchSpec>> patches;
        readonly Dictionary<string, NativeHookSpec> nativeHooks;

        public StateOfDecay2GameApi()
        {
            capabilities.Declare(StateOfDecay2Capabilities.Followers, false, "尚未验证目标程序。");
            capabilities.Declare(StateOfDecay2Capabilities.Community, false, "尚未验证目标程序。");
            capabilities.Declare(StateOfDecay2Capabilities.MeleeAction, false, "尚未验证目标程序。");
            capabilities.Declare(StateOfDecay2Capabilities.MeleeAnimation, false, "尚未验证目标程序。");
            capabilities.Declare(StateOfDecay2Capabilities.Mcm, false, "尚未验证目标程序。");
            capabilities.Declare(StateOfDecay2Capabilities.RogueliteKillEvents, false, "尚未验证 Update 38.2 的击杀归属和敌人分类入口；成长 Mod 保持停用。");
            capabilities.Declare(StateOfDecay2Capabilities.SurvivorIdentity, false, "尚未验证可跨社区、遗产池持久化的幸存者标识；不会使用名字或地址猜测。");
            capabilities.Declare(StateOfDecay2Capabilities.SurvivorAttributes, false, "尚未验证角色属性计算入口；不会写入未经确认的角色偏移。");
            capabilities.Declare(StateOfDecay2Capabilities.SinglePlayerPause, false, "尚未验证单人暂停接口；选择界面不能用线程挂起替代。");
            capabilities.Declare(StateOfDecay2Capabilities.NativeProgressionUi, false, "尚未验证把成长信息接入原版角色/社区 UI 的方法；当前自绘界面只是开发原型。");
            patches = BuildPatches();
            nativeHooks = new Dictionary<string, NativeHookSpec>(StringComparer.OrdinalIgnoreCase) {
                { StateOfDecay2Capabilities.MeleeAction, new NativeHookSpec("melee.action-tick", 0x341320, Hex("40555357488D6C24D04881EC30010000")) },
                { StateOfDecay2Capabilities.MeleeAnimation, new NativeHookSpec("melee.animation-tick", 0x1C88F50, Hex("40564883EC304883B97806000000")) }
            };
        }

        public string Id { get { return "sod2.update38.2"; } }
        public GameTarget Target { get { return target; } }
        public CapabilitySet Capabilities { get { return capabilities; } }
        public NativeHookSpec GetNativeHook(string capability)
        {
            NativeHookSpec value;
            return nativeHooks.TryGetValue(capability ?? "", out value) ? value : null;
        }

        public IList<PatchSpec> GetPatches(string capability)
        {
            IList<PatchSpec> value;
            return patches.TryGetValue(capability ?? "", out value) ? value : new List<PatchSpec>();
        }

        public void Detect(IGameSession session)
        {
            if (session == null) throw new ArgumentNullException("session");
            string actual;
            try { actual = FrameworkInfo.HashFile(session.ExePath); }
            catch (Exception error)
            {
                capabilities.DisableAll("无法读取游戏主程序：" + error.Message);
                return;
            }
            bool hashMatch = String.Equals(actual, TargetSha256, StringComparison.OrdinalIgnoreCase);
            bool moduleMatch = String.Equals(Path.GetFileName(session.ExePath), ShippingModule, StringComparison.OrdinalIgnoreCase);
            string reason = hashMatch && moduleMatch ? "Update 38.2 / Build 16535856 已验证。" :
                "目标不是已登记的 Update 38.2 主程序；实际 SHA256=" + actual + "。";
            capabilities.Declare(StateOfDecay2Capabilities.Followers, hashMatch && moduleMatch, reason);
            capabilities.Declare(StateOfDecay2Capabilities.Community, hashMatch && moduleMatch, reason);
            capabilities.Declare(StateOfDecay2Capabilities.MeleeAction, hashMatch && moduleMatch, reason);
            capabilities.Declare(StateOfDecay2Capabilities.MeleeAnimation, hashMatch && moduleMatch, reason);
            capabilities.Declare(StateOfDecay2Capabilities.Mcm, hashMatch && moduleMatch, reason);
            capabilities.Declare(StateOfDecay2Capabilities.RogueliteKillEvents, false, hashMatch && moduleMatch ? "版本已识别，但击杀归属和敌人分类仍未完成静态验证。" : reason);
            capabilities.Declare(StateOfDecay2Capabilities.SurvivorIdentity, false, hashMatch && moduleMatch ? "版本已识别，但跨社区持久身份仍未完成静态验证。" : reason);
            capabilities.Declare(StateOfDecay2Capabilities.SurvivorAttributes, false, hashMatch && moduleMatch ? "版本已识别，但角色属性入口仍未完成静态验证。" : reason);
            capabilities.Declare(StateOfDecay2Capabilities.SinglePlayerPause, false, hashMatch && moduleMatch ? "版本已识别，但单人暂停入口仍未完成静态验证。" : reason);
            capabilities.Declare(StateOfDecay2Capabilities.NativeProgressionUi, false, hashMatch && moduleMatch ? "版本已识别，但原版 CharacterUI/CommunityUI 的扩展和输入接入仍未验证。" : reason);
        }

        public static StateOfDecay2GameApi Create() { return new StateOfDecay2GameApi(); }

        // Compatibility for inert test adapters that implement IGameSession
        // without the runtime service interface.  The descriptors still have a
        // single owner here; production plugins always obtain them through Api.
        public static IList<PatchSpec> GetCompatibilityPatches(string capability)
        {
            return new StateOfDecay2GameApi().GetPatches(capability);
        }

        static Dictionary<string, IList<PatchSpec>> BuildPatches()
        {
            var result = new Dictionary<string, IList<PatchSpec>>(StringComparer.OrdinalIgnoreCase) {
                { StateOfDecay2Capabilities.Followers, new ReadOnlyCollection<PatchSpec>(new List<PatchSpec> {
                    new PatchSpec(
                        "Dialogue follower quantity gate",
                        0x22D7B9,
                        Hex("74"),
                        Hex("EB"),
                        0x22D7B4,
                        Hex("807C243100743A488D95A8070000C603004C8D05045EFE02488D8C24B8000000E857832600488BD0488D8B88000000E888ADE600488BBC24C0000000E94EFEFFFF0FB644243084C00F857CFEFFFF498BCEE866")
                    ),
                    new PatchSpec(
                        "Community screen follower quantity gate",
                        0x39595E,
                        Hex("40B702"),
                        Hex("EB2C90"),
                        0x395936,
                        Hex("85FF7E524863C7488BCB488D14C3483BDA74150F1F800000000048393174324883C108483BCA75F240B7024885DB7408488BCBE8A25ED100400FB6C7488B7C2440488B5C2450488B6C24584883C4305EC340B701EBD54885DB7408488BCBE8775ED10080BD60010000037224488BD6488BCDE8133DFFFF0F2F85380C00007210488BCDE8B2D2FEFF84C07504B003EBAC32C0EBA8488B")
                    )
                }) },
                { StateOfDecay2Capabilities.Community, new ReadOnlyCollection<PatchSpec>(new List<PatchSpec> {
                    new PatchSpec(
                        "CanAddCharacter",
                        0x276360,
                        Hex("40534883EC20F681F80A0000020FB6DA740832C04883C4205BC3E8D16D000084DBB909000000BA0C0000000F45CA2BC883F9010F9DC04883C4205BC3"),
                        Hex("40534883EC208B81F80A0000A8027524A808751C88D3E8D56D000033C984DB0F95C98D4C49092BC883F9010F9DC0EB06B001EB0232C04883C4205BC3"),
                        0x276360,
                        Hex("40534883EC20F681F80A0000020FB6DA740832C04883C4205BC3E8D16D000084DBB909000000BA0C0000000F45CA2BC883F9010F9DC04883C4205BC3")
                    ),
                    new PatchSpec(
                        "CanAddCharacters",
                        0x2763A0,
                        Hex("48895C2408574883EC20F681F80A000002410FB6D88BFA740D32C0488B5C24304883C4205FC3E8856D000084DBB909000000488B5C2430BA0C0000000F45CA2BC83BF90F9EC04883C4205FC3"),
                        Hex("48895C2408574883EC208BFA8B81F80A0000A8027524A808751C4488C3E88E6D000033C984DB0F95C98D4C49092BC83BF90F9EC0EB0BB001EB0732C09090909090488B5C24304883C4205FC3"),
                        0x2763A0,
                        Hex("48895C2408574883EC20F681F80A000002410FB6D88BFA740D32C0488B5C24304883C4205FC3E8856D000084DBB909000000488B5C2430BA0C0000000F45CA2BC83BF90F9EC04883C4205FC3")
                    ),
                    new PatchSpec(
                        "TryAddCharacter population gate",
                        0x290665,
                        Hex("84DBB90C00000041BA09000000440F45D1442BD04183FA017C6B"),
                        Hex("F687F80A000008751184DB750783F8097C08EB7183F80C7D6C90"),
                        0x290645,
                        Hex("F681F80A000002450FB6F1410FB6D8488BEA488BF90F858A000000E8EBCAFEFF84DBB90C00000041BA09000000440F45D1442BD04183FA017C6B48897424304863B7A00300008D46018987A00300003B87A40300007E0E8BD6488D8F98030000E846D41A01")
                    ),
                    new PatchSpec(
                        "TryAddCharacterRecord population gate",
                        0x29073B,
                        Hex("84DBB90C00000041B909000000440F45C9442BC84183F9017C71"),
                        Hex("F686F80A000008751184DB750783F8097C08EB7783F80C7D7290"),
                        0x29071F,
                        Hex("F681F80A000002410FB6D8488BFA488BF10F8590000000E815CAFEFF84DBB90C00000041B909000000440F45C9442BC84183F9017C7180BFA100000000756848896C24304863AE600200008D45018986600200003B86640200007E0E8BD5488D8E58020000E8E797FFFF")
                    ),
                    new PatchSpec(
                        "Inlined recruitment hard-cap gate",
                        0x2797CD,
                        Hex("F687F80A0000020F8589000000488BCFE86E390000B90C0000002BC883F9017C75"),
                        Hex("8A87F80A0000A8020F8588000000A808750F488BCFE86939000083F80C7D779090"),
                        0x2797C6,
                        Hex("498BBD70110000F687F80A0000020F8589000000488BCFE86E390000B90C0000002BC883F9017C7540387531756F4863B7600200008D46018987600200003B87640200007E0E8BD6488D8F58020000E856070100")
                    ),
                    new PatchSpec(
                        "Restore exiled character population gate",
                        0x290F71,
                        Hex("41B80C000000442BC04183F8010F8CC3000000"),
                        Hex("F687F80A000008750A83F80C0F8DC400000090"),
                        0x290F5A,
                        Hex("F681F80A0000028BF2488BF90F85DB000000E8DFC1FEFF41B80C000000442BC04183F8010F8CC3000000486387800200004869C858020000")
                    ),
                    new PatchSpec(
                        "Transfer character population gate",
                        0x291A06,
                        Hex("498BC8E842B7FEFF80BC248000000000B909000000BA0C0000000F45CA2BC883F9010F8C07020000"),
                        Hex("F687F80A000008751F488BCFE839B7FEFF80BC24800000000119C98D4C490C3BC10F8D0802000090"),
                        0x2919EF,
                        Hex("4D85C00F843D02000041F680F80A0000020F852F020000498BC8E842B7FEFF80BC248000000000B909000000BA0C0000000F45CA2BC883F9010F8C07020000488B44246880B808040000000F85F5010000")
                    ),
                    new PatchSpec(
                        "Player capacity query leaf helper",
                        0x27B432,
                        Hex("CCCCCCCCCCCCCCCCCCCCCCCCCCCC"),
                        Hex("F681F80A00000875DAE9101D0000"),
                        0x27B431,
                        Hex("C3CCCCCCCCCCCCCCCCCCCCCCCCCCCC")
                    ),
                    new PatchSpec(
                        "Player capacity query zero result",
                        0x27B415,
                        Hex("CCCCCC"),
                        Hex("31C0C3"),
                        0x27B414,
                        Hex("C3CCCCCCCCCCCCCCCCCCCCCC")
                    ),
                    new PatchSpec(
                        "Remaining recruitment availability",
                        0x27ADC9,
                        Hex("E882230000"),
                        Hex("E864060000"),
                        0x27ADC0,
                        Hex("40534883EC200FB6DAE88223000084DBB909000000BA0C0000000F45CA2BC88BC14883C4205BC3")
                    ),
                    new PatchSpec(
                        "Population soft-cap query",
                        0x281544,
                        Hex("E807BCFFFF"),
                        Hex("E8E99EFFFF"),
                        0x281540,
                        Hex("4883EC28E807BCFFFF83F8090F9FC04883C428C3")
                    )
                }) }
            };
            return result;
        }

        static byte[] Hex(string value)
        {
            var compact = (value ?? "").Replace(" ", "").Replace("\r", "").Replace("\n", "");
            if ((compact.Length & 1) != 0) throw new FormatException("十六进制长度错误。");
            var bytes = new byte[compact.Length / 2];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(compact.Substring(i * 2, 2), 16);
            return bytes;
        }
    }
}
