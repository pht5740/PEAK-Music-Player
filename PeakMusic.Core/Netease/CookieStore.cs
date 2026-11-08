using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PeakMusic.Core.Netease
{
    /// <summary>
    /// 登录态落盘。只存 cookie 字符串（MUSIC_U / __csrf / NMTID），不存账号密码。
    ///
    /// 位置：%APPDATA%\PeakMusic\cookie.txt（一行 "k=v; k=v; ..."）
    /// 选 AppData 而不是工程目录，是因为游戏里的插件也要读同一份——
    /// 插件的工作目录是游戏根目录，用固定路径两边都能拿到。
    /// </summary>
    public static class CookieStore
    {
        public static string Dir
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "PeakMusic");
            }
        }

        public static string FilePath
        {
            get { return Path.Combine(Dir, "cookie.txt"); }
        }

        public static bool Exists
        {
            get { return File.Exists(FilePath); }
        }

        public static string Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return "";
                return File.ReadAllText(FilePath, Encoding.UTF8).Trim();
            }
            catch
            {
                return "";
            }
        }

        public static void Save(string cookieText)
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, cookieText ?? "", Encoding.UTF8);
        }

        public static void Clear()
        {
            try
            {
                if (File.Exists(FilePath)) File.Delete(FilePath);
            }
            catch { }
        }

        /// <summary>把 "k=v; k=v" 拆成字典，顺便忽略空项。</summary>
        public static Dictionary<string, string> Parse(string cookieText)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(cookieText)) return d;

            foreach (string part in cookieText.Split(';'))
            {
                string p = part.Trim();
                if (p.Length == 0) continue;
                int eq = p.IndexOf('=');
                if (eq <= 0) continue;
                d[p.Substring(0, eq).Trim()] = p.Substring(eq + 1).Trim();
            }
            return d;
        }

        /// <summary>只保留真正影响登录态的几项，别把一堆跟踪 cookie 存进去。</summary>
        public static string Normalize(IDictionary<string, string> cookies)
        {
            var sb = new StringBuilder();
            string[] keep = { "MUSIC_U", "__csrf", "NMTID", "MUSIC_A_T", "MUSIC_R_T" };
            foreach (string k in keep)
            {
                string v;
                if (cookies.TryGetValue(k, out v) && !string.IsNullOrEmpty(v))
                {
                    if (sb.Length > 0) sb.Append("; ");
                    sb.Append(k).Append('=').Append(v);
                }
            }
            return sb.ToString();
        }

        /// <summary>调试用：把 cookie 打码，别把 MUSIC_U 明文丢进日志。</summary>
        public static string Mask(string cookieText)
        {
            if (string.IsNullOrEmpty(cookieText)) return "(空)";
            var d = Parse(cookieText);
            var sb = new StringBuilder();
            foreach (var kv in d)
            {
                if (sb.Length > 0) sb.Append("; ");
                string v = kv.Value;
                sb.Append(kv.Key).Append('=')
                  .Append(v.Length <= 8 ? "***" : v.Substring(0, 4) + "***" + v.Substring(v.Length - 2));
            }
            return sb.ToString();
        }
    }
}
