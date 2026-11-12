using System;
using System.Globalization;

namespace PeakMusic.Core.Lyrics
{
    /// <summary>
    /// 一个字素（grapheme）。中文按字、拉丁按组合字符，避免把"表情+肤色"或
    /// "e+´"这种拆成两半。SourceStart/SourceLength 是它在原字符串里的 UTF-16 位置。
    /// </summary>
    public sealed class LyricGrapheme
    {
        public string Text;
        public int SourceStart;
        public int SourceLength;

        public LyricGrapheme(string text, int sourceStart, int sourceLength)
        {
            Text = text;
            SourceStart = sourceStart;
            SourceLength = sourceLength;
        }
    }

    /// <summary>
    /// 一个字素在某一帧的渲染姿态：透明度、缩放、X/Y 像素偏移、旋转（弧度）。
    /// </summary>
    public readonly struct LyricGlyphFrame
    {
        public readonly double Alpha;
        public readonly double Scale;
        public readonly double XOffset;
        public readonly double YOffset;
        public readonly double Rotation;

        /// <summary>弹出进度：字刚出现时 1，约 160ms 内回落到 0。给「逐字光晕脉冲」用。</summary>
        public readonly double Pop;

        public LyricGlyphFrame(double alpha, double scale, double x, double y, double rotation, double pop = 0)
        {
            Alpha = alpha;
            Scale = scale;
            XOffset = x;
            YOffset = y;
            Rotation = rotation;
            Pop = pop;
        }
    }

    /// <summary>
    /// 逐字动画（纯逻辑，不碰 Unity）。
    /// 移植自 PeakDangoMusic/DangoMusic 的 LyricAnimation（GPL-3.0，作者署名见 NOTICE）。
    ///
    /// 两套独立的驱动：
    ///   1) 逐字显现 —— 由<b>歌曲进度</b>驱动。有逐字时间（单词模式）就按词弹出，
    ///      没有就按"整句时间均分"或"CJK 两字一组"估算，一个字一个字地亮；
    ///   2) 轻微抖动 —— 由<b>单调递增的动画秒数</b>驱动，8Hz 的确定性噪声，
    ///      同一句歌词每次播放抖动都一样，不会乱跳。
    /// </summary>
    public sealed class LyricAnimation
    {
        private readonly double _lineStartMs;
        private readonly double _revealSpanMs;
        private readonly double[] _wordStarts;
        private readonly bool[] _visible;
        private readonly int _seed;

        public LyricGrapheme[] Graphemes { get; private set; }

        /// <param name="text">整句歌词文本。</param>
        /// <param name="lineStartMs">这一句开始的毫秒。</param>
        /// <param name="durationMs">这一句持续多久（毫秒）。&lt;=0 时按 5000 兜底。</param>
        /// <param name="wholeToken">true = 单词模式，整词同时弹出（中文无词库时按字）。</param>
        /// <param name="singleCharacter">true = 逐字模式（无逐字时间时按单字估算）。</param>
        public LyricAnimation(string text, double lineStartMs, double durationMs = 0, bool wholeToken = false, bool singleCharacter = false)
        {
            if (string.IsNullOrEmpty(text)) text = "";

            _lineStartMs = lineStartMs;

            // 拆字素：StringInfo.ParseCombiningCharacters 会把"组合字符序列"（含代理对）归成一个
            var starts = StringInfo.ParseCombiningCharacters(text);
            var glyphs = new LyricGrapheme[starts.Length];
            _visible = new bool[starts.Length];
            for (int i = 0; i < starts.Length; i++)
            {
                int end = i + 1 < starts.Length ? starts[i + 1] : text.Length;
                string g = text.Substring(starts[i], end - starts[i]);
                glyphs[i] = new LyricGrapheme(g, starts[i], end - starts[i]);
                _visible[i] = !AllWhiteSpace(g);
            }
            Graphemes = glyphs;

            double duration = durationMs > 0 ? durationMs : 5000d;
            // 逐字显现的时间窗口：至少 250ms，最多这一句时长的 60%，跟字数和时长都挂钩
            _revealSpanMs = Math.Min(Math.Max(glyphs.Length * 55d, 250), Math.Max(duration, 400) * .6);

            long timingEnd = (long)Clamp(lineStartMs + (durationMs > 0 ? durationMs : 5000), long.MinValue, long.MaxValue);
            _wordStarts = wholeToken
                ? AllSameStart(glyphs, lineStartMs)
                : (singleCharacter ? EstimateCharacterStarts(text, glyphs, timingEnd, lineStartMs) : EstimateWordStarts(glyphs, lineStartMs, duration));

            _seed = StableSeed(text);
        }

        private static double[] AllSameStart(LyricGrapheme[] glyphs, double start)
        {
            var r = new double[glyphs.Length];
            for (int i = 0; i < r.Length; i++) r[i] = start;
            return r;
        }

        /// <summary>
        /// 采样某字素在当前帧的姿态。
        /// </summary>
        /// <param name="graphemeIndex">字素下标。</param>
        /// <param name="songPositionMs">当前歌曲进度（毫秒）。</param>
        /// <param name="animationSeconds">单调递增的动画时钟（秒），驱动抖动。</param>
        /// <param name="wordPop">是否逐字弹出。</param>
        /// <param name="wiggle">是否抖动。</param>
        /// <param name="wiggleFrequencyHz">抖动频率（Hz）。</param>
        /// <param name="wiggleAmount">抖动幅度（0–2）。</param>
        public LyricGlyphFrame Sample(int graphemeIndex, double songPositionMs, double animationSeconds,
            bool wordPop = true, bool wiggle = true, double wiggleFrequencyHz = 8, double wiggleAmount = .8)
        {
            if ((uint)graphemeIndex >= (uint)Graphemes.Length || !_visible[graphemeIndex])
                return new LyricGlyphFrame(0, 1, 0, 0, 0);

            double alpha, scale = 1, x = 0, y = 0, rotation = 0, pop = 0;

            if (wordPop && _wordStarts != null)
            {
                // 弹出：从它自己的开始时间起，160ms 内 alpha 0→1，scale 从 1.55 回弹到 1
                double ageMs = songPositionMs - _wordStarts[graphemeIndex];
                alpha = Clamp(ageMs * 3 / 160, 0, 1);
                double back = Clamp(1 - ageMs / 160, 0, 1);
                scale = 1 + .55 * back * back;
                pop = back * back;                 // 同一条回弹曲线，单独给光晕用
            }
            else
            {
                // 逐字淡入：按字素在整句里的位置错开，180ms 的 smoothstep 淡入 + 5px 上浮
                double appearAt = graphemeIndex / (double)Math.Max(Graphemes.Length, 1) * _revealSpanMs;
                double t = Clamp((songPositionMs - _lineStartMs - appearAt) / 180, 0, 1);
                alpha = t * t * (3 - 2 * t);
                y = (1 - alpha) * 5;
            }

            if (wiggle)
            {
                double frequency = (!double.IsNaN(wiggleFrequencyHz) && !double.IsInfinity(wiggleFrequencyHz)) ? Clamp(wiggleFrequencyHz, 4, 24) : 8;
                double amount = (!double.IsNaN(wiggleAmount) && !double.IsInfinity(wiggleAmount)) ? Clamp(wiggleAmount, 0, 2) : .8;
                double clock = (!double.IsNaN(animationSeconds) && !double.IsInfinity(animationSeconds)) ? Clamp(animationSeconds, 0, long.MaxValue / 48d) : 0;
                long bucket = (long)Math.Floor(clock * frequency);
                rotation = Noise(graphemeIndex, bucket, 1, _seed) * 9 * amount * Math.PI / 180;
                x = Noise(graphemeIndex, bucket, 2, _seed) * .8 * amount;
                y += Noise(graphemeIndex, bucket, 3, _seed) * 1.6 * amount;
                scale *= 1 + Noise(graphemeIndex, bucket, 4, _seed) * .12 * amount;
            }

            return new LyricGlyphFrame(alpha, scale, x, y, rotation, pop);
        }

        /// <summary>逐字模式：把整句时长按字数均分，每个字有自己的开始时间。</summary>
        private static double[] EstimateCharacterStarts(string text, LyricGrapheme[] glyphs, long endMs, double lineStartMs)
        {
            int count = Math.Max(1, CountNonSpace(text));
            var starts = new double[glyphs.Length];
            double span = Math.Max(1, endMs - lineStartMs);
            int nonSpace = 0;
            for (int i = 0; i < glyphs.Length; i++)
            {
                if (AllWhiteSpace(glyphs[i].Text)) { starts[i] = lineStartMs; continue; }
                starts[i] = lineStartMs + span * nonSpace / count;
                nonSpace++;
            }
            return starts;
        }

        /// <summary>默认估算：拉丁按词、CJK 两字一组，按"min(75% 时长, 每组 330ms)"铺开。</summary>
        private static double[] EstimateWordStarts(LyricGrapheme[] glyphs, double start, double duration)
        {
            var groups = new int[glyphs.Length];
            int group = 0, cjk = 0;
            bool pending = false;
            for (int i = 0; i < glyphs.Length; i++)
            {
                string value = glyphs[i].Text;
                if (AllWhiteSpace(value)) { if (pending) { group++; pending = false; cjk = 0; } groups[i] = -1; continue; }
                bool asian = value[0] >= 0x2e80;
                if (pending && (asian && cjk == 0 || !asian && cjk > 0)) { group++; pending = false; cjk = 0; }
                groups[i] = group;
                pending = true;
                if (asian && ++cjk >= 2) { group++; pending = false; cjk = 0; }
            }
            int count = Math.Max(1, group + (pending ? 1 : 0));
            double span = Math.Min(Math.Max(300, duration) * .75, count * 330d);
            var result = new double[glyphs.Length];
            for (int i = 0; i < glyphs.Length; i++)
                result[i] = groups[i] < 0 ? start : start + span * Math.Max(0, groups[i]) / count;
            return result;
        }

        private static int CountNonSpace(string text)
        {
            int n = 0;
            foreach (char c in text) if (!char.IsWhiteSpace(c)) n++;
            return n;
        }

        private static bool AllWhiteSpace(string s)
        {
            foreach (char c in s) if (!char.IsWhiteSpace(c)) return false;
            return true;
        }

        // net472 的 System.Math 没有 Clamp（.NET Core 才有），这里补一个
        private static double Clamp(double v, double min, double max)
        {
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }

        private static int StableSeed(string text)
        {
            uint hash = 2166136261;
            foreach (char c in text) hash = unchecked((hash ^ c) * 16777619);
            return unchecked((int)hash);
        }

        private static double Noise(int index, long frame, int salt, int textSeed)
        {
            uint hash = unchecked((uint)(index * 73856093) ^ (uint)(frame * 19349663L) ^ (uint)(salt * 83492791) ^ (uint)textSeed);
            hash ^= hash >> 13;
            hash = unchecked(hash * 1540483477);
            hash ^= hash >> 15;
            return (hash & 0xFFFF) / 32767.5 - 1;
        }
    }
}
