using UnityEngine;

namespace PeakMusic
{
    /// <summary>
    /// 一套主题色板。字段划分抄自 MuoniumPlayer 的 NCMTheme（11 个语义角色），
    /// 因为按"角色"而不是"颜色"命名，换主题时不用改任何绘制代码。
    ///
    /// 关键观感差异（和原来那版默认灰盒子比）：
    ///   1) 背景不是中性灰，是**带色相的深色**（玫瑰紫 / 蓝灰 / 青绿），一眼就有气质；
    ///   2) 边框比背景亮得多（Border 明显可见），面板才有"边缘"；
    ///   3) 强调色分两级（Accent / AccentHover），悬停才有层次；
    ///   4) 文本两级 + 元素背景两级，共 5 级层次，不是全白一坨。
    /// </summary>
    internal struct Palette
    {
        public string Name;
        public Color Bg;            // 面板底
        public Color Elem;          // 元素底（按钮、卡片、行）
        public Color ElemHover;     // 元素悬停
        public Color Text;          // 主文本
        public Color Text2;         // 次文本
        public Color Accent;        // 强调
        public Color AccentHover;   // 强调悬停
        public Color Input;         // 输入框底
        public Color Nav;           // 底栏 / 导航条
        public Color Border;        // 边框

        public static Palette FromHex(string name, int bg, int elem, int elemHover, int text, int text2,
            int accent, int accentHover, int input, int nav, int border)
        {
            return new Palette
            {
                Name = name,
                Bg = Hex(bg),
                Elem = Hex(elem),
                ElemHover = Hex(elemHover),
                Text = Hex(text),
                Text2 = Hex(text2),
                Accent = Hex(accent),
                AccentHover = Hex(accentHover),
                Input = Hex(input),
                Nav = Hex(nav),
                Border = Hex(border),
            };
        }

        public static Color Hex(int rgb)
        {
            return new Color(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);
        }

        /// <summary>按 alpha 叠透明度，用于"半透明面板 / 悬停铺色"这类场合。</summary>
        public static Color A(Color c, float a)
        {
            c.a = a;
            return c;
        }

        public static Color Lerp(Color a, Color b, float t)
        {
            return Color.Lerp(a, b, Mathf.Clamp01(t));
        }
    }

    internal static class Palettes
    {
        // 全部取自 MuoniumPlayer 的 ThemePreset（同一套 hex），名字也照抄，方便对照。
        public static readonly Palette[] All =
        {
            // 丝绒玫瑰：它的默认主题。玫瑰紫底 + 柔和粉强调，最"手机音乐 App"
            Palette.FromHex("丝绒玫瑰",
                0x211821, 0x30212C, 0x493342, 0xFFF7FB, 0xC2AAB7, 0xE48AAB, 0xF4A9C4, 0x2A1D27, 0x1A1219, 0x604252),

            // 午夜石墨：中性蓝灰，最不抢游戏画面
            Palette.FromHex("午夜石墨",
                0x111419, 0x1B2028, 0x323B49, 0xF5F7FA, 0xA3ACBA, 0x8FA8C7, 0xB4C8E5, 0x202731, 0x0C0F14, 0x4C5869),

            // 极光海：青绿强调，PEAK 的雪山雾色最搭
            Palette.FromHex("极光海",
                0x0D1720, 0x142631, 0x203D4A, 0xEFFBFF, 0x91AAB5, 0x54D6C1, 0x7DE8D6, 0x10212B, 0x091118, 0x2B5160),

            // 曜石红：经典网易云红，最"正统"
            Palette.FromHex("曜石红",
                0x1E1E1E, 0x252525, 0x383838, 0xFFFFFF, 0x9B9B9B, 0xD60017, 0xF04455, 0x292727, 0x191919, 0x454545),
        };

        public static Palette Get(int index)
        {
            if (index < 0 || index >= All.Length) return All[0];
            return All[index];
        }

        public static int Count { get { return All.Length; } }
    }
}
