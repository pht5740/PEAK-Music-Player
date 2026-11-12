using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using BepInEx.Logging;
using PeakMusic.Core.Netease;
using UnityEngine;

namespace PeakMusic
{
    /// <summary>
    /// 把我们的播放进度推给 Folia 的 Stage API（本机 HTTP + bearer token）。
    ///
    /// 契约来自上游源码（vendor/folia-major/electron/stageApi.cjs）：
    ///   POST /stage/lyrics        { title, artist, album, durationMs, positionMs, paused,
    ///                              lyricSource: { type:"local", lrcContent, tLrcContent } }
    ///   POST /stage/lyrics/clock  { positionMs, paused, durationMs }
    ///   Header: Authorization: Bearer &lt;token&gt;（token 从连接文件里来）
    ///
    /// 连接文件由 Folia 写在 %APPDATA%\&lt;应用名&gt;\stage\connection.json，内容是
    /// { "port": 12345, "baseUrl": "http://127.0.0.1:12345", "token": "..." }。
    /// 应用名随构建而变（Folia / folia-major / PeakFoliaRenderer…），所以这里直接扫目录找最新的那份。
    ///
    /// 注意：Stage 模式必须在它自己的设置里打开（否则 /stage/lyrics 返回 503）；
    /// 这里所有请求都是"发完就算"，失败只写一次日志，绝不阻塞游戏。
    /// </summary>
    internal static class FoliaStageBridge
    {
        private const string Tag = "[Folia桥]";
        private static readonly HttpClient Http = NewClient();
        private static string _base, _token;
        private static float _nextDiscover;
        private static float _nextClock;
        private static string _songKey = "";
        private static bool _warned;

        private static HttpClient NewClient()
        {
            var c = new HttpClient();
            c.Timeout = TimeSpan.FromMilliseconds(1500);
            return c;
        }

        private static ManualLogSource Log { get { return Plugin.Log; } }

        private static bool Enabled
        {
            get { try { return Plugin.SendToFolia != null && Plugin.SendToFolia.Value; } catch { return false; } }
        }

        // ───────────────────────── 发现连接 ─────────────────────────

        private static void EnsureConnection()
        {
            if (!string.IsNullOrEmpty(_base) && !string.IsNullOrEmpty(_token)) return;
            if (Time.unscaledTime < _nextDiscover) return;
            _nextDiscover = Time.unscaledTime + 10f;

            string best = null;
            DateTime bestTime = DateTime.MinValue;
            foreach (string root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) })
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                try
                {
                    foreach (string dir in Directory.GetDirectories(root))
                    {
                        string f = Path.Combine(dir, "stage", "connection.json");
                        if (!File.Exists(f)) continue;
                        DateTime t = File.GetLastWriteTimeUtc(f);
                        if (t > bestTime) { bestTime = t; best = f; }
                    }
                }
                catch { }
            }

            if (best == null)
            {
                if (!_warned) { _warned = true; Log.LogInfo(Tag + " 没找到 Folia 的连接文件（它没启动，或 Stage 模式没开）。"); }
                return;
            }

            try
            {
                string text = File.ReadAllText(best);
                string baseUrl = JsonStr(text, "baseUrl");
                string token = JsonStr(text, "token");
                if (string.IsNullOrEmpty(baseUrl))
                {
                    string port = JsonStr(text, "port");
                    if (!string.IsNullOrEmpty(port)) baseUrl = "http://127.0.0.1:" + port;
                }
                if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(token)) return;

                _base = baseUrl.TrimEnd('/');
                _token = token;
                _warned = false;
                Log.LogInfo(Tag + " 已连上 Folia Stage：" + _base);
            }
            catch (Exception e)
            {
                Log.LogWarning(Tag + " 连接文件解析失败：" + e.Message);
            }
        }

        // ───────────────────────── 对外 ─────────────────────────

        /// <summary>换歌时调用：把整份歌词推过去，并带上起始位置。</summary>
        internal static void PushLyrics(SongInfo song, string lrc, string translated, float positionSec, float durationSec, bool paused)
        {
            if (!Enabled || string.IsNullOrEmpty(lrc)) return;
            EnsureConnection();
            if (string.IsNullOrEmpty(_base)) return;

            string key = song != null ? song.Key : "";
            _songKey = key;

            int durationMs = Mathf.Max(0, Mathf.RoundToInt(durationSec * 1000f));
            int positionMs = Mathf.Max(0, Mathf.RoundToInt(positionSec * 1000f));

            var sb = new StringBuilder(4096);
            sb.Append('{');
            Append(sb, "title", song != null ? song.Name : ""); sb.Append(',');
            Append(sb, "artist", song != null ? song.Artists : ""); sb.Append(',');
            Append(sb, "album", song != null ? song.Album : ""); sb.Append(',');
            sb.Append("\"durationMs\":").Append(durationMs).Append(',');
            sb.Append("\"positionMs\":").Append(positionMs).Append(',');
            sb.Append("\"paused\":").Append(paused ? "true" : "false").Append(',');
            sb.Append("\"lyricSource\":{\"type\":\"local\",\"lrcContent\":");
            AppendRaw(sb, lrc);
            if (!string.IsNullOrEmpty(translated))
            {
                sb.Append(",\"tLrcContent\":");
                AppendRaw(sb, translated);
            }
            sb.Append("}}");

            Post("/stage/lyrics", sb.ToString(), true);
        }

        /// <summary>每帧调用（内部限流到 1s）：只推时钟，不重发歌词。</summary>
        internal static void Tick(MusicPlayer player)
        {
            if (!Enabled || player == null || !player.HasClip) return;
            if (Time.unscaledTime < _nextClock) return;
            _nextClock = Time.unscaledTime + 1f;

            EnsureConnection();
            if (string.IsNullOrEmpty(_base)) return;

            int pos = Mathf.Max(0, Mathf.RoundToInt(player.Time * 1000f));
            int dur = Mathf.Max(0, Mathf.RoundToInt(player.Length * 1000f));

            var sb = new StringBuilder(128);
            sb.Append('{').Append("\"positionMs\":").Append(pos)
              .Append(",\"paused\":").Append(player.IsPlaying ? "false" : "true");
            if (dur > 0) sb.Append(",\"durationMs\":").Append(dur);
            sb.Append('}');

            Post("/stage/lyrics/clock", sb.ToString(), false);
        }

        // ───────────────────────── HTTP ─────────────────────────

        private static void Post(string path, string json, bool isLyrics)
        {
            string url = _base + path;
            string token = _token;

            Task.Run(async () =>
            {
                try
                {
                    using (var req = new HttpRequestMessage(HttpMethod.Post, url))
                    {
                        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                        req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                        using (var res = await Http.SendAsync(req).ConfigureAwait(false))
                        {
                            if (!res.IsSuccessStatusCode)
                            {
                                int code = (int)res.StatusCode;
                                if (code == 503)
                                    Log.LogWarning(Tag + " Folia 的 Stage 模式没开（503）：请在 Folia 设置里打开 Stage 模式并把来源设为 stage-api。");
                                else if (code == 409)
                                    Log.LogInfo(Tag + " Folia 会话已变化（409），下一次换歌会重新推歌词。");
                                else
                                    Log.LogWarning(Tag + " " + path + " 返回 " + code);
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    Log.LogWarning(Tag + " " + path + " 请求失败：" + e.Message);
                }
            });
        }

        // ───────────────────────── 小工具 ─────────────────────────

        private static void Append(StringBuilder sb, string key, string value)
        {
            sb.Append('"').Append(key).Append("\":");
            AppendRaw(sb, value ?? "");
        }

        private static void AppendRaw(StringBuilder sb, string value)
        {
            sb.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        /// <summary>极简 JSON 取值：只需要从连接文件里抠 baseUrl / token / port。</summary>
        private static string JsonStr(string json, string key)
        {
            int i = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (i < 0) return "";
            int colon = json.IndexOf(':', i);
            if (colon < 0) return "";
            int q1 = json.IndexOf('"', colon + 1);
            if (q1 < 0) return "";
            int q2 = q1 + 1;
            while (q2 < json.Length && !(json[q2] == '"' && json[q2 - 1] != '\\')) q2++;
            if (q2 >= json.Length) return "";
            return json.Substring(q1 + 1, q2 - q1 - 1);
        }
    }
}
