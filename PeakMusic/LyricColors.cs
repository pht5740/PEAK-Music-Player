using BepInEx.Configuration;
using UnityEngine;

namespace PeakMusic
{
    /// <summary>
    /// 歌词样式表。
    ///
    /// 这里不是「换个颜色」，而是一整套可描述的观感：
    ///   配色（已唱/未唱/翻译三种） + 未唱常显度 + 光晕 + 亮度
    ///   + 字重/字距/字号倍率 + 呼吸/整句漂移 + 入场/退场方式
    ///
    /// 设计取向和常见的整行淡入式实现不同：
    ///   · 常见做法是「未唱的字不显示，唱到才弹出来」；
    ///   · 这里是「整句先以暗色常显，唱到哪个字哪个字往亮色过渡」，
    ///     所以画面里不会出现半句空着的情况，边爬边看更舒服。
    /// 两边底层参数（字色、光晕、抖动）方向一致，但呈现出来的不是同一个东西。
    ///
    /// 颜色全是十六进制字符串，写进 cfg 之后用户还能自己改，改完下一帧生效。
    /// </summary>
    internal static class LyricColors
    {
        /// <summary>一套完整样式。字段用对象初始化器对齐着写，方便加新维度。</summary>
        internal sealed class Preset
        {
            internal string Name = "未命名";
            internal string Lit = "FFFFFF";        // 已唱（点亮）
            internal string Rest = "808080";       // 未唱（常显的暗色）
            internal string Sub = "E0E0E0";        // 翻译副行

            internal float RestAlpha = .55f;       // 未唱字的透明度（0.2–1）
            internal float Glow;                   // 光晕强度 0–1
            internal float Bright = 1.5f;          // 亮度倍率 1–2

            internal int Weight;                   // 0 常规 1 加粗（SDF 加厚）
            internal float Spacing;                // 字距（TMP characterSpacing）
            internal float SizeScale = 1f;         // 字号倍率

            internal float Wiggle = .5f;           // 抖动幅度 0–2
            internal int WiggleFps = 8;            // 抖动频率 Hz
            internal float Breath = .01f;          // 呼吸幅度（缩放起伏 0–0.08）
            internal float Drift;                  // 位移幅度（漂浮/摇摆/潮汐共用 0–0.06）
            internal int Motion = 1;               // 0静止 1呼吸 2漂浮 3摇摆 4心跳 5潮汐
            internal int Warp;                     // 0直排 1弧排 2阶梯 3散聚 4斜行 5波浪
            internal float GlowPulse = .6f;        // 逐字光晕脉冲 0–1.5（字唱到时亮度冲高一次）

            internal int Enter = 1;                // 0 淡入 1 上浮 2 弹入 3 侧滑
            internal int Exit = 1;                 // 0 淡出 1 上飘 2 缩小 3 下坠
        }

        /// <summary>
        /// 10 套样式。前两套日常够用，中间四套拉开观感差异，后四套是风格化表达。
        /// 默认 0 号，和旧版暖白观感衔接得上。
        /// </summary>
        internal static readonly Preset[] Presets =
        {
            new Preset
            {
                Name = "暖白·常显", Lit = "F7E1D7", Rest = "8A7A70", Sub = "FFEEDF",
                RestAlpha = .58f, Glow = .90f, Bright = 1.70f,
                Weight = 0, Spacing = 2f, SizeScale = 1f,
                Wiggle = .8f, WiggleFps = 8, Breath = .010f, Drift = .010f,
                GlowPulse = 0.60f, Motion = 1, Enter = 1, Exit = 1,
            },
            new Preset
            {
                Name = "薄荷·点亮", Lit = "A6F07A", Rest = "3F5A34", Sub = "DCF3CB",
                RestAlpha = .46f, Glow = .55f, Bright = 1.55f,
                Weight = 0, Spacing = 3f, SizeScale = 1f,
                Wiggle = .4f, WiggleFps = 7, Breath = .008f, Drift = .008f,
                GlowPulse = 0.50f, Motion = 0, Enter = 0, Exit = 0,
            },
            new Preset
            {
                Name = "霓虹·脉冲", Lit = "C9A6FF", Rest = "4B3A63", Sub = "E5D8FF",
                RestAlpha = .40f, Glow = 1f, Bright = 1.85f,
                Weight = 1, Spacing = 6f, SizeScale = 1.02f,
                Wiggle = 1.3f, WiggleFps = 12, Breath = .022f, Drift = .014f,
                GlowPulse = 1.20f, Motion = 4, Enter = 2, Exit = 2,
            },
            new Preset
            {
                Name = "琥珀·暖阳", Lit = "FFC166", Rest = "6B4A24", Sub = "FFE5B0",
                RestAlpha = .55f, Glow = .85f, Bright = 1.70f,
                Weight = 1, Spacing = 1f, SizeScale = 1f,
                Wiggle = .5f, WiggleFps = 9, Breath = .012f, Drift = .020f,
                GlowPulse = 0.70f, Motion = 2, Enter = 1, Exit = 3,
            },
            new Preset
            {
                Name = "冰蓝·呼吸", Lit = "A8D8FF", Rest = "35506B", Sub = "DAEAFF",
                RestAlpha = .62f, Glow = .35f, Bright = 1.42f,
                Weight = 0, Spacing = 8f, SizeScale = .98f,
                Wiggle = .15f, WiggleFps = 6, Breath = .026f, Drift = .006f,
                GlowPulse = 0.35f, Motion = 1, Enter = 0, Exit = 0,
            },
            new Preset
            {
                Name = "粉樱·轻拂", Lit = "FFB8D8", Rest = "6E4A58", Sub = "F8DCE8",
                RestAlpha = .50f, Glow = .80f, Bright = 1.60f,
                Weight = 0, Spacing = 4f, SizeScale = 1f,
                Wiggle = .6f, WiggleFps = 8, Breath = .016f, Drift = .022f,
                GlowPulse = 0.55f, Motion = 3, Enter = 3, Exit = 2,
            },
            new Preset
            {
                Name = "纯白·锐利", Lit = "FFFFFF", Rest = "4A4A55", Sub = "D8D8E4",
                RestAlpha = .34f, Glow = 0f, Bright = 1.40f,
                Weight = 1, Spacing = 0f, SizeScale = 1.02f,
                Wiggle = 0f, WiggleFps = 8, Breath = 0f, Drift = 0f,
                GlowPulse = 0.20f, Motion = 0, Enter = 2, Exit = 0,
            },
            new Preset
            {
                Name = "赤霞·热烈", Lit = "FF8A6B", Rest = "5E2A22", Sub = "FFC4AE",
                RestAlpha = .44f, Glow = 1f, Bright = 1.80f,
                Weight = 1, Spacing = 2f, SizeScale = 1.03f,
                Wiggle = 1f, WiggleFps = 11, Breath = .014f, Drift = .018f,
                GlowPulse = 1.10f, Motion = 4, Enter = 2, Exit = 3,
            },
            new Preset
            {
                Name = "星尘·漂浮", Lit = "E8D9FF", Rest = "4A4468", Sub = "D6CDFF",
                RestAlpha = .42f, Glow = .70f, Bright = 1.65f,
                Weight = 0, Spacing = 10f, SizeScale = .97f,
                Wiggle = 1.1f, WiggleFps = 10, Breath = .028f, Drift = .045f,
                GlowPulse = 0.80f, Motion = 2, Enter = 3, Exit = 1,
            },
            new Preset
            {
                Name = "墨玉·静水", Lit = "C8D6C0", Rest = "2E3A33", Sub = "AFC2AC",
                RestAlpha = .70f, Glow = .25f, Bright = 1.30f,
                Weight = 0, Spacing = 5f, SizeScale = .96f,
                Wiggle = .1f, WiggleFps = 5, Breath = .006f, Drift = .004f,
                GlowPulse = 0.10f, Motion = 5, Enter = 0, Exit = 2,
            },
            new Preset
            {
                Name = "白描·流光", Lit = "FFFFFF", Rest = "9AA0AE", Sub = "E6EAF2",
                RestAlpha = .30f, Glow = 1f, Bright = 1.85f,
                Weight = 1, Spacing = 1f, SizeScale = 1.02f,
                Wiggle = .5f, WiggleFps = 9, Breath = .006f, Drift = .004f,
                GlowPulse = 1.20f, Motion = 1, Enter = 0, Exit = 0,
            },
            new Preset
            {
                Name = "衬线·书页", Lit = "F5EBD8", Rest = "8A8172", Sub = "E8DECB",
                RestAlpha = .62f, Glow = .35f, Bright = 1.45f,
                Weight = 0, Spacing = 7f, SizeScale = .97f,
                Wiggle = .15f, WiggleFps = 6, Breath = .010f, Drift = .006f,
                GlowPulse = .15f, Motion = 0, Enter = 0, Exit = 0,
            },
            new Preset
            {
                Name = "等宽·字幕", Lit = "EDF1F6", Rest = "727A88", Sub = "C9D0DA",
                RestAlpha = .70f, Glow = .15f, Bright = 1.32f,
                Weight = 0, Spacing = 3f, SizeScale = .96f,
                Wiggle = 0f, WiggleFps = 8, Breath = 0f, Drift = 0f,
                GlowPulse = 0f, Motion = 0, Enter = 0, Exit = 0,
            },
            new Preset
            {
                Name = "霓彩·涌光", Lit = "8EF0FF", Rest = "35596B", Sub = "CFF4FF",
                RestAlpha = .38f, Glow = 1f, Bright = 1.95f,
                Weight = 1, Spacing = 2f, SizeScale = 1.03f,
                Wiggle = 1.1f, WiggleFps = 12, Breath = .020f, Drift = .012f,
                GlowPulse = 1.45f, Motion = 4, Enter = 2, Exit = 2,
            },

            // ── 以下是「逐字排布」类样式：参考开源播放器的文字 PV 构图，用顶点位移复刻 ──

            // 时计：整行沿一段圆弧排开，像钟面刻度，逐字角度顺着弧线偏
            new Preset
            {
                Name = "时计·弧光", Lit = "BFD8F0", Rest = "44566B", Sub = "DCE9F7",
                RestAlpha = .45f, Glow = .75f, Bright = 1.70f,
                Weight = 1, Spacing = 3f, SizeScale = 1.01f,
                Wiggle = .25f, WiggleFps = 6, Breath = .008f, Drift = .004f,
                GlowPulse = .70f, Motion = 5, Warp = 1, Enter = 0, Exit = 0,
            },

            // 云阶：每两个字一组、上下错落，排成往下的阶梯
            new Preset
            {
                Name = "云阶·错落", Lit = "F3E3C8", Rest = "6E6250", Sub = "EDE2D0",
                RestAlpha = .55f, Glow = .55f, Bright = 1.62f,
                Weight = 0, Spacing = 4f, SizeScale = .99f,
                Wiggle = .3f, WiggleFps = 7, Breath = .010f, Drift = .006f,
                GlowPulse = .55f, Motion = 0, Warp = 2, Enter = 3, Exit = 2,
            },

            // 心象：未唱的字散开漂浮，唱到哪个字哪个字归位
            new Preset
            {
                Name = "心象·散聚", Lit = "DCEBFF", Rest = "3E4A5C", Sub = "E8F1FF",
                RestAlpha = .38f, Glow = .85f, Bright = 1.80f,
                Weight = 1, Spacing = 1f, SizeScale = 1.02f,
                Wiggle = .9f, WiggleFps = 11, Breath = .012f, Drift = .008f,
                GlowPulse = 1.10f, Motion = 4, Warp = 3, Enter = 2, Exit = 2,
            },

            // 倾诉：整行向右下斜着排，像手写；配侧滑入场
            new Preset
            {
                Name = "倾诉·斜行", Lit = "FFD9A8", Rest = "6B5844", Sub = "FFE8CC",
                RestAlpha = .52f, Glow = .65f, Bright = 1.68f,
                Weight = 0, Spacing = 5f, SizeScale = 1f,
                Wiggle = .5f, WiggleFps = 8, Breath = .014f, Drift = .010f,
                GlowPulse = .60f, Motion = 3, Warp = 4, Enter = 3, Exit = 3,
            },

            // 波浪：每个字按正弦上下起伏，波峰随拍右移（自创，用来补足「律动」这类）
            new Preset
            {
                Name = "潮涌·波浪", Lit = "A6F0C8", Rest = "3F5B4C", Sub = "D6F7E4",
                RestAlpha = .42f, Glow = .80f, Bright = 1.75f,
                Weight = 1, Spacing = 2f, SizeScale = 1.02f,
                Wiggle = .6f, WiggleFps = 9, Breath = .012f, Drift = .006f,
                GlowPulse = .85f, Motion = 2, Warp = 5, Enter = 1, Exit = 1,
            },
        };

        // ── 缓存：只有 cfg 里的字符串变了才重解析 ──
        private static string _kLit, _kRest, _kSub;
        private static Color _lit = Color.white, _rest = Color.gray, _sub = Color.white;

        /// <summary>已唱部分的颜色（不含亮度，亮度由渲染器乘）。</summary>
        internal static Color Lit { get { Sync(); return _lit; } }

        /// <summary>未唱部分的颜色。</summary>
        internal static Color Rest { get { Sync(); return _rest; } }

        /// <summary>翻译副行颜色。</summary>
        internal static Color Sub { get { Sync(); return _sub; } }

        /// <summary>当前落在第几套样式；-1 表示用户手改过颜色。</summary>
        internal static int MatchPreset()
        {
            string l = Norm(Get(Plugin.LyricColorLit, "FFFFFF"));
            string r = Norm(Get(Plugin.LyricColorRest, "808080"));
            string s = Norm(Get(Plugin.LyricColorSub, "FFFFFF"));

            for (int i = 0; i < Presets.Length; i++)
            {
                if (Norm(Presets[i].Lit) == l && Norm(Presets[i].Rest) == r && Norm(Presets[i].Sub) == s)
                    return i;
            }
            return -1;
        }

        internal static string CurrentName()
        {
            int i = MatchPreset();
            return i >= 0 ? Presets[i].Name : "自定义";
        }

        /// <summary>循环到下一套样式，把整套参数一起写掉。</summary>
        internal static int Cycle()
        {
            int cur = MatchPreset();
            if (cur < 0 && Plugin.LyricColorPreset != null) cur = Plugin.LyricColorPreset.Value;
            if (cur < 0 || cur >= Presets.Length) cur = -1;

            int next = (cur + 1) % Presets.Length;
            Apply(next);
            return next;
        }

        /// <summary>把第 i 套样式的全部字段写进配置。之后用户仍可在 cfg 里手改任一项。</summary>
        internal static void Apply(int i)
        {
            if (i < 0 || i >= Presets.Length) return;
            Preset p = Presets[i];

            // 配色是否跟随样式：关掉后点样式卡只换动效，颜色保持用户自己选的
            if (Plugin.LyricColorFromStyle == null || Plugin.LyricColorFromStyle.Value)
            {
                Set(Plugin.LyricColorLit, p.Lit);
                Set(Plugin.LyricColorRest, p.Rest);
                Set(Plugin.LyricColorSub, p.Sub);
            }

            // 样式序号/名字始终记（动效基准就是它），配色单独由 ColorFromStyle 决定
            if (Plugin.LyricColorPreset != null) Plugin.LyricColorPreset.Value = i;
            if (Plugin.LyricStyleName != null) Plugin.LyricStyleName.Value = p.Name;

            if (Plugin.LyricRestAlpha != null) Plugin.LyricRestAlpha.Value = p.RestAlpha;
            if (Plugin.LyricGlow != null) Plugin.LyricGlow.Value = p.Glow > 0.01f;
            if (Plugin.LyricGlowStrength != null) Plugin.LyricGlowStrength.Value = Mathf.Clamp01(p.Glow);
            if (Plugin.LyricBrightness != null) Plugin.LyricBrightness.Value = p.Bright;

            if (Plugin.LyricWeight != null) Plugin.LyricWeight.Value = p.Weight;
            if (Plugin.LyricCharSpacing != null) Plugin.LyricCharSpacing.Value = p.Spacing;
            if (Plugin.LyricSizeScale != null) Plugin.LyricSizeScale.Value = p.SizeScale;

            if (Plugin.LyricWiggle != null) Plugin.LyricWiggle.Value = p.Wiggle > 0.01f;
            if (Plugin.LyricWiggleFps != null) Plugin.LyricWiggleFps.Value = p.WiggleFps;
            if (Plugin.LyricWiggleAmount != null) Plugin.LyricWiggleAmount.Value = p.Wiggle;
            if (Plugin.LyricBreath != null) Plugin.LyricBreath.Value = p.Breath;
            if (Plugin.LyricDrift != null) Plugin.LyricDrift.Value = p.Drift;
            if (Plugin.LyricMotion != null) Plugin.LyricMotion.Value = p.Motion;
            if (Plugin.LyricWarp != null) Plugin.LyricWarp.Value = p.Warp;
            if (Plugin.LyricGlowPulse != null) Plugin.LyricGlowPulse.Value = p.GlowPulse;

            if (Plugin.LyricEnterAnim != null) Plugin.LyricEnterAnim.Value = p.Enter;
            if (Plugin.LyricExitAnim != null) Plugin.LyricExitAnim.Value = p.Exit;

            Sync();
        }

        /// <summary>只换配色（动效保持不变）：面板「配色」那一排走这里。</summary>
        internal static void ApplyColor(int i)
        {
            if (i < 0 || i >= Presets.Length) return;
            Preset p = Presets[i];

            Set(Plugin.LyricColorLit, p.Lit);
            Set(Plugin.LyricColorRest, p.Rest);
            Set(Plugin.LyricColorSub, p.Sub);
            if (Plugin.LyricColorFromStyle != null) Plugin.LyricColorFromStyle.Value = false;

            Sync();
        }

        /// <summary>回到「配色跟着样式走」：把当前样式自带的颜色写回去。</summary>
        internal static void UseStyleColor()
        {
            if (Plugin.LyricColorFromStyle != null) Plugin.LyricColorFromStyle.Value = true;

            int i = Plugin.LyricColorPreset != null ? Plugin.LyricColorPreset.Value : 0;
            if (i >= 0 && i < Presets.Length)
            {
                Preset p = Presets[i];
                Set(Plugin.LyricColorLit, p.Lit);
                Set(Plugin.LyricColorRest, p.Rest);
                Set(Plugin.LyricColorSub, p.Sub);
            }

            Sync();
        }

        // ───────────────────────────── 内部 ─────────────────────────────

        private static void Set(ConfigEntry<string> e, string v)
        {
            if (e != null) e.Value = v;
        }

        private static string Get(ConfigEntry<string> e, string fallback)
        {
            if (e == null) return fallback;
            string v = e.Value;
            return string.IsNullOrEmpty(v) ? fallback : v;
        }

        private static void Sync()
        {
            string l = Norm(Get(Plugin.LyricColorLit, "FFFFFF"));
            string r = Norm(Get(Plugin.LyricColorRest, "808080"));
            string s = Norm(Get(Plugin.LyricColorSub, "FFFFFF"));

            if (l != _kLit) { _kLit = l; _lit = Parse(l, Color.white); }
            if (r != _kRest) { _kRest = r; _rest = Parse(r, Color.gray); }
            if (s != _kSub) { _kSub = s; _sub = Parse(s, Color.white); }
        }

        private static string Norm(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return "FFFFFF";
            string h = hex.Trim();
            if (h.StartsWith("#")) h = h.Substring(1);
            return h.ToUpperInvariant();
        }

        /// <summary>
        /// 解析 6/8 位十六进制。写错不崩、不报错，直接回退 —— cfg 是给人手改的，
        /// 一个字母打错不该让整句歌词消失。
        /// </summary>
        private static Color Parse(string hex, Color fallback)
        {
            string h = Norm(hex);
            if (h.Length != 6 && h.Length != 8) return fallback;
            Color c;
            if (!ColorUtility.TryParseHtmlString("#" + h, out c)) return fallback;
            return c;
        }
    }
}
