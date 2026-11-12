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
        public const string Version = "1.4.5";

        internal static ManualLogSource Log;
        internal static Plugin Instance;

        internal static ConfigEntry<string> UrlA;
        internal static ConfigEntry<string> UrlB;
        internal static ConfigEntry<string> UrlSlow;
        internal static ConfigEntry<float> SeekSeconds;
        internal static ConfigEntry<float> Volume;
        internal static ConfigEntry<int> TimeoutSeconds;
        internal static ConfigEntry<int> QualityLevel;
        internal static ConfigEntry<int> MaxLosslessSeconds;
        internal static ConfigEntry<bool> RepeatOne;
        internal static ConfigEntry<bool> LyricColorFromStyle;
        internal static ConfigEntry<int> LyricWarp;
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
        internal static ConfigEntry<float> LyricBrightness;
        internal static ConfigEntry<bool> LyricWiggle;
        internal static ConfigEntry<int> LyricWiggleFps;
        internal static ConfigEntry<float> LyricWiggleAmount;
        internal static ConfigEntry<int> LyricEnterAnim;
        internal static ConfigEntry<int> LyricExitAnim;
        internal static ConfigEntry<float> LyricRestAlpha;
        internal static ConfigEntry<int> LyricWeight;
        internal static ConfigEntry<float> LyricCharSpacing;
        internal static ConfigEntry<float> LyricSizeScale;
        internal static ConfigEntry<float> LyricBreath;
        internal static ConfigEntry<float> LyricDrift;
        internal static ConfigEntry<string> LyricStyleName;
        internal static ConfigEntry<int> LyricMotion;
        internal static ConfigEntry<float> LyricGlowPulse;
        internal static ConfigEntry<bool> SendToFolia;
        internal static ConfigEntry<int> LyricLayout;
        internal static ConfigEntry<int> LyricFxMode;
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

            // Core 是下层程序集，拿不到 Logger，用回调把日志接回来。
            // 取流规格、无损降级原因都要靠这条线才能进 BepInEx 日志。
            PeakMusic.Core.PluginHooks.LogInfo = delegate(string m) { try { Logger.LogInfo("[取流] " + m); } catch { } };
            PeakMusic.Core.PluginHooks.LogWarn = delegate(string m) { try { Logger.LogWarning("[取流] " + m); } catch { } };

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

            // ── 音质 ──
            // 0 自动：先拿 320k 保底，能升无损就升；非会员升不上去也不会掉到 128k。
            // 1–3 是「主动降档」，4/5 需要对应平台会员。
            QualityLevel = Config.Bind("音质", "QualityLevel", 0,
                new ConfigDescription("音质档位：0=自动（推荐）1=标准128k 2=较高192k 3=极高320k "
                                    + "4=无损FLAC 5=Hi-Res。4/5 需要对应平台会员，没有会员会自动退回 320k，"
                                    + "不会出现「点了没声」。无损要先整曲下载再本地解码，起播会慢几秒。",
                    new AcceptableValueRange<int>(0, 5)));

            MaxLosslessSeconds = Config.Bind("音质", "MaxLosslessSeconds", 900,
                new ConfigDescription("无损单曲时长上限（秒）。超过就跳过无损改播 320k —— "
                                    + "一首 15 分钟的无损在内存里是几百 MB，低配机会直接卡死。0=不限。",
                    new AcceptableValueRange<int>(0, 3600)));

            // ── 播放 ──
            RepeatOne = Config.Bind("播放", "RepeatOne", false,
                "单曲循环：开着时一首播完从头再放，不会切下一首。"
                + "面板底栏的「↻ 单曲」按钮改的就是这一项，改完立刻生效并自动存盘。");

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

            LyricBrightness = Config.Bind("歌词", "Brightness", 1.70f,
                new ConfigDescription("歌词亮度。1=原始颜色，越大越亮（颜色会往白里过渡）。"
                                    + "默认 1.7 是实测比较抓眼、又不到刺眼的值；"
                                    + "嫌刺眼往 1.2 调，想更炸可以到 2.0。",
                    new AcceptableValueRange<float>(0.8f, 2.0f)));

            LyricWiggle = Config.Bind("歌词", "Wiggle", true,
                "逐字轻微抖动（字会小幅漂浮，原项目默认开）。关掉更稳。");

            LyricWiggleFps = Config.Bind("歌词", "WiggleFps", 8,
                new ConfigDescription("抖动频率 Hz（4–24）。越大越急，8 接近原项目默认。",
                    new AcceptableValueRange<int>(4, 24)));

            LyricWiggleAmount = Config.Bind("歌词", "WiggleAmount", 0.8f,
                new ConfigDescription("抖动幅度（0–2）。0 = 不抖，0.8 接近原项目默认。",
                    new AcceptableValueRange<float>(0f, 2f)));

            LyricEnterAnim = Config.Bind("歌词", "EnterAnim", 1,
                new ConfigDescription("入场动画：0=淡入 1=上浮 2=弹入（带回弹）3=侧滑入",
                    new AcceptableValueRange<int>(0, 3)));

            LyricExitAnim = Config.Bind("歌词", "ExitAnim", 1,
                new ConfigDescription("退场动画：0=淡出 1=上飘 2=缩小 3=下坠",
                    new AcceptableValueRange<int>(0, 3)));

            // ── 以下是「样式」的其余维度。面板点一次样式会把这些一起写掉，
            //    手改任意一项也能立刻看到效果（不需要重启）。 ──

            LyricRestAlpha = Config.Bind("歌词", "RestAlpha", .58f,
                new ConfigDescription("未唱字的透明度（0.2–1）。越小，未唱部分越隐，逐字浮现的感觉越强；"
                                    + "越大，整句越完整地摆在画面里。",
                    new AcceptableValueRange<float>(.2f, 1f)));

            LyricWeight = Config.Bind("歌词", "Weight", 0,
                new ConfigDescription("字重：0=常规 1=加粗（SDF 加厚，远处更好认）",
                    new AcceptableValueRange<int>(0, 1)));

            LyricCharSpacing = Config.Bind("歌词", "CharSpacing", 2f,
                new ConfigDescription("字距。0=紧凑，8~12 会拉出「气声/呼吸」那种疏朗感（中文建议不超过 12）。",
                    new AcceptableValueRange<float>(-6f, 16f)));

            LyricSizeScale = Config.Bind("歌词", "SizeScale", 1f,
                new ConfigDescription("字号倍率（相对 [歌词] Size）。每套样式可以略有大小差，形成节奏。",
                    new AcceptableValueRange<float>(.8f, 1.25f)));

            LyricBreath = Config.Bind("歌词", "Breath", .01f,
                new ConfigDescription("呼吸幅度（0–0.08）：整句缓慢缩放起伏，像跟着气息走。0=完全不动。",
                    new AcceptableValueRange<float>(0f, .08f)));

            LyricDrift = Config.Bind("歌词", "Drift", .01f,
                new ConfigDescription("漂移幅度（0–0.06）：整句沿屏幕横向缓慢游走，别调太大，会飘出画面。",
                    new AcceptableValueRange<float>(0f, .06f)));

            LyricMotion = Config.Bind("歌词", "Motion", 1,
                new ConfigDescription("整句运动方式：0=静止 1=呼吸(缩放) 2=漂浮(上下) 3=摇摆(左右+微倾) "
                                    + "4=心跳(双拍脉冲) 5=潮汐(慢上下+缓倾)。每套样式自带一种，这就是「同样的词、不一样的动」。",
                    new AcceptableValueRange<int>(0, 5)));

            LyricWarp = Config.Bind("歌词", "Warp", 0,
                new ConfigDescription("逐字排布（参考开源播放器的文字 PV 样式）：0=直排 1=弧排（时计） "
                                    + "2=阶梯（云阶） 3=散聚（心象，未唱时散开、唱到归位） 4=斜行（倾诉） 5=波浪。"
                                    + "只改每个字的位置/角度，不影响逐字点亮。",
                    new AcceptableValueRange<int>(0, 5)));

            LyricColorFromStyle = Config.Bind("歌词", "ColorFromStyle", true,
                "样式是否自带配色。true = 点样式卡连颜色一起换；"
                + "false = 颜色由你在面板「配色」里单独选，点样式卡只换动效（运动/入场退场/字距/抖动）。");

            LyricFxMode = Config.Bind("歌词", "Fx", 0,
                new ConfigDescription("歌词特效层：0=关 1=星尘 2=花火 3=绘光 4=时计 5=回环 6=心象 7=脉冲 8=波纹 9=极光。"
                                    + "10=流星。纯程序化粒子，不依赖任何外部渲染器；换句/逐字都会触发。",
                    new AcceptableValueRange<int>(0, 10)));

            LyricLayout = Config.Bind("歌词", "Layout", 0,
                new ConfigDescription("歌词排版：0=两侧悬浮（默认，世界空间、随视角转）；"
                                    + "1=居中滚动（屏幕中央一行一句往上滚，当前句最亮，上一句渐暗上移）。"
                                    + "居中模式更接近播放器歌词，两侧模式更不挡视野。",
                    new AcceptableValueRange<int>(0, 1)));

            SendToFolia = Config.Bind("歌词", "SendToFolia", false,
                "把当前歌词与播放进度推给 Folia 的 Stage API（需要 Folia 已启动、并在它自己的设置里打开 Stage 模式）。"
                + "打开后 Folia 那边会跟着我们的播放器走；关掉不影响我们自己的歌词。");

            LyricGlowPulse = Config.Bind("歌词", "GlowPulse", .6f,
                new ConfigDescription("逐字光晕脉冲（0–1.5）：每个字刚唱到时亮度冲高一次再回落。"
                                    + "0=字只是由暗变亮；1 以上会「闪」得比较明显，接近舞台灯牌那种观感。",
                    new AcceptableValueRange<float>(0f, 1.5f)));

            LyricStyleName = Config.Bind("歌词", "StyleName", "暖白·常显",
                "当前样式名（面板「样式」按钮自动维护，只用于显示。手改颜色后这里不会跟着变，属正常）。");

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
                new ConfigDescription("面板缩放（0.85–1.8）。面板角落能拖，Ctrl+滚轮也能调。"
                                    + "低于 1 时字号会小到看不清，不建议长期用。",
                    new AcceptableValueRange<float>(0.85f, 1.8f)));

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

            // 一次性修正：游戏启动时窗口还是 304×201 的加载窗，插件在这个尺寸下算出的缩放
            // 会被写回 cfg（实测变成 0.7），之后无论屏幕多大，面板都只有 0.7 倍 —— 字全糊。
            // 这里把「明显是被压到底」的值刷回 1.0。
            bool scaleFix = !Config.ContainsKey(new ConfigDefinition("内部", "ScaleFixMigrated"));
            if (scaleFix && PanelScale.Value < 0.9f) PanelScale.Value = 1f;
            Config.Bind("内部", "ScaleFixMigrated", true,
                "内部标记：2026-11-10 修正过被加载窗口写坏的 PanelScale。别改。");

            // 2026-11-11：歌词「样式」系统重做（10 套，每套自带运动方式 / 字距 / 字重 / 呼吸）。
            // 老 cfg 只有三个颜色键，没有 Motion / RestAlpha 这些新键，直接跑会落在「自定义」上、
            // 颜色和动画对不上。用一次性标记把默认落回 0 号样式（暖白·常显）。
            bool styleMigrate = !Config.ContainsKey(new ConfigDefinition("内部", "StylesV2Migrated"));
            if (styleMigrate) LyricColors.Apply(0);
            Config.Bind("内部", "StylesV2Migrated", true,
                "内部标记：2026-11-11 歌词样式系统重做后同步过一次默认样式。别改。");

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
        internal const string BuildStamp = "2026-11-12 / v1.4.5（19 套样式 · 无损换栈 · 下一首修复）";

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
