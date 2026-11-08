using BepInEx.Configuration;
using UnityEngine;

namespace PeakMusic
{
    /// <summary>
    /// 歌词配色。
    ///
    /// 颜色全部来自 cfg 的十六进制字符串（<c>[歌词] ColorLit / ColorRest / ColorSub</c>），
    /// 所以用户既可以填 <c>FF6B6B</c> 这种自定义色，也可以填 8 位的 <c>FF6B6BAA</c> 连透明度一起定。
    /// 解析结果按"上次解析的字符串"缓存：每帧取色不会触发 GC，改 cfg 之后下一帧自动生效。
    ///
    /// 面板左侧导航底部那栏点一下会在 <see cref="Presets"/> 之间循环，等价于把三个 cfg 一起改掉。
    /// </summary>
    internal static class LyricColors
    {
        internal struct Preset
        {
            internal readonly string Name;
            internal readonly string Lit;    // 已唱（逐字点亮的前半段）
            internal readonly string Rest;   // 未唱（后半段，通常同色但更暗）
            internal readonly string Sub;    // 翻译副行

            internal Preset(string name, string lit, string rest, string sub)
            {
                Name = name;
                Lit = lit;
                Rest = rest;
                Sub = sub;
            }
        }

        internal static readonly Preset[] Presets =
        {
            new Preset("纯白",   "FFFFFF", "FFFFFF", "FFFFFF"),
            new Preset("暖米",   "FFF6E3", "FFF6E3", "E6D9C2"),
            new Preset("霓虹青", "6FF7E4", "6FF7E4", "BCEDE6"),
            new Preset("樱花粉", "FFB3D1", "FFB3D1", "F2DAE4"),
            new Preset("烈焰红", "FF6B6B", "FF6B6B", "FFC9C9"),
            new Preset("薄荷绿", "A6F07A", "A6F07A", "DCF3CB"),
            new Preset("落日橙", "FFC078", "FFC078", "FFE5C4"),
            new Preset("冰川蓝", "A3CBFF", "A3CBFF", "DAEAFF"),
            new Preset("暗夜紫", "C9A6FF", "C9A6FF", "E5D8FF"),
        };

        // ── 缓存：只有 cfg 里的字符串变了才重解析 ──
        private static string _kLit, _kRest, _kSub;
        private static Color _lit = Color.white, _rest = Color.white, _sub = Color.white;

        /// <summary>已唱部分的颜色（不含透明度，透明度由歌词层每帧乘进去）。</summary>
        internal static Color Lit
        {
            get { Sync(); return _lit; }
        }

        internal static Color Rest
        {
            get { Sync(); return _rest; }
        }

        internal static Color Sub
        {
            get { Sync(); return _sub; }
        }

        /// <summary>当前配色落在第几个预设上；-1 表示用户手填了自定义颜色。</summary>
        internal static int MatchPreset()
        {
            string l = Norm(Get(Plugin.LyricColorLit, "FFFFFF"));
            string r = Norm(Get(Plugin.LyricColorRest, "FFFFFF"));
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

        /// <summary>循环到下一套预设，并把三个 cfg 一起写掉。</summary>
        internal static int Cycle()
        {
            int cur = MatchPreset();
            // 用户手改过颜色（MatchPreset = -1）时，从他上次停留的预设往后接着走，而不是突然跳回第一套
            if (cur < 0 && Plugin.LyricColorPreset != null) cur = Plugin.LyricColorPreset.Value;
            if (cur < 0 || cur >= Presets.Length) cur = -1;

            int next = (cur + 1) % Presets.Length;

            Preset p = Presets[next];
            if (Plugin.LyricColorLit != null) Plugin.LyricColorLit.Value = p.Lit;
            if (Plugin.LyricColorRest != null) Plugin.LyricColorRest.Value = p.Rest;
            if (Plugin.LyricColorSub != null) Plugin.LyricColorSub.Value = p.Sub;
            if (Plugin.LyricColorPreset != null) Plugin.LyricColorPreset.Value = next;

            Sync();
            return next;
        }

        // ───────────────────────────── 内部 ─────────────────────────────

        private static string Get(ConfigEntry<string> e, string fallback)
        {
            if (e == null) return fallback;
            string v = e.Value;
            return string.IsNullOrEmpty(v) ? fallback : v;
        }

        private static void Sync()
        {
            string l = Norm(Get(Plugin.LyricColorLit, "FFFFFF"));
            string r = Norm(Get(Plugin.LyricColorRest, "FFFFFF"));
            string s = Norm(Get(Plugin.LyricColorSub, "FFFFFF"));

            if (l != _kLit)
            {
                _kLit = l;
                _lit = Parse(l, Color.white);
            }
            if (r != _kRest)
            {
                _kRest = r;
                _rest = Parse(r, Color.white);
            }
            if (s != _kSub)
            {
                _kSub = s;
                _sub = Parse(s, Color.white);
            }
        }

        private static string Norm(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return "FFFFFF";
            string h = hex.Trim();
            if (h.StartsWith("#")) h = h.Substring(1);
            return h.ToUpperInvariant();
        }

        /// <summary>
        /// 解析 6 位 / 8 位十六进制。8 位时第四段是透明度，这样用户可以做出"半透明的幽灵歌词"。
        /// 解析失败不报错也不崩，直接退回 fallback —— cfg 是给人手改的，写错一个字母不该让歌词消失。
        /// </summary>
        private static Color Parse(string hex, Color fallback)
        {
            string h = Norm(hex);
            if (h.Length != 6 && h.Length != 8) return fallback;

            Color c;
            if (!ColorUtility.TryParseHtmlString("#" + h, out c)) return fallback;

            // 6 位时 TryParseHtmlString 给的是 a=1，正是我们要的；8 位时 a 来自第四段
            return c;
        }
    }
}
