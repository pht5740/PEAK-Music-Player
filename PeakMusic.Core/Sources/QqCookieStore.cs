using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PeakMusic.Core.Sources
{
    /// <summary>
    /// QQ 音乐登录态落盘。独立于网易的 <c>CookieStore</c>，只存 cookie 字符串。
    ///
    /// 位置：%APPDATA%\PeakMusic\qq_cookie.txt（一行 "k=v; k=v; ..."）
    ///
    /// QQ 登录态的关键字段：
    ///   <list type="bullet">
    ///   <item><c>uin</c> —— QQ 号（取流 CgiGetVkey 的 uin 参数要填它）</item>
    ///   <item><c>qm_keyst</c> / <c>qqmusic_key</c> —— 音乐票据（qqCookieMusicKey）</item>
    ///   <item><c>p_skey</c> / <c>p_uin</c> —— ptlogin 会话票</item>
    ///   <item><c>musickey</c> —— 老版票据，部分接口还在用</item>
    ///   </list>
    /// </summary>
    public static class QqCookieStore
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
            get { return Path.Combine(Dir, "qq_cookie.txt"); }
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
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(FilePath, cookieText ?? "", Encoding.UTF8);
            }
            catch { }
        }

        public static void Clear()
        {
            try
            {
                if (File.Exists(FilePath)) File.Delete(FilePath);
            }
            catch { }
        }

        /// <summary>把 "k=v; k=v" 拆成字典，忽略空项。</summary>
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

        /// <summary>只保留登录态真正要用的几项。</summary>
        public static string Normalize(IDictionary<string, string> cookies)
        {
            var sb = new StringBuilder();
            string[] keep =
            {
                "uin", "wxuin", "qqmusic_key", "qm_keyst", "musickey", "p_skey",
                "p_uin", "pt4_token", "euin", "wxunionid",
            };
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

        /// <summary>调试用：把 cookie 打码。</summary>
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
