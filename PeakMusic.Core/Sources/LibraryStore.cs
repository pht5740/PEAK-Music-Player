using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using PeakMusic.Core.Netease;

namespace PeakMusic.Core.Sources
{
    /// <summary>
    /// 本机曲库落盘：搜索历史 + 收藏歌曲。
    ///
    /// 位置：%APPDATA%\PeakMusic\
    ///   <list type="bullet">
    ///   <item><c>history.txt</c> —— 搜索关键词，一行一条，最近的排最前，去重上限 20 条。</item>
    ///   <item><c>favorites.txt</c> —— 收藏的歌，每行一条 <c>平台|songmid/Id|歌名|歌手|专辑|时长ms</c>，
    ///       用 <c>\u0001</c> 当字段分隔符（歌名里不可能有这个字符）。</item>
    ///   </list>
    ///
    /// 纯文本、可手工编辑，坏行直接跳过不影响整体。
    /// </summary>
    public static class LibraryStore
    {
        private const char Sep = '\u0001';
        private const int MaxHistory = 20;

        public static string Dir
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "PeakMusic");
            }
        }

        private static string HistoryPath { get { return Path.Combine(Dir, "history.txt"); } }
        private static string FavoritesPath { get { return Path.Combine(Dir, "favorites.txt"); } }

        // ───────────────────────────── 搜索历史 ─────────────────────────────

        /// <summary>读历史关键词，最近的排最前。永不抛异常。</summary>
        public static List<string> LoadHistory()
        {
            var list = new List<string>();
            try
            {
                if (!File.Exists(HistoryPath)) return list;
                foreach (string raw in File.ReadAllLines(HistoryPath, Encoding.UTF8))
                {
                    string k = (raw ?? "").Trim();
                    if (k.Length == 0 || list.Contains(k)) continue;
                    list.Add(k);
                }
            }
            catch { }
            return list;
        }

        /// <summary>把关键词记到历史最前（去重、截断上限），并写盘。</summary>
        public static void AddHistory(string keyword)
        {
            if (string.IsNullOrEmpty(keyword)) return;
            string k = keyword.Trim();
            if (k.Length == 0) return;

            var list = LoadHistory();
            list.RemoveAll(x => string.Equals(x, k, StringComparison.OrdinalIgnoreCase));
            list.Insert(0, k);
            if (list.Count > MaxHistory) list.RemoveRange(MaxHistory, list.Count - MaxHistory);
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllLines(HistoryPath, list.ToArray(), Encoding.UTF8);
            }
            catch { }
        }

        // ───────────────────────────── 收藏 ─────────────────────────────

        /// <summary>读收藏列表，保持写入顺序（新的在前）。坏行跳过。</summary>
        public static List<SongInfo> LoadFavorites()
        {
            var list = new List<SongInfo>();
            try
            {
                if (!File.Exists(FavoritesPath)) return list;
                foreach (string raw in File.ReadAllLines(FavoritesPath, Encoding.UTF8))
                {
                    SongInfo s = ParseLine(raw);
                    if (s != null) list.Add(s);
                }
            }
            catch { }
            return list;
        }

        /// <summary>收藏一首歌（已存在则顶到最前），写盘。</summary>
        public static void AddFavorite(SongInfo song)
        {
            if (song == null) return;
            var list = LoadFavorites();
            list.RemoveAll(x => x.Key == song.Key);
            list.Insert(0, song);
            SaveFavorites(list);
        }

        /// <summary>取消收藏（按 Key）。写盘。</summary>
        public static void RemoveFavorite(string key)
        {
            var list = LoadFavorites();
            list.RemoveAll(x => x.Key == key);
            SaveFavorites(list);
        }

        /// <summary>是否已收藏（按 Key）。</summary>
        public static bool IsFavorite(string key)
        {
            foreach (SongInfo s in LoadFavorites())
                if (s.Key == key) return true;
            return false;
        }

        private static void SaveFavorites(List<SongInfo> list)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var lines = new List<string>();
                foreach (SongInfo s in list)
                {
                    string line = Serialize(s);
                    if (line != null) lines.Add(line);
                }
                File.WriteAllLines(FavoritesPath, lines.ToArray(), Encoding.UTF8);
            }
            catch { }
        }

        private static string Serialize(SongInfo s)
        {
            if (s == null) return null;
            string id = string.IsNullOrEmpty(s.SourceId) ? s.Id.ToString() : s.SourceId;
            return string.Join(Sep.ToString(),
                Platforms.ToInt(s.Platform).ToString(),
                id,
                Safe(s.Name),
                Safe(s.Artists),
                Safe(s.Album),
                s.DurationMs.ToString());
        }

        private static SongInfo ParseLine(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            string[] f = raw.Split(Sep);
            if (f.Length < 6) return null;
            try
            {
                int platform = int.Parse(f[0]);
                long dur = long.Parse(f[5]);
                var s = new SongInfo
                {
                    Platform = Platforms.FromInt(platform),
                    Name = f[2],
                    Artists = f[3],
                    Album = f[4],
                    DurationMs = dur,
                };
                // QQ 的 id 是 songmid 字符串，网易的是数字字符串
                long numeric;
                if (long.TryParse(f[1], out numeric)) s.Id = numeric;
                else s.SourceId = f[1];
                return s;
            }
            catch
            {
                return null;
            }
        }

        private static string Safe(string v)
        {
            return (v ?? "").Replace(Sep, ' ').Replace("\r", " ").Replace("\n", " ");
        }
    }
}
