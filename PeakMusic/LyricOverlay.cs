using System;
using System.Collections.Generic;
using PeakMusic.Core;
using PeakMusic.Core.Netease;
using UnityEngine;

namespace PeakMusic
{
    /// <summary>
    /// 实时歌词宿主：世界空间 3D 歌词（TextMeshPro SDF，移植自 PeakDangoMusic）。
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

        // 世界空间 3D 歌词（移植自 PeakDangoMusic）。没有主相机（菜单/加载）时它自然不画。
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
            "吉他", "贝斯", "键盘", "弦乐", "鼓", "统筹", "策划", "出品", "发行", "词", "曲",
            "OP", "SP", "Lyrics", "Composed", "Arranged", "Produced", "Mixed", "Mastered",
        };

        private static bool IsMetaLine(string text)
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

        private void LateUpdate()
        {
            if (_player == null) _player = MusicPlayer.Active;
            if (_player == null) return;

            // 换歌清空
            if (_player.Current != null && _song != null && _player.Current.Key != _song.Key) Clear();

            if (_world == null) return;

            if (!Plugin.LyricEnabled.Value || !_player.HasClip || _lines.Count == 0 || !InWorldScene)
            {
                _world.Hide();
                return;
            }

            float t = _player.Time;
            _world.Tick(_player, Camera.main, t);
        }
    }
}
