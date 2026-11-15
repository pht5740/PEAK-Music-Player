using System;
using System.Collections.Generic;
using PeakMusic.Core;
using PeakMusic.Core.Netease;
using UnityEngine;

namespace PeakMusic
{
    /// <summary>
    /// 实时歌词宿主：世界空间 3D 歌词（TextMeshPro SDF）。
    /// 歌词渲染部分移植自第三方开源项目（GPL-3.0），来源与许可见仓库根目录 NOTICE.md。
    ///
    /// <para><b>只保留这一套 3D 歌词</b>：歌词不是画在屏幕上，而是在相机左右两侧、
    /// 按 FOV 算出的散布角、距离相机一定距离处放置真正的 3D 文字网格，正对相机、
    /// 逐字弹出/抖动。旧的 IMGUI 居中平铺那套已经移除。</para>
    ///
    /// <para>位置由 cfg 的 [歌词] 段控制：Distance（离镜头的距离）、Edge（左右位置）、
    /// Y（上下位置）、Side（单侧/两侧交替）、Size（字号）。面板左侧导航也能即时调。</para>
    /// </summary>
    public sealed class LyricOverlay : MonoBehaviour
    {
        private const string Tag = "[歌词]";

        private MusicPlayer _player;

        private readonly List<LrcLine> _lines = new List<LrcLine>();
        private readonly List<LrcLine> _trans = new List<LrcLine>();
        private SongInfo _song;
        private string _lrcRaw = "";

        // 世界空间 3D 歌词（移植部分见 NOTICE.md）。没有主相机（菜单/加载）时它自然不画。
        private WorldLyricRenderer _world;

        public static LyricOverlay Instance { get; private set; }

        private void Awake()
        {
            Instance = this;
            _world = new WorldLyricRenderer();
        }

        private void OnDestroy()
        {
            if (_world != null) { _world.Dispose(); _world = null; }
            if (Instance == this) Instance = null;
        }

        // ───────────────────────────── 对外 ─────────────────────────────

        public void SetLyrics(SongInfo song, string lrc)
        {
            SetLyrics(song, lrc, null);
        }

        public void SetLyrics(SongInfo song, string lrc, string translated)
        {
            _song = song;
            _lrcRaw = lrc ?? "";
            _lines.Clear();
            _trans.Clear();

            if (string.IsNullOrEmpty(_lrcRaw))
            {
                if (Plugin.Log != null)
                    Plugin.Log.LogInfo(Tag + " 这首没有歌词，只显示歌名。");
                return;
            }

            FillLines(_lines, _lrcRaw);
            FillLines(_trans, translated);

            // 喂给世界空间渲染器（3D 歌词）
            if (_world != null)
                _world.SetLyrics(song != null ? song.Key : "", _lines);

            // 推给 Folia 的 Stage API（开关在 [歌词] SendToFolia）
            try
            {
                var pl = MusicPlayer.Active;
                FoliaStageBridge.PushLyrics(song, _lrcRaw, translated,
                    pl != null ? pl.Time : 0f,
                    pl != null ? pl.Length : 0f,
                    pl == null || !pl.IsPlaying);
            }
            catch { }

            if (Plugin.Log != null)
                Plugin.Log.LogInfo(Tag + " 歌词就绪：" + _lines.Count + " 行" +
                                   (_trans.Count > 0 ? "（含翻译 " + _trans.Count + " 行）" : "") +
                                   " · " + (song != null ? song.Name : "?") +
                                   " · 世界空间 3D（TMP SDF，逐字动画）");
        }

        public void Clear()
        {
            _song = null;
            _lines.Clear();
            _trans.Clear();
            _lrcRaw = "";
            if (_world != null) _world.SetLyrics("", new List<LrcLine>());
        }

        private static void FillLines(List<LrcLine> dst, string raw)
        {
            if (string.IsNullOrEmpty(raw)) return;

            LrcDocument doc = LrcDocument.Parse(raw);
            if (doc == null || doc.Lines == null) return;

            foreach (LrcLine l in doc.Lines)
            {
                if (l == null) continue;
                if (string.IsNullOrEmpty(l.Text) || l.Text.Trim().Length == 0) continue;
                if (Plugin.LyricFilterMeta != null && Plugin.LyricFilterMeta.Value && IsMetaLine(l.Text)) continue;
                dst.Add(l);
            }
        }

        /// <summary>制作署名行的前缀。"词：xxx" 这种，后面必须跟冒号或空格才算。</summary>
        private static readonly string[] MetaPrefixes =
        {
            "作词", "作曲", "编曲", "制作人", "出品人", "监制", "混音", "母带", "录音", "和声",
            "演唱", "原唱", "人声", "后期", "封面", "企划", "制作", "设计", "助理", "词曲",
            "吉他", "贝斯", "键盘", "弦乐", "鼓", "统筹", "策划", "出品", "发行", "词", "曲",
            "OP", "SP", "Lyrics", "Composed", "Arranged", "Produced", "Mixed", "Mastered",
        };

        internal static bool IsMetaLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            string t = text.Trim();

            if (t.Length >= 2 && t[0] == '【' && t[t.Length - 1] == '】') return true;
            if (t.IndexOf("正版授权", StringComparison.Ordinal) >= 0) return true;

            for (int i = 0; i < MetaPrefixes.Length; i++)
            {
                string p = MetaPrefixes[i];
                if (t.StartsWith(p, StringComparison.Ordinal))
                {
                    if (t.Length > p.Length)
                    {
                        char nxt = t[p.Length];
                        if (nxt == '：' || nxt == ':' || nxt == ' ' || nxt == '　') return true;
                    }
                }
            }
            return false;
        }

        // ───────────────────────────── 每帧更新 ─────────────────────────────

        /// <summary>
        /// 是否有主相机（= 处于可放 3D 歌词的游戏场景）。
        /// 之前还要求「光标锁定 + 不可见」，太严格：PEAK 大厅/部分界面光标可见时，
        /// 歌词会被整段 Hide，用户体感就是「歌词没了」。改成只要有主相机就放。
        /// </summary>
        private bool InWorldScene
        {
            get
            {
                var cam = Camera.main;
                if (cam == null) return false;
                return cam.enabled && cam.gameObject.activeInHierarchy;
            }
        }

        // 本地模式（跟着本机网易云/QQ）自己的歌词与歌名，和在线播放的那套分开存
        private string _extKey = "";
        private string _extLrc = "";          // 上次喂给 3D 层的 lrc 原文
        private int _extCount = -1;           // 上次的行数（只在变化时打日志）
        private float _extProbeAt;            // 同步探针的节流
        private float _extDriftSince = -1f;   // 3D 层和源不一致的起始时刻
        private readonly List<LrcLine> _extLines = new List<LrcLine>();
        private float _ownIdleSince;

        private void LateUpdate()
        {
            if (_player == null) _player = MusicPlayer.Active;
            if (_world == null) return;

            if (TryLocalFollow()) return;

            // 从本地模式切回在线：把我们自己的歌词重新挂回去
            if (_extKey.Length > 0)
            {
                _extKey = "";
                _extLines.Clear();
                _world.SetLyrics(_song != null ? _song.Key : "", _lines);
            }

            if (_player == null) return;

            // 换歌清空
            if (_player.Current != null && _song != null && _player.Current.Key != _song.Key) Clear();

            if (!Plugin.LyricEnabled.Value || !_player.HasClip || _lines.Count == 0 || !InWorldScene)
            {
                _world.Hide();
                return;
            }

            float t = _player.Time;
            _world.Tick(_player, Camera.main, t);
        }

        /// <summary>
        /// 本地模式：外部播放器（网易云/QQ）在放歌时，用 MusicListsGUI 的进度 + 歌词驱动同一套 3D 歌词。
        /// 只在「我们自己的播放器没在放、也没有正在取歌」时才接管，避免两套时间轴打架。
        /// </summary>
        private bool TryLocalFollow()
        {
            var b = LocalBridge.Instance;
            bool modeOn = b != null && b.Alive && MusicPanel.Active != null && MusicPanel.Active.LocalMode;
            if (!modeOn) return false;

            bool ours = _player != null && (_player.HasClip || MusicPanel.Active.IsBusy);
            if (ours) { _ownIdleSince = Time.unscaledTime; return false; }
            if (Time.unscaledTime - _ownIdleSince < 1.5f) return false;   // 刚停下，缓一拍再接管

            string key = b.SongKey;
            if (string.IsNullOrEmpty(key)) return false;

            // 重建条件：换歌 **或者歌词内容变了**。
            // 她的 lyricsLines 是异步补全的 —— 实测同一首《时光背面的我》，
            // 23:24 的接口只有 34 行（还带 12 行制作署名），23:31 已经是 70 行。
            // 以前只判断歌名，于是 3D 层永远停在最早那份残缺歌词上：行号整体错位，
            // 屏幕上就出现"灵动岛和她的面板都对、游戏里却在唱别的句子"。
            string raw = b.Lrc ?? "";
            bool songChanged = _extKey != key;
            // 先比引用：桥里的歌词字符串在两次轮询之间是同一个实例，
            // 不然每帧都要把整篇 LRC（几 KB）比对一遍
            if (songChanged
                || (!ReferenceEquals(_extLrc, raw) && !string.Equals(_extLrc, raw, StringComparison.Ordinal)))
            {
                _extKey = key;
                _extLrc = raw;
                _extLines.Clear();
                FillLines(_extLines, raw);
                _world.SetLyrics(key, _extLines);

                if (songChanged || _extLines.Count != _extCount)
                {
                    _extCount = _extLines.Count;
                    Plugin.Log.LogInfo(Tag + " 本地模式歌词：" + b.Title + " - " + b.Artist
                                       + " · " + _extLines.Count + " 行 · 端口 " + b.Port
                                       + (songChanged ? "" : "（内容更新）"));
                }
            }

            if (!Plugin.LyricEnabled.Value || _extLines.Count == 0 || !InWorldScene)
            {
                _world.Hide();
                return true;
            }

            _world.Tick(key, Camera.main, (float)(b.PositionMsNow / 1000.0));

            // 探针必须放在 Tick **之后**：Tick 里才会把本帧应该显示的那句写进 LastLineText。
            // 放在前面就永远在拿「上一帧的文字」和「这一帧的进度」比 —— 每次换行都会误报一条
            // 「本地歌词不同步」，而自愈又因为只差一帧、2 秒内自己恢复了，永远不会触发。
            string srcNow = b.LineTextAt(b.PositionMsNow);
            if (!string.IsNullOrEmpty(srcNow)
                && !string.Equals(srcNow, WorldLyricRenderer.LastLineText, StringComparison.Ordinal))
            {
                if (_extDriftSince < 0f) _extDriftSince = Time.unscaledTime;
                else if (Time.unscaledTime - _extDriftSince > 2f)
                {
                    _extDriftSince = -1f;
                    _extLines.Clear();
                    FillLines(_extLines, raw);
                    _world.SetLyrics(key, _extLines);
                    _extCount = _extLines.Count;
                    Plugin.Log.LogWarning(Tag + " 本地歌词漂移 2s，已按最新歌词重建（"
                                          + _extLines.Count + " 行）· 期望「" + srcNow + "」");
                }
            }
            else _extDriftSince = -1f;

            if (Time.unscaledTime >= _extProbeAt)
            {
                _extProbeAt = Time.unscaledTime + 3f;
                double pms = b.PositionMsNow;
                string src = b.LineTextAt(pms);
                string shown = WorldLyricRenderer.LastLineText;
                if (!string.IsNullOrEmpty(src) && !string.Equals(src, shown, StringComparison.Ordinal))
                    Plugin.Log.LogWarning(Tag + " 本地歌词不同步：3D「" + shown + "」/ 源「" + src
                                          + "」 pos=" + (pms / 1000.0).ToString("F1") + "s");
            }

            return true;
        }
    }
}
