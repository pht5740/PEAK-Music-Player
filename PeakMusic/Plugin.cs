using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace PeakMusic
{
    /// <summary>
    /// PEAK 音乐 Mod 的插件入口。
    ///   阶段 1（探针）已过闸门，AudioProbe 留着做回归；
    ///   阶段 3（面板 + 播放器）从这里挂载：F1 开关面板，搜索网易云并播放。
    /// </summary>
    [BepInPlugin(Guid, "PeakMusic", Version)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.peakmusic.probe";
        public const string Version = "1.0.0";

        internal static ManualLogSource Log;
        internal static Plugin Instance;

        internal static ConfigEntry<string> UrlA;
        internal static ConfigEntry<string> UrlB;
        internal static ConfigEntry<string> UrlSlow;
        internal static ConfigEntry<float> SeekSeconds;
        internal static ConfigEntry<float> Volume;
        internal static ConfigEntry<int> TimeoutSeconds;
        internal static ConfigEntry<int> Theme;

        /// <summary>当前音源平台序号（对应 <c>Platforms.All</c> 的下标）。</summary>
        internal static ConfigEntry<int> SourcePlatform;

        internal static ConfigEntry<bool> LyricEnabled;
        internal static ConfigEntry<int> LyricSize;
        internal static ConfigEntry<float> LyricY;
        internal static ConfigEntry<bool> LyricTranslation;
        internal static ConfigEntry<int> LyricSide;
        internal static ConfigEntry<int> LyricLines;
        internal static ConfigEntry<float> LyricDistance;
        internal static ConfigEntry<float> LyricEdge;
        internal static ConfigEntry<string> LyricKey;

        // ── 歌词配色：全部是十六进制字符串，方便手改 ──
        internal static ConfigEntry<string> LyricColorLit;
        internal static ConfigEntry<string> LyricColorRest;
        internal static ConfigEntry<string> LyricColorSub;
        internal static ConfigEntry<float> LyricRestDim;
        internal static ConfigEntry<int> LyricColorPreset;
        internal static ConfigEntry<int> LyricStyle;
        internal static ConfigEntry<bool> LyricSplit;
        internal static ConfigEntry<bool> LyricGlow;
        internal static ConfigEntry<float> LyricGlowStrength;
        internal static ConfigEntry<bool> LyricFilterMeta;

        /// <summary>阶段 1 探针（F5–F11）。默认关：它有自己的 AudioSource，会和面板播放器同时出声。</summary>
        internal static ConfigEntry<bool> EnableProbe;

        // ── 面板窗口：热键 / 位置 / 缩放 / 折叠 ──
        // 面板是自绘 IMGUI 窗口，位置和大小都由自己管；这些项就是它的"窗口几何"。
        internal static ConfigEntry<string> PanelKey;
        internal static ConfigEntry<float> PanelX;
        internal static ConfigEntry<float> PanelY;
        internal static ConfigEntry<float> PanelScale;
        internal static ConfigEntry<bool> PanelCollapsed;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            // 默认走本机 HTTP：internet 到国外测试站只有 27KB/s（实测），8.7MB 要 323 秒，
            // 拿它当闸门素材永远过不了。本机 HTTP 走的是同一套 UnityWebRequest 网络栈，
            // 除了"带宽"以外每一行代码都验到。
            UrlA = Config.Bind("探针", "UrlA",
                "http://127.0.0.1:8765/test-song.mp3",
                "主测试音频直链。默认指向本机 HTTP 服务（见 README / notes.md 的起服务命令）");

            UrlB = Config.Bind("探针", "UrlB",
                "http://127.0.0.1:8765/test-song.mp3?v=2",
                "第二首，用于切歌压测。本机到国外站约 27KB/s，别指公网慢站，压测会跑成几百秒");

            UrlSlow = Config.Bind("探针", "UrlSlow",
                "http://127.0.0.1:8766/test-song.mp3",
                "限速链路（serve.py 8766 512 → 512KB/s）。F5 专用：本机 125MB/s 时'秒开'和'整曲下完再播'表现一样，测不出 streamAudio 有没有真生效");

            SeekSeconds = Config.Bind("探针", "SeekSeconds", 60f,
                new ConfigDescription("seek 目标秒数", new AcceptableValueRange<float>(0f, 3600f)));

            Volume = Config.Bind("探针", "Volume", 0.8f,
                new ConfigDescription("探针音量（0–1），用来验证和游戏原声不打架", new AcceptableValueRange<float>(0f, 1f)));

            TimeoutSeconds = Config.Bind("探针", "TimeoutSeconds", 0,
                "UnityWebRequest 超时（秒）。0 = 不限。之前设 25 秒，在 27KB/s 的链路上必然失败，这个坑记着");

            Theme = Config.Bind("界面", "Theme", 0,
                new ConfigDescription("面板配色：0=丝绒玫瑰 1=午夜石墨 2=极光海 3=曜石红（面板右上角按钮也能切）",
                    new AcceptableValueRange<int>(0, Palettes.Count - 1)));

            Volume.Value = Mathf.Clamp01(Volume.Value);

            SourcePlatform = Config.Bind("音源", "Platform", 0,
                new ConfigDescription("当前音源：0=网易云 1=QQ音乐（面板左侧导航也能切）",
                    new AcceptableValueRange<int>(0, 1)));

            // 老版本里 [歌词] Enabled 可能被 F4 关掉、并且被存进了 cfg。用户要的是
            // "只要在放音乐就该有歌词"，所以升级到这一版时强制打开一次。
            // 用 [内部] 段的一个标记位判断"是不是第一次跑这一版"，只迁一次，之后用户
            // 自己按 F4 关掉仍然会被记住。
            bool upgradeToAutoLyric = !Config.ContainsKey(new ConfigDefinition("内部", "AutoLyricMigrated"));

            LyricEnabled = Config.Bind("歌词", "Enabled", true,
                "歌词总开关（F4 也能开关）。默认开：只要在放音乐，屏幕两侧就会自动出现 3D 歌词，" +
                "不需要按任何键。");

            if (upgradeToAutoLyric) LyricEnabled.Value = true;

            LyricSize = Config.Bind("歌词", "Size", 84,
                new ConfigDescription("字号 = 每个字在屏幕上的高度（像素，按 1080p 记；2K/4K 会自动同比例放大）。" +
                    "40–80 是舒服区间，长句会按屏宽自动收一点",
                    new AcceptableValueRange<int>(16, 140)));

            LyricDistance = Config.Bind("歌词", "Distance", 12f,
                new ConfigDescription("文字平面离镜头多远（格）。越小越贴脸、透视感越强；越大越远越平。",
                    new AcceptableValueRange<float>(2f, 12f)));

            LyricEdge = Config.Bind("歌词", "Edge", 0.40f,
                new ConfigDescription("句子中心离屏幕边缘的远近：0.40 = 左边那句的中心在屏幕 40% 处、" +
                    "右边那句在 60% 处。0.18 更贴边，0.35 更靠中间（面板左侧「左右」行也能点）",
                    new AcceptableValueRange<float>(0.12f, 0.40f)));

            LyricKey = Config.Bind("歌词", "Hotkey", "F4",
                "临时关/开歌词的热键。留空 = 不要热键（歌词一直跟着音乐走）。" +
                "注意别和 [界面] PanelKey 写成同一个键 —— 撞了的话这个热键会自动失效，" +
                "用面板左侧的「歌词」行开关即可。");

            LyricY = Config.Bind("歌词", "Y", 0.62f,
                new ConfigDescription("句子中心在屏幕上的高度，0=屏幕顶 1=屏幕底。0.62 略偏下（面板左侧「高度」行也能点）",
                    new AcceptableValueRange<float>(0.1f, 0.9f)));

            LyricTranslation = Config.Bind("歌词", "Translation", true,
                "有翻译歌词时，在当前行下面显示一行小字");

            LyricSide = Config.Bind("歌词", "Side", 0,
                new ConfigDescription("3D 歌词往哪边散开：0=两侧交替 1=只左侧 2=只右侧（面板导航底部点一下也能切）",
                    new AcceptableValueRange<int>(0, 2)));

            LyricLines = Config.Bind("歌词", "Lines", 5,
                new ConfigDescription("同屏最多几句 3D 歌词（含正在上飘消失的旧句，建议 3–5）",
                    new AcceptableValueRange<int>(3, 7)));

            // ── 歌词配色 ──
            // 三项都是十六进制：6 位 = RGB，8 位 = RGBA（第四段是透明度）。
            // 已唱/未唱分开是为了做卡拉 OK 效果：点亮的部分一个色、没唱的部分另一个色。
            LyricColorLit = Config.Bind("歌词", "ColorLit", "A6F07A",
                "已唱部分的字色，十六进制（如 FF6B6B）。8 位可以连透明度一起定，例如 FF6B6BCC。");

            LyricColorRest = Config.Bind("歌词", "ColorRest", "A6F07A",
                "还没唱到的部分字色，十六进制。默认和已唱同色，只靠 RestDim 压暗；" +
                "想让未唱部分是另一种颜色（比如已唱金色、未唱灰蓝 8FA6C4）就改这里。");

            LyricColorSub = Config.Bind("歌词", "ColorSub", "DCF3CB",
                "翻译副行字色，十六进制。");

            LyricRestDim = Config.Bind("歌词", "RestDim", 0.44f,
                new ConfigDescription("未唱部分的暗度：1=和已唱一样亮，0.44=默认的「压暗待点亮」效果",
                    new AcceptableValueRange<float>(0.05f, 1f)));

            LyricColorPreset = Config.Bind("歌词", "ColorPreset", 5,
                "上次选中的配色预设（面板左侧点「歌词颜色」循环切换时自动写这里）。" +
                "-1 表示你手改了上面三个颜色，让面板显示「自定义」。");

            // ── 动效（对齐 MuoniumPlayer 的「动效风格」）──
            LyricSplit = Config.Bind("歌词", "Split", true,
                "按词切块：一句歌词拆成几个词，各自飘在画面上不同位置（参考视频里 Not / to / me 那种）。" +
                "关掉就恢复成整句一块。中文没有空格，会按标点切；整句没标点就是一块。");

            LyricStyle = Config.Bind("歌词", "Style", 0,
                new ConfigDescription("动效风格：0=逐词弹出（每块按它自己的时间弹进来）1=整行同进同出（静态）",
                    new AcceptableValueRange<int>(0, 1)));

            LyricGlow = Config.Bind("歌词", "Glow", true,
                "字发光。TextMesh 没有后处理 bloom，这里是用两层放大的半透明副本模拟的柔光。");

            LyricGlowStrength = Config.Bind("歌词", "GlowStrength", 0.6f,
                new ConfigDescription("发光强度（0=看不见 1=很强）", new AcceptableValueRange<float>(0f, 1f)));

            LyricFilterMeta = Config.Bind("歌词", "FilterMeta", true,
                "跳过歌词里的制作信息行（【本音乐作品已获得正版授权】、词：xxx、曲：xxx、" +
                "编曲/混音/制作人/出品 这类）。开着更像播放器的歌词，关掉则一字不漏地显示。");

            EnableProbe = Config.Bind("探针", "EnableProbe", false,
                "是否挂载阶段1探针（F5–F11）。默认关：探针有自己的 AudioSource，" +
                "开着按 F7 会和面板播放器同时出声，而且它左上角那排调试文字会一直压在画面上。");

            PanelKey = Config.Bind("界面", "PanelKey", "F1",
                "开关面板的热键。F1 被别的 mod 占用时改这里，比如 F8 / P / Insert。" +
                "写多个用逗号分隔（F1,F2）。留空则退回默认 F1/F2。");

            PanelX = Config.Bind("界面", "PanelX", -1f,
                "面板左上角 X（拖动后自动记下来）。-1 = 每次打开都居中");

            PanelY = Config.Bind("界面", "PanelY", -1f,
                "面板左上角 Y（拖动后自动记下来）。-1 = 每次打开都居中");

            PanelScale = Config.Bind("界面", "PanelScale", 1f,
                new ConfigDescription("面板缩放（0.7–1.5）。面板角落能拖，Ctrl+滚轮也能调",
                    new AcceptableValueRange<float>(0.7f, 1.5f)));

            PanelCollapsed = Config.Bind("界面", "PanelCollapsed", false,
                "打开时是否折叠成一条标题栏（只留曲名和按钮）");

            // 一次性迁移用的标记位，写在 [内部] 段；用户不用管，也别删（删了下次启动会
            // 把歌词重新强制打开一次，行为上不致命但会覆盖你按 F4 关掉的选择）。
            Config.Bind("内部", "AutoLyricMigrated", true,
                "内部标记：本次升级已把 [歌词] Enabled 强制打开过一次。别改。");

            // r38：默认歌词配置一次性同步（定稿：字号84 / 高度0.62 / 左右0.40 / 距离12 / 薄荷绿）。
            // BepInEx 不会用新默认值覆盖已有 cfg，所以老安装（比如朋友那台已经生成过配置的）
            // 用这个一次性标记刷成定稿这套；同步完之后用户自己改的任何值都会被正常记住。
            bool upgradeToV2Defaults = !Config.ContainsKey(new ConfigDefinition("内部", "DefaultsV2Migrated"));
            if (upgradeToV2Defaults)
            {
                LyricEnabled.Value = true;
                LyricSize.Value = 84;
                LyricY.Value = 0.62f;
                LyricEdge.Value = 0.40f;
                LyricDistance.Value = 12f;
                LyricColorLit.Value = "A6F07A";
                LyricColorRest.Value = "A6F07A";
                LyricColorSub.Value = "DCF3CB";
                LyricColorPreset.Value = 5;
            }
            Config.Bind("内部", "DefaultsV2Migrated", true,
                "内部标记：2026-11-08 定稿默认歌词配置已同步过一次。别改。");

            SourcePlatform.Value = Mathf.Clamp(SourcePlatform.Value, 0, 1);
            LyricSide.Value = Mathf.Clamp(LyricSide.Value, 0, 2);
            if ((LyricLines.Value & 1) == 0) LyricLines.Value += 1;   // 偶数行没有"中间行"，强制奇数

            var root = new GameObject("PeakMusic.Runtime");
            DontDestroyOnLoad(root);

            if (EnableProbe.Value)
            {
                root.AddComponent<AudioProbe>();
                Log.LogInfo("[PeakMusic] 阶段1 探针已挂载（EnableProbe=true）：F7 播放 / F8 seek / F9 暂停 / " +
                            "F10 压测 / F11 停止 / F6 收面板 / F5 慢链验证。注意它有独立 AudioSource。");
            }

            root.AddComponent<MusicSync>();       // 「一起听」联机同步（先于面板，面板 Start 要订它的事件）
            root.AddComponent<MusicPanel>();      // 面板 + 播放器（MusicPanel.Awake 里挂 MusicPlayer）
            root.AddComponent<LyricOverlay>();    // 漂浮歌词

            Log.LogInfo("[PeakMusic] ===== 构建 " + BuildStamp + " =====");
            Log.LogInfo("[PeakMusic] 面板已挂载：F1 / F2 开关，输入歌名 → 搜索 → 点一行播放。");
            Log.LogInfo("[PeakMusic] 歌词：放歌就自动出现，世界空间 3D（左右两侧、逐字动画）。字号 " + LyricSize.Value + "px、" +
                        "距离 " + LyricDistance.Value.ToString("0") + " 格、高度 " + LyricY.Value.ToString("0.00") +
                        "、左右 " + (LyricEdge.Value * 100f).ToString("0") + "%" +
                        "，配色=" + LyricColors.CurrentName());
            Log.LogInfo("[PeakMusic] 注意：改了 cfg 文件必须重启游戏才生效；面板左侧那几行（歌词/字号/高度/颜色）" +
                        "是点了立刻生效的。");
            if (upgradeToAutoLyric) Log.LogInfo("[PeakMusic] 升级迁移：歌词已自动打开（之前被 F4 关掉过）。");
            Log.LogInfo("[PeakMusic] 热键：面板=" + (string.IsNullOrWhiteSpace(PanelKey.Value) ? "F1/F2" : PanelKey.Value) +
                        "，歌词开关=" + (string.IsNullOrWhiteSpace(LyricKey.Value) ? "(无)" : LyricKey.Value));
        }

        /// <summary>
        /// 构建标记。改代码后手动往上推一位 —— 排查时第一件事就是确认游戏里跑的
        /// 到底是哪一版（BepInEx 只在启动时加载 DLL，很容易「改了但没生效」）。
        /// </summary>
        // r42 警告：这行常量只许短！它会被整行写进日志，单行超约 2000 字符会打爆
        // BepInEx 5 控制台编码（UnityInjector.ConsoleEncoding.ReadByteBuffer 越界），
        // 且会毒化后续所有插件的日志 → PRZ 加载失败 → Fatal 进不了游戏。
        // 历史根因记录放 CHANGELOG.md / 记忆文件，别再堆回这里。
        internal const string BuildStamp = "2026-11-09 r44 / v1.0.1（取流修复：Cookie 头手动带）";

        internal static string SideName(int side)
        {
            return side == 1 ? "左侧" : side == 2 ? "右侧" : "两侧交替";
        }

        private void OnApplicationQuit()
        {
            Log.LogInfo("[PeakMusic] 退出中——如果这条之后日志还在刷，说明有东西没清干净。");
        }
    }
}
