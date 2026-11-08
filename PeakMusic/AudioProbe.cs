using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Networking;

namespace PeakMusic
{
    /// <summary>
    /// 阶段 1 探针。三条必须记录的数据都在这里产出：
    ///   起播延迟、seek 是否成立、反复切歌后 AudioClip 会不会涨。
    ///
    /// 两个已踩过的坑，代码里已经处理：
    ///   1) streamAudio = true 时，UnityWebRequest 一旦 Dispose，底层流缓冲就没了，
    ///      声音立刻断。所以 _activeRequest 必须活到停播为止，不能 GetContent 完就释放。
    ///   2) 旧版 UnityEngine.Input 在只启用新输入系统时会抛 InvalidOperationException。
    ///      PEAK 自带 Unity.InputSystem.dll，这里优先走新系统，旧 Input 只做兜底。
    /// </summary>
    internal sealed class AudioProbe : MonoBehaviour
    {
        private const string Tag = "[探针]";

        private AudioSource _src;
        private AudioClip _clip;
        private UnityWebRequest _activeRequest;
        private readonly List<string> _urls = new List<string>();

        private bool _paused;
        private bool _busy;
        private bool _showGui = true;
        private bool _inputSystemOk = true;
        private bool _legacyInputOk = true;
        private bool _inputBackendLogged;

        private void LogInputBackendOnce(string backend)
        {
            if (_inputBackendLogged) return;
            _inputBackendLogged = true;
            Log.LogInfo(Tag + " 热键实际走的是： " + backend + " —— 这个结论要写进 notes.md");
        }
        private string _status = "空闲 · F7 开始播放";
        private long _lastMs = -1;

        private GUIStyle _style;
        private bool _styleReady;

        private static ManualLogSource Log
        {
            get { return Plugin.Log; }
        }

        private void Start()
        {
            _src = gameObject.AddComponent<AudioSource>();
            _src.playOnAwake = false;
            _src.loop = true;
            _src.spatialBlend = 0f;          // 2D，不受位置衰减影响
            _src.volume = Plugin.Volume.Value;
            _src.bypassReverbZones = true;

            _urls.Clear();
            _urls.Add(Plugin.UrlA.Value);
            _urls.Add(Plugin.UrlB.Value);

            Log.LogInfo(Tag + " AudioSource 就绪。spatialBlend=0 loop=true volume=" + _src.volume);
            Log.LogInfo(Tag + " 当前 AudioClip 计数（含游戏自带）= " + CountClips());
        }

        // ───────────────────────────── 热键 ─────────────────────────────

        private void Update()
        {
            if (KeyPressed(KeyCode.F7, UnityEngine.InputSystem.Key.F7, "F7"))
            {
                if (!_busy) StartCoroutine(PlayCoroutine(Plugin.UrlA.Value, "A"));
            }
            else if (KeyPressed(KeyCode.F8, UnityEngine.InputSystem.Key.F8, "F8"))
            {
                if (!_busy) StartCoroutine(SeekCoroutine());
            }
            else if (KeyPressed(KeyCode.F9, UnityEngine.InputSystem.Key.F9, "F9"))
            {
                TogglePause();
            }
            else if (KeyPressed(KeyCode.F10, UnityEngine.InputSystem.Key.F10, "F10"))
            {
                if (!_busy) StartCoroutine(StressCoroutine(20));
            }
            else if (KeyPressed(KeyCode.F11, UnityEngine.InputSystem.Key.F11, "F11"))
            {
                if (!_busy) StartCoroutine(StopCoroutineSafe());
            }
            else if (KeyPressed(KeyCode.F6, UnityEngine.InputSystem.Key.F6, "F6"))
            {
                _showGui = !_showGui;
            }
            else if (KeyPressed(KeyCode.F5, UnityEngine.InputSystem.Key.F5, "F5"))
            {
                if (!_busy) StartCoroutine(SlowLinkCoroutine());
            }
        }

        private bool KeyPressed(KeyCode legacy, UnityEngine.InputSystem.Key modern, string name)
        {
            if (_inputSystemOk)
            {
                try
                {
                    var kb = UnityEngine.InputSystem.Keyboard.current;
                    if (kb != null)
                    {
                        var ctrl = kb[modern];
                        bool hit = ctrl != null && ctrl.wasPressedThisFrame;
                        if (hit) LogInputBackendOnce("Input System");
                        return hit;
                    }
                }
                catch (Exception e)
                {
                    _inputSystemOk = false;
                    Log.LogWarning(Tag + " 读 Input System 失败，回退旧版 Input：" + e.GetType().Name + " " + e.Message);
                }
            }

            if (!_legacyInputOk) return false;
            try
            {
                bool hit = Input.GetKeyDown(legacy);
                if (hit) LogInputBackendOnce("旧版 Input（InputSystem 拿不到键盘）");
                return hit;
            }
            catch (Exception e)
            {
                _legacyInputOk = false;
                Log.LogError(Tag + " 旧版 Input 也不可用（Active Input Handling 可能只开了新系统），" + name +
                             " 热键失效，需要用 InputSystem 的 action 方案：" + e.GetType().Name);
                return false;
            }
        }

        // ───────────────────────────── 播放 ─────────────────────────────

        private static void ApplyTimeout(UnityWebRequest req)
        {
            int t = Plugin.TimeoutSeconds.Value;
            req.timeout = t > 0 ? t : 0;     // 0 = 不限。别再用 25 秒了，慢链路必挂。
        }

        /// <summary>
        /// 不要直接 yield return SendWebRequest()——那样在下载完成前屏幕上是死的，
        /// 慢链路下看起来像"卡住"。这里每帧刷新缓冲百分比，日志每 10% 打一行。
        /// </summary>
        private IEnumerator SendWithProgress(UnityWebRequest req, string tag)
        {
            var op = req.SendWebRequest();
            int lastLogged = -10;

            while (!op.isDone)
            {
                float p = req.downloadProgress;                  // 未知长度时可能是 -1
                int pct = p < 0f ? -1 : Mathf.Clamp(Mathf.RoundToInt(p * 100f), 0, 100);
                long kb = (long)(req.downloadedBytes / 1024);

                _status = pct < 0 ? "缓冲中 " + kb + " KB" : "缓冲 " + pct + "%  " + kb + " KB";

                if (pct >= 0 && pct - lastLogged >= 10)
                {
                    lastLogged = pct;
                    Log.LogInfo(Tag + " 缓冲 " + pct + "% " + kb + "KB " + tag);
                }
                yield return null;
            }
        }

        private IEnumerator PlayCoroutine(string url, string tag)
        {
            _busy = true;
            _status = "准备播放 " + tag;

            // 探针和面板播放器各有一个 AudioSource，同时响就是两首歌叠在一起。
            // 探针默认不挂载（EnableProbe=false），万一开了也要互斥。
            MusicPlayer main = MusicPlayer.Active;
            if (main != null && main.HasClip) main.Stop();

            yield return StopCoroutineSafeCore();

            var sw = Stopwatch.StartNew();
            var req = UnityWebRequestMultimedia.GetAudioClip(url, AudioType.MPEG);

            var dh = req.downloadHandler as DownloadHandlerAudioClip;
            if (dh != null) dh.streamAudio = true;    // 秒开；如果 seek 不稳再改 false
            ApplyTimeout(req);

            yield return SendWithProgress(req, "[" + tag + "]");
            long netMs = sw.ElapsedMilliseconds;

            if (req.result != UnityWebRequest.Result.Success)
            {
                Log.LogError(Tag + " 起播失败 [" + tag + "] code=" + req.responseCode + " err=" + req.error + " 耗时=" + netMs + "ms");
                _status = "起播失败：" + req.error;
                req.Dispose();
                _busy = false;
                yield break;
            }

            AudioClip clip = DownloadHandlerAudioClip.GetContent(req);
            if (clip == null)
            {
                Log.LogError(Tag + " GetContent 返回 null，但请求成功了。audioType 或编码可能不匹配。");
                _status = "GetContent 为 null";
                req.Dispose();
                _busy = false;
                yield break;
            }

            _clip = clip;
            _activeRequest = req;        // 关键：流式播放期间不能 Dispose
            _src.clip = clip;
            _src.volume = Plugin.Volume.Value;
            _src.Play();

            sw.Stop();
            _lastMs = sw.ElapsedMilliseconds;
            _playCount++;

            Log.LogInfo(Tag + " 起播成功 [" + tag + "] 网络=" + netMs + "ms 出声=" + sw.ElapsedMilliseconds +
                        "ms len=" + clip.length.ToString("F2") + "s freq=" + clip.frequency +
                        " ch=" + clip.channels + " loadState=" + clip.loadState +
                        " isPlaying=" + _src.isPlaying + " clips=" + CountClips());

            if (clip.length <= 0.01f)
                Log.LogWarning(Tag + " clip.length≈0 —— 流式模式下常见，seek 会因此不确定，记一笔。");

            _status = "播放中 [" + tag + "] 起播 " + sw.ElapsedMilliseconds + "ms";
            _busy = false;
        }

        private int _playCount;

        private void TogglePause()
        {
            if (_src == null || _clip == null)
            {
                Log.LogWarning(Tag + " 还没播任何东西。");
                return;
            }

            if (_src.isPlaying)
            {
                _src.Pause();
                _paused = true;
                Log.LogInfo(Tag + " 已暂停，time=" + _src.time.ToString("F2"));
            }
            else
            {
                _src.UnPause();
                _paused = false;
                Log.LogInfo(Tag + " 已恢复，time=" + _src.time.ToString("F2"));
            }
            _status = _paused ? "已暂停" : "播放中";
        }

        private IEnumerator SeekCoroutine()
        {
            if (_src == null || _clip == null || !_src.isPlaying)
            {
                Log.LogWarning(Tag + " seek 需要先有正在播放的曲子（按 F7）。");
                yield break;
            }

            _busy = true;
            float target = Plugin.SeekSeconds.Value;
            float len = _clip.length;
            if (len > 5f && target > len - 5f) target = len - 5f;

            _src.time = target;
            float at0 = _src.time;

            yield return new WaitForSecondsRealtime(0.5f);
            float at500 = _src.time;

            yield return new WaitForSecondsRealtime(0.5f);
            float at1000 = _src.time;

            bool landed = at500 >= target - 0.5f;
            bool advancing = at1000 > at500;
            bool pass = landed && advancing;

            Log.LogInfo(Tag + " seek 测试 目标=" + target.ToString("F1") + "s 立即=" + at0.ToString("F2") +
                        "s +500ms=" + at500.ToString("F2") + "s +1000ms=" + at1000.ToString("F2") +
                        "s => " + (pass ? "PASS" : "FAIL"));

            if (!pass)
                Log.LogWarning(Tag + " seek 没落到位。streamAudio=true 时只能跳到已缓冲区间——" +
                                    "把 AudioProbe 里 streamAudio 改成 false 再测一次（代价是等整曲下完才播）。");

            _status = pass ? "seek PASS (" + at500.ToString("F1") + "s)" : "seek FAIL";
            _busy = false;
        }

        /// <summary>
        /// 限速链路验证。本机 125 MB/s，3.7 MB 的曲子 30 ms 就下完，
        /// 这时候"秒开"和"整曲下完再播"两种实现的表现完全一样，测不出 streamAudio 有没有生效。
        /// 所以另起一条 512 KB/s 的限速服务（serve.py 8766 512，整曲需约 7.2 s），三种判据：
        ///   1) 起播耗时远小于整曲下载时间 → 确实在边下边播；
        ///   2) clip.length 在流式未完成时可能是 0（Unity 已知行为）；
        ///   3) 往"已缓冲区间之外"seek 会不会落位 → 决定阶段 3 的"从第 N 秒开始播"要不要自己做 Range 重请求。
        /// </summary>
        private IEnumerator SlowLinkCoroutine()
        {
            _busy = true;
            _status = "慢链验证中";

            yield return StartCoroutine(PlayCoroutine(Plugin.UrlSlow.Value, "慢链"));
            if (_clip == null)
            {
                _status = "慢链起播失败";
                _busy = false;
                yield break;
            }

            float len = _clip.length;
            float far = len > 30f ? len - 15f : len * 0.8f;
            float at0 = _src.time;

            Log.LogInfo(Tag + " 慢链：len=" + len.ToString("F1") + "s 起播后 time=" + at0.ToString("F2") +
                        "s isPlaying=" + _src.isPlaying + "（512KB/s 下整曲约需 7.2s）");

            yield return new WaitForSecondsRealtime(3f);
            float at3 = _src.time;
            Log.LogInfo(Tag + " 慢链 3s 后 time=" + at3.ToString("F2") + "s；按 128kbps 估算，3s 内网络侧只下到约 1.5MB≈96s 音频，" +
                        "所以 seek 到 " + far.ToString("F0") + "s 已经超出已缓冲区间");

            _src.time = far;
            float j0 = _src.time;
            yield return new WaitForSecondsRealtime(0.5f);
            float j500 = _src.time;
            yield return new WaitForSecondsRealtime(0.5f);
            float j1000 = _src.time;

            bool landed = j500 >= far - 2f;
            bool advancing = j1000 > j500;
            Log.LogInfo(Tag + " 慢链远距 seek 目标=" + far.ToString("F0") + "s 立即=" + j0.ToString("F2") +
                        "s +500ms=" + j500.ToString("F2") + "s +1000ms=" + j1000.ToString("F2") +
                        "s => " + (landed && advancing ? "PASS" : "FAIL"));

            if (!landed)
                Log.LogWarning(Tag + " 流式下远距 seek 不落位 —— 阶段 3 的「从第 N 秒开始播」不能只靠 AudioSource.time，" +
                                    "必须用 Range 头重新请求（CDN 支持 206 才行）或者改整曲下载后 seek。这条要写进 notes.md。");

            _status = "慢链 seek " + (landed && advancing ? "PASS" : "FAIL");
            _busy = false;
        }

        private IEnumerator StressCoroutine(int times)
        {
            _busy = true;
            int before = CountClips();
            Log.LogInfo(Tag + " 换歌压测开始：" + times + " 次，每首整曲下载，起始 clips=" + before);

            for (int i = 0; i < times; i++)
            {
                string url = _urls[i % _urls.Count];
                yield return StopCoroutineSafeCore();

                var req = UnityWebRequestMultimedia.GetAudioClip(url, AudioType.MPEG);
                var dh = req.downloadHandler as DownloadHandlerAudioClip;
                if (dh != null) dh.streamAudio = true;
                ApplyTimeout(req);

                yield return SendWithProgress(req, "[压测 " + (i + 1) + "]");

                if (req.result != UnityWebRequest.Result.Success)
                {
                    Log.LogError(Tag + " 压测第 " + (i + 1) + " 次请求失败：" + req.error);
                    req.Dispose();
                    _status = "压测中断于第 " + (i + 1) + " 次";
                    _busy = false;
                    yield break;
                }

                _clip = DownloadHandlerAudioClip.GetContent(req);
                _activeRequest = req;
                _src.clip = _clip;
                _src.Play();

                _status = "压测 " + (i + 1) + "/" + times;
                Log.LogInfo(Tag + " 压测 " + (i + 1) + "/" + times + " ok clips=" + CountClips());

                yield return new WaitForSecondsRealtime(0.4f);
            }

            yield return Resources.UnloadUnusedAssets();

            int after = CountClips();
            Log.LogInfo(Tag + " 换歌压测结束：clips " + before + " → " + after +
                        (after - before <= 3 ? " （正常）" : " （在涨，清理逻辑要查）"));
            _status = "压测完成 clips=" + after;
            _busy = false;
        }

        private IEnumerator StopCoroutineSafe()
        {
            _busy = true;
            yield return StopCoroutineSafeCore();
            _status = "已停止并清理";
            _busy = false;
        }

        /// <summary>
        /// 清理顺序不能错：Stop → clip=null → Dispose 请求 → Destroy(clip) → 等一帧 → UnloadUnusedAssets。
        /// </summary>
        private IEnumerator StopCoroutineSafeCore()
        {
            if (_src != null)
            {
                _src.Stop();
                _src.clip = null;
            }

            if (_activeRequest != null)
            {
                try { _activeRequest.Abort(); } catch { /* 已经完成就 Abort 会抛，忽略 */ }
                try { _activeRequest.Dispose(); } catch { }
                _activeRequest = null;
            }

            if (_clip != null)
            {
                Destroy(_clip);
                _clip = null;
            }

            _paused = false;
            yield return null;
            yield return Resources.UnloadUnusedAssets();
        }

        private static int CountClips()
        {
            try
            {
                var all = Resources.FindObjectsOfTypeAll<AudioClip>();
                return all == null ? -1 : all.Length;
            }
            catch
            {
                return -1;
            }
        }

        // ───────────────────────────── 屏幕提示 ─────────────────────────────

        private void OnGUI()
        {
            if (!_showGui) return;

            if (!_styleReady)
            {
                _styleReady = true;
                try
                {
                    // IMGUI 默认字体没有中文字形，会显示方块。必须用系统字体。
                    var font = Font.CreateDynamicFontFromOSFont("Microsoft YaHei", 14);
                    _style = new GUIStyle(GUI.skin.label) { fontSize = 14, richText = false };
                    if (font != null) _style.font = font;
                }
                catch (Exception e)
                {
                    Log.LogWarning(Tag + " 加载中文字体失败，界面可能显示方块：" + e.Message);
                }
            }

            float w = 430f, h = 108f;
            var rect = new Rect(12f, 12f, w, h);
            GUI.Box(rect, GUIContent.none);
            GUI.Label(new Rect(rect.x + 10f, rect.y + 6f, w - 20f, 20f), "PeakMusic 阶段1 探针  " + _status, _style);
            GUI.Label(new Rect(rect.x + 10f, rect.y + 30f, w - 20f, 20f),
                "F7 播放   F8 seek   F9 暂停   F10 压测x20   F11 停止   F5 慢链", _style);
            GUI.Label(new Rect(rect.x + 10f, rect.y + 54f, w - 20f, 20f),
                (_lastMs >= 0 ? "上次起播 " + _lastMs + "ms   " : "") +
                "clips=" + CountClips() + "   vol=" + (_src != null ? _src.volume.ToString("F2") : "-"), _style);
            GUI.Label(new Rect(rect.x + 10f, rect.y + 78f, w - 20f, 20f),
                "playCount=" + _playCount + (_busy ? "   [忙]" : ""), _style);
        }

        private void OnDestroy()
        {
            try
            {
                if (_src != null) { _src.Stop(); _src.clip = null; }
                if (_activeRequest != null) { _activeRequest.Abort(); _activeRequest.Dispose(); _activeRequest = null; }
                if (_clip != null) Destroy(_clip);
                Log.LogInfo(Tag + " AudioProbe 已销毁，资源已释放。");
            }
            catch (Exception e)
            {
                Log.LogWarning(Tag + " 销毁清理时异常：" + e.Message);
            }
        }
    }
}
