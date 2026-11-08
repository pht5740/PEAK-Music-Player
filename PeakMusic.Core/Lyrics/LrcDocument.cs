using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace PeakMusic.Core
{
    public sealed class LrcLine
    {
        public TimeSpan Time;
        public string Text = "";
    }

    /// <summary>
    /// LRC 解析。专门为网易的返回格式写的，它比标准 LRC 脏：
    ///   1) 开头会混进类似 {"t":0,"c":[{"tx":"作词: "}]} 的 JSON 行（作者/编曲署名），必须丢掉；
    ///   2) 一行可能有多个时间戳 [00:10.00][01:20.00]同一句；
    ///   3) 时间戳格式 [mm:ss.xx] / [mm:ss.xxx] / [mm:ss] 都出现过；
    ///   4) 元信息标签 [by:xxx] [offset:0] 要识别出来，别当成歌词。
    /// </summary>
    public sealed class LrcDocument
    {
        private static readonly Regex TagRegex = new Regex(@"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled);
        private static readonly Regex MetaRegex = new Regex(@"^\[(ar|ti|al|by|offset|re|ve|length|kana|ti|tool):", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public readonly List<LrcLine> Lines = new List<LrcLine>();

        /// <summary>整体偏移（毫秒），来自 [offset:] 标签。</summary>
        public int OffsetMs;

        public static LrcDocument Parse(string lrc)
        {
            var doc = new LrcDocument();
            if (string.IsNullOrEmpty(lrc)) return doc;

            string[] rows = lrc.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            foreach (string raw in rows)
            {
                string row = raw.Trim();
                if (row.Length == 0) continue;

                // 网易的署名行是 JSON，直接跳过
                if (row[0] == '{') continue;

                if (MetaRegex.IsMatch(row))
                {
                    int off;
                    if (row.StartsWith("[offset:", StringComparison.OrdinalIgnoreCase))
                    {
                        string num = row.Substring(8).TrimEnd(']');
                        if (int.TryParse(num, out off)) doc.OffsetMs = off;
                    }
                    continue;
                }

                var matches = TagRegex.Matches(row);
                if (matches.Count == 0) continue;

                // 去掉所有时间戳，剩下的就是歌词文本
                string text = TagRegex.Replace(row, "").Trim();
                if (text.Length == 0) continue;

                foreach (Match m in matches)
                {
                    int min = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    int sec = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);

                    int ms = 0;
                    if (m.Groups[3].Success)
                    {
                        string frac = m.Groups[3].Value;
                        // .28 → 280ms，.286 → 286ms，统一补到 3 位
                        while (frac.Length < 3) frac += "0";
                        if (frac.Length > 3) frac = frac.Substring(0, 3);
                        ms = int.Parse(frac, CultureInfo.InvariantCulture);
                    }

                    doc.Lines.Add(new LrcLine
                    {
                        Time = new TimeSpan(0, 0, min, sec, ms),
                        Text = text,
                    });
                }
            }

            doc.Lines.Sort((a, b) => a.Time.CompareTo(b.Time));
            return doc;
        }

        /// <summary>二分查找：给定播放秒数，返回当前应该显示的行号，-1 表示还没到第一句。</summary>
        public int IndexAt(float seconds)
        {
            if (Lines.Count == 0) return -1;

            double target = seconds * 1000.0 + OffsetMs;
            int lo = 0, hi = Lines.Count - 1, ans = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (Lines[mid].Time.TotalMilliseconds <= target) { ans = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            return ans;
        }

        public string TextAt(float seconds)
        {
            int i = IndexAt(seconds);
            return i < 0 ? "" : Lines[i].Text;
        }
    }
}
