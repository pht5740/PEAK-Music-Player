using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Threading;
using PeakMusic.Core;
using PeakMusic.Core.Netease;
using UnityEngine;

namespace PeakMusic
{
    /// <summary>
    /// 本地模式的数据源：读 MusicListsGUI 的只读接口（127.0.0.1:8765–8775）。
    ///
    /// 分工：琳玲的项目负责「读本机网易云/QQ/酷狗的播放状态 + 歌词 + 歌单」，
    /// 我们只消费 `/api/progress`，把它的时间轴和歌词喂给游戏里的 3D 歌词层。
    /// 它不播音频、不控播放器，所以这边也**只读**：不 seek、不暂停外部播放器。
    ///
    /// 两条实测出来的坑（2026-11-14）：
    ///   1) `positionMs` 是 500ms tick 刷新的，会有 600ms 的台阶 → 必须本地外推，否则歌词一顿一顿；
    ///   2) 网易云链路 `endMs` 恒为 0，总长得用 `positionMs + remainingMs` 反推。
    /// </summary>
    internal sealed class LocalBridge : MonoBehaviour
    {
        private const string Tag = "[本地]";
        private const int PortFrom = 8765;
        private const int PortTo = 8775;
        private const int PollMs = 500;

        internal static LocalBridge Instance { get; private set; }

        // ── 后台线程写、主线程读 ──
        private readonly object _gate = new object();
        private bool _alive;
        private string _title = "";
        private string _songKey = "";
        private string _artist = "";
        private string _source = "";
        private double _posMs;
        private double _lenMs;
        private bool _playing;
        private string _lrc = "";
        private double _ageMs;
        private long _recvTicks;

        private Thread _thread;
        private volatile bool _stop;
        private int _port = -1;

        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        // ── 主线程读 ──
        internal bool Alive { get { lock (_gate) return _alive; } }
        internal string Title { get { lock (_gate) return _title; } }
        internal string Artist { get { lock (_gate) return _artist; } }
        internal string Source { get { lock (_gate) return _source; } }
        internal string Lrc { get { lock (_gate) return _lrc; } }
        internal bool Playing { get { lock (_gate) return _playing; } }
        internal int Port { get { return _port; } }

        internal string SongKey
        {
            // 以前每次访问都拼 "歌名|歌手" —— 而它每帧会被问两次（一起听广播 + 歌词层），
            // 每秒 120 个临时字符串。现在只在轮询拿到新歌时拼一次。
            get { lock (_gate) return _songKey; }
        }

        /// <summary>当前进度（毫秒）：在最后一次轮询的基础上，按本地时钟外推，避免 500ms 台阶。</summary>
        internal double PositionMsNow
        {
            get
            {
                lock (_gate)
                {
                    double p = _posMs;
                    if (_playing && _recvTicks > 0)
                        p += (Clock.Elapsed.Ticks - _recvTicks) * 1000.0 / TimeSpan.TicksPerSecond;
                    if (_lenMs > 1000 && p > _lenMs) p = _lenMs;
                    return p;
                }
            }
        }

        internal double LengthMs { get { lock (_gate) return _lenMs; } }

        // 歌词行缓存（只在 lrc 变化时重新解析，避免每帧 Parse）
        private string _lineRaw;
        private List<LrcLine> _lineCache;

        /// <summary>
        /// 取「此刻正在唱的那一句」。时间轴用 PositionMsNow（已本地外推），所以和 3D 歌词同步。
        /// 制作署名行（作词/作曲/…）跳过，灵动岛那一行太窄，显示这些没意义。
        /// </summary>
        internal string LineTextAt(double ms)
        {
            string raw = Lrc;
            if (string.IsNullOrEmpty(raw)) return "";

            if (!ReferenceEquals(raw, _lineRaw) && !string.Equals(raw, _lineRaw, StringComparison.Ordinal))
            {
                _lineRaw = raw;
                _lineCache = null;
                var doc = LrcDocument.Parse(raw);
                if (doc != null && doc.Lines != null && doc.Lines.Count > 0)
                {
                    var list = new List<LrcLine>(doc.Lines.Count);
                    for (int i = 0; i < doc.Lines.Count; i++)
                    {
                        var l = doc.Lines[i];
                        if (string.IsNullOrEmpty(l.Text)) continue;
                        // 必须和 LyricOverlay.FillLines 用同一套过滤条件，
                        // 否则两边行号会错开，同步探针就会误报。
                        if (Plugin.LyricFilterMeta != null && Plugin.LyricFilterMeta.Value
                            && LyricOverlay.IsMetaLine(l.Text)) continue;
                        list.Add(l);
                    }
                    list.Sort(delegate(LrcLine a, LrcLine b2) { return a.Time.CompareTo(b2.Time); });
                    _lineCache = list;
                }
            }

            if (_lineCache == null || _lineCache.Count == 0) return "";

            int lo = 0, hi = _lineCache.Count - 1, best = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (_lineCache[mid].Time.TotalMilliseconds <= ms) { best = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            return best < 0 ? "" : _lineCache[best].Text;
        }

        private void Awake()
        {
            Instance = this;
            _stop = false;
            _thread = new Thread(Loop) { IsBackground = true, Name = "PeakMusic.LocalBridge" };
            _thread.Start();
        }

        private void OnDestroy()
        {
            _stop = true;
            Instance = null;
            try { if (_thread != null) _thread.Join(500); } catch { }
        }

        private void Loop()
        {
            while (!_stop)
            {
                try { Poll(); }
                catch { /* 后台线程绝不能把异常抛出去 */ }
                try { Thread.Sleep(PollMs); } catch { }
            }
        }

        private void Poll()
        {
            if (_port < 0 && !Discover()) return;

            string body = Get("/api/progress");
            if (body == null)
            {
                // 连续失败就重新找端口（她的服务可能重启后退到 8766+）
                _port = -1;
                return;
            }

            var root = MiniJson.AsObject(MiniJson.Parse(body));
            var p = MiniJson.Dict(root, "progress");
            if (p == null) { MarkDead(); return; }

            string title = MiniJson.Str(p, "title", "") ?? "";
            if (string.IsNullOrEmpty(title)) { MarkDead(); return; }

            double pos = MiniJson.Dbl(p, "positionMs", 0);
            double end = MiniJson.Dbl(p, "endMs", 0);
            double remain = MiniJson.Dbl(p, "remainingMs", 0);
            if (end <= 1000 && remain > 0) end = pos + remain;   // 实测网易云链路 endMs 恒 0

            string lrc = BuildLrc(MiniJson.List(p, "lyricsLines"));

            lock (_gate)
            {
                _alive = true;
                _title = title;
                _artist = MiniJson.Str(p, "artist", "") ?? "";
                _songKey = string.IsNullOrEmpty(_title) ? "" : _title + "|" + _artist;
                _source = MiniJson.Str(p, "source", "") ?? "";
                _posMs = pos;
                _lenMs = end;
                _playing = MiniJson.Bool(p, "playing", false);
                _ageMs = MiniJson.Dbl(p, "ageMs", 0);
                _lrc = lrc;
                _recvTicks = Clock.Elapsed.Ticks;
            }
        }

        private void MarkDead()
        {
            lock (_gate) { _alive = false; _lrc = ""; _songKey = ""; }
        }

        private bool Discover()
        {
            for (int port = PortFrom; port <= PortTo; port++)
            {
                string body = Get(port, "/api/health");
                if (body == null) continue;
                _port = port;
                Plugin.Log.LogInfo(Tag + " 找到 MusicListsGUI：127.0.0.1:" + port);
                return true;
            }
            return false;
        }

        private string Get(string path) { return Get(_port, path); }

        private static string Get(int port, string path)
        {
            if (port <= 0) return null;
            try
            {
                using (var resp = Http.GetAsync("http://127.0.0.1:" + port + path).GetAwaiter().GetResult())
                {
                    if (!resp.IsSuccessStatusCode) return null;
                    return resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                }
            }
            catch { return null; }
        }

        /// <summary>
        /// 把她的 `lyricsLines[{timeMs,text}]` 还原成 LRC 文本，交给现有的 FillLines 解析。
        /// 这样翻译/过滤/时间轴都复用同一套逻辑，不在两处各写一份解析。
        /// </summary>
        private static string BuildLrc(List<object> lines)
        {
            if (lines == null || lines.Count == 0) return "";
            var sb = new StringBuilder(lines.Count * 32);
            for (int i = 0; i < lines.Count; i++)
            {
                var o = MiniJson.AsObject(lines[i]);
                if (o == null) continue;
                double ms = MiniJson.Dbl(o, "timeMs", 0);
                string text = MiniJson.Str(o, "text", "") ?? "";
                if (text.Length == 0) continue;
                int total = (int)Math.Max(0, ms);
                int m = total / 60000;
                int s = (total / 1000) % 60;
                int f = total % 1000;
                sb.Append('[').Append(m.ToString("00", CultureInfo.InvariantCulture)).Append(':')
                  .Append(s.ToString("00", CultureInfo.InvariantCulture)).Append('.')
                  .Append(f.ToString("000", CultureInfo.InvariantCulture)).Append(']')
                  .Append(text).Append('\n');
            }
            return sb.ToString();
        }
    }
}
