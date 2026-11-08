using System;
using System.Collections;
using BepInEx.Logging;
using PeakMusic.Core.Netease;
using UnityEngine;
using UnityEngine.Networking;

namespace PeakMusic
{
    /// <summary>
    /// 真正的播放器：一条 AudioSource + 一个 UnityWebRequest，负责把 mp3 拉下来放到扬声器里。
    ///
    /// 三条从阶段 1 实测得来的硬规则，不要"优化"掉：
    ///   1) streamAudio = true 时请求对象不能提前 Dispose，否则底层缓冲被收走，声音当场断；
    ///   2) streamAudio = true 时远距 seek 不落位（实测 +500ms 仍在 0.00s），
    ///      所以「从第 N 秒开始播」必须先整曲下完（streamAudio = false）再设 time；
    ///   3) 播放地址带签名有时效，每次播放现取，绝不缓存 URL；要循环就循环 AudioClip。
    /// </summary>
    internal sealed class MusicPlayer : MonoBehaviour
    {
        private const string Tag = "[播放器]";

        private AudioSource _src;
        private AudioClip _clip;
        private UnityWebRequest _req;
        private Coroutine _loading;
        private float _lastUnload = -100f;

        private static ManualLogSource Log { get { return Plugin.Log; } }

        /// <summary>
        /// 当前活着的播放器实例。歌词层用它读播放进度——
        /// 这样歌词不需要知道面板存在，面板也不需要知道歌词存在，两边都只依赖播放器。
        /// </summary>
        public static MusicPlayer Active { get; private set; }

        public SongInfo Current { get; private set; }
        public bool Loading { get; private set; }
        public string Status = "未播放";
        public float Volume { get; private set; }

        public bool HasClip { get { return _clip != null; } }
        public bool IsPlaying { get { return _src != null && _src.isPlaying; } }
        public float Time { get { return _clip != null && _src != null ? _src.time : 0f; } }
        public float Length { get { return _clip != null ? _clip.length : 0f; } }

        private void Awake()
        {
            Active = this;
            _src = gameObject.AddComponent<AudioSource>();
            _src.playOnAwake = false;
            _src.loop = false;   // 不单曲循环：播完由面板接下一首
            _src.spatialBlend = 0f;             // 2D，不吃位置衰减
            _src.bypassReverbZones = true;
            Volume = Mathf.Clamp01(Plugin.Volume.Value);
            _src.volume = Volume;
        }

        public void SetVolume(float v)
        {
            Volume = Mathf.Clamp01(v);
            if (_src != null) _src.volume = Volume;
            Plugin.Volume.Value = Volume;
        }

        /// <summary>开始播放。startAt &gt; 0 时会走整曲下载（见类注释第 2 条）。</summary>
        public void Play(SongInfo song, string url, float startAt = 0f)
        {
            if (string.IsNullOrEmpty(url))
            {
                Status = "播放地址为空";
                return;
            }
            if (_loading != null) StopCoroutine(_loading);
            _loading = StartCoroutine(LoadAndPlay(song, url, startAt));
        }

        private IEnumerator LoadAndPlay(SongInfo song, string url, float startAt)
        {
            Loading = true;
            Current = song;
            Status = "加载中：" + (song != null ? song.Name : url);

            yield return StopCore();

            // 网易 CDN 给的是 http://，Unity 6 默认禁止非安全连接，会在
            // SendWebRequest() 里同步抛异常。这里兜一道，别指望调用方总记得升级协议。
            string safeUrl = NeteaseClient.ForceHttps(url);
            if (!string.Equals(safeUrl, url, StringComparison.OrdinalIgnoreCase))
            {
                Log.LogInfo(Tag + " 直链协议 http→https：" + ShortenUrl(safeUrl));
                url = safeUrl;
            }

            bool needFull = startAt > 0.5f;
            var req = UnityWebRequestMultimedia.GetAudioClip(url, AudioType.MPEG);
            var dh = req.downloadHandler as DownloadHandlerAudioClip;
            if (dh != null) dh.streamAudio = !needFull;

            int timeout = Plugin.TimeoutSeconds.Value;
            req.timeout = timeout > 0 ? timeout : 0;

            UnityWebRequestAsyncOperation op = null;
            try
            {
                op = req.SendWebRequest();
            }
            catch (Exception e)
            {
                // 不接住的话协程直接被终止，Loading 永远停在 true，面板一直显示「加载中」，
                // 看起来就是「点了没反应」——这正是 2026-11-07 那次「点歌不出声」的真凶。
                Status = "加载失败：" + e.Message;
                Log.LogError(Tag + " 发起请求就失败：" + e.GetType().Name + ": " + e.Message +
                             " url=" + ShortenUrl(url));
                try { req.Dispose(); } catch { }
                Loading = false;
                _loading = null;
                yield break;
            }
            int lastPct = -10;
            // 注意别写 Time.unscaledTime：本类有个 Time 属性（当前播放秒数），会把它遮住
            float startedAt = UnityEngine.Time.unscaledTime;

            // 流式播放要"先攒一段再开声"：网易 CDN 的抖动会让解码端断粮，听起来就是
            // 音乐一顿一顿的。这里先攒够 ~32 秒（512KB @128kbps）再起播；链路太慢就
            // 最多等 10 秒，然后照旧边下边播 —— 宁可卡也不能点了半天没声音。
            const long PrefillBytes = 512 * 1024;
            const float PrefillWait = 10f;

            while (!op.isDone)
            {
                float p = req.downloadProgress;
                int pct = p < 0f ? -1 : Mathf.Clamp(Mathf.RoundToInt(p * 100f), 0, 100);
                Status = pct < 0
                    ? "缓冲中 " + (req.downloadedBytes / 1024) + " KB"
                    : "缓冲 " + pct + "% " + (req.downloadedBytes / 1024) + " KB";
                if (pct >= 0 && pct - lastPct >= 25)
                {
                    lastPct = pct;
                    Log.LogInfo(Tag + " 缓冲 " + pct + "% " + (req.downloadedBytes / 1024) + "KB");
                }

                if (!needFull)
                {
                    bool enough = req.downloadedBytes >= PrefillBytes;
                    bool tooSlow = UnityEngine.Time.unscaledTime - startedAt > PrefillWait;
                    // 攒够了 / 等太久了：退出等待，直接用已经到手的那部分起播。
                    // 这时请求还在飞 —— 流式模式本来就该这样。
                    if (enough || tooSlow)
                    {
                        if (enough)
                            Log.LogInfo(Tag + " 预缓冲 " + (req.downloadedBytes / 1024) + "KB 就绪，等下载完成后起播");
                        break;
                    }
                }
                yield return null;
            }

            // 预缓冲提前退出时请求还没结束，result 仍是 InProgress —— 这不是失败。
            bool partial = !op.isDone;
            if (!partial && req.result != UnityWebRequest.Result.Success)
            {
                Status = "加载失败：" + req.error + " (code=" + req.responseCode + ")";
                Log.LogError(Tag + " 加载失败 code=" + req.responseCode + " err=" + req.error + " url=" + ShortenUrl(url));
                req.Dispose();
                Loading = false;
                _loading = null;
                yield break;
            }

            AudioClip clip = null;
            if (partial)
            {
                // r39：撤掉 r38 的「中途取 handler.audioClip 提前起播」——Unity 6000.3 的
                // DownloadHandlerAudioClip 在下载中途被读 audioClip 会把整个流式解码打成
                // Data Processing Error（属性返回 null，请求收尾必失败），结果就是
                // 100% 无声。r37 及之前从不碰它、整曲下完再 GetContent，一直稳定出声。
                // 提前起播想真做得到要自备 MP3 解码器（如 NLayer），以后再说。
                while (!op.isDone) yield return null;
                if (req.result != UnityWebRequest.Result.Success)
                {
                    Status = "加载失败：" + req.error + " (code=" + req.responseCode + ")";
                    Log.LogError(Tag + " 加载失败 code=" + req.responseCode + " err=" + req.error);
                    req.Dispose();
                    Loading = false;
                    _loading = null;
                    yield break;
                }
            }

            if (clip == null) clip = DownloadHandlerAudioClip.GetContent(req);

            if (clip == null)
            {
                Status = "解码失败（GetContent 返回 null）";
                Log.LogError(Tag + " GetContent 为 null —— audioType 或编码不匹配");
                req.Dispose();
                Loading = false;
                _loading = null;
                yield break;
            }

            _clip = clip;
            _req = req;                 // 流式期间必须活着
            _src.clip = clip;
            _src.volume = Volume;
            _src.priority = 0;          // 混音里优先，别被游戏音效挤掉
            _src.Play();

            if (needFull && clip.length > 1f)
            {
                float target = Mathf.Clamp(startAt, 0f, clip.length - 1f);
                _src.time = target;
                Log.LogInfo(Tag + " 从 " + target.ToString("F1") + "s 起播（整曲下载模式）");
            }

            Status = "播放中：" + (song != null ? song.Name : "?");
            Log.LogInfo(Tag + " 起播 " + (song != null ? song.Name : "?") +
                        " len=" + clip.length.ToString("F2") + "s freq=" + clip.frequency +
                        " ch=" + clip.channels + " stream=" + !needFull);

            Loading = false;
            _loading = null;
        }

        public void TogglePause()
        {
            if (_clip == null || _src == null)
            {
                Status = "还没有正在播放的歌";
                return;
            }

            if (_src.isPlaying)
            {
                _src.Pause();
                Status = "已暂停";
            }
            else
            {
                _src.UnPause();
                Status = "播放中";
            }
        }

        public void Stop()
        {
            if (_loading != null) { StopCoroutine(_loading); _loading = null; }
            StartCoroutine(StopAndReport());
        }

        private IEnumerator StopAndReport()
        {
            Loading = true;
            yield return StopCore();
            Status = "已停止";
            Loading = false;
        }

        /// <summary>顺序不能错：Stop → clip=null → 请求 Abort/Dispose → Destroy(clip) → 等一帧 → 卸载。</summary>
        private IEnumerator StopCore()
        {
            if (_src != null)
            {
                _src.Stop();
                _src.clip = null;
            }

            if (_req != null)
            {
                try { _req.Abort(); } catch { }
                try { _req.Dispose(); } catch { }
                _req = null;
            }

            if (_clip != null)
            {
                Destroy(_clip);
                _clip = null;
            }

            yield return null;

            // Resources.UnloadUnusedAssets 是一次全量扫描，装了几十个 mod 的游戏里
            // 动辄几百毫秒，连续切歌时那就是肉眼可见的"卡一下"。真正的音频内存
            // Destroy(_clip) 已经放掉了，这里只做低频兜底清理。
            if (UnityEngine.Time.unscaledTime - _lastUnload > 30f)
            {
                _lastUnload = UnityEngine.Time.unscaledTime;
                yield return Resources.UnloadUnusedAssets();
            }
        }

        private static string ShortenUrl(string u)
        {
            if (string.IsNullOrEmpty(u)) return "(空)";
            int q = u.IndexOf('?');
            return q > 0 ? u.Substring(0, q) + "?…" : u;
        }

        private void OnDestroy()
        {
            if (Active == this) Active = null;
            try
            {
                if (_src != null) { _src.Stop(); _src.clip = null; }
                if (_req != null) { _req.Abort(); _req.Dispose(); _req = null; }
                if (_clip != null) Destroy(_clip);
                Log.LogInfo(Tag + " 已销毁，资源已释放。");
            }
            catch (Exception e)
            {
                Log.LogWarning(Tag + " 销毁时异常：" + e.Message);
            }
        }
    }
}
