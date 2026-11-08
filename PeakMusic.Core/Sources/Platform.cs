using System;

namespace PeakMusic.Core.Sources
{
    /// <summary>
    /// 音乐平台。参考 MuoniumPlayer 的 <c>MusicPlatform</c>：每个平台自带显示名与品牌色，
    /// UI 只要按平台取值就行，不写死任何颜色。
    ///
    /// <para><b>只能往后追加</b>：配置里存的是序号，插在中间会让已保存的选择错位。</para>
    /// </summary>
    public enum Platform
    {
        /// <summary>网易云音乐：搜索/歌词/取流全部匿名可用，会员曲需登录且有 VIP。</summary>
        Netease = 0,

        /// <summary>
        /// QQ 音乐：搜索与匿名取流可用，但匿名只有 128k mp3（M500）。
        /// 付费曲（pay.payplay=1）的 purl 为空，等价于网易的 -110。
        /// </summary>
        QQ = 1,
    }

    /// <summary>平台元数据。颜色沿用各家品牌色，和 MuoniumPlayer 里的一致。</summary>
    public static class Platforms
    {
        public static readonly Platform[] All = { Platform.Netease, Platform.QQ };

        public static string DisplayName(Platform p)
        {
            switch (p)
            {
                case Platform.QQ: return "QQ音乐";
                default: return "网易云";
            }
        }

        /// <summary>品牌色（十六进制，不带 #），与 MuoniumPlayer 的枚举值对齐。</summary>
        public static int BrandRgb(Platform p)
        {
            switch (p)
            {
                case Platform.QQ: return 0x31C27C;      // QQ 绿
                default: return 0xE94747;              // 网易红
            }
        }

        /// <summary>UI 直接可用的 "#RRGGBB"，IMGUI 富文本与 Unity ColorUtility 都吃这个。</summary>
        public static string BrandHex(Platform p)
        {
            return "#" + BrandRgb(p).ToString("X6");
        }

        /// <summary>品牌色的 Unity 颜色（各分量 0–1）。</summary>
        public static void BrandColor(Platform p, out float r, out float g, out float b)
        {
            int v = BrandRgb(p);
            r = ((v >> 16) & 0xFF) / 255f;
            g = ((v >> 8) & 0xFF) / 255f;
            b = (v & 0xFF) / 255f;
        }

        public static Platform FromInt(int i)
        {
            if (i < 0 || i >= All.Length) return Platform.Netease;
            return All[i];
        }

        public static int ToInt(Platform p)
        {
            for (int i = 0; i < All.Length; i++)
                if (All[i] == p) return i;
            return 0;
        }
    }
}
