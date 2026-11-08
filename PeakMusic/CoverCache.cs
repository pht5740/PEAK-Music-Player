using System.Collections;
using System.Collections.Generic;
using PeakMusic.Core.Netease;
using UnityEngine;
using UnityEngine.Networking;

namespace PeakMusic
{
    /// <summary>
    /// 专辑封面缓存：按 URL 拉小图（<c>?param=96y96</c>，单张几 KB），
    /// 缩放成 64×64 后**烘进圆角 alpha**，因为 IMGUI 的 GUI.DrawTexture 不支持遮罩，
    /// 只能先把圆角烤进贴图里，之后当普通贴图画就行。
    ///
    /// 三条约束：
    ///   1) 主线程只调 Get()，它要么立刻返回贴图、要么返回 null 并丢一个下载任务，绝不阻塞；
    ///   2) 并发最多 3 个，一次滚出 8 行不会瞬间打 8 个请求；
    ///   3) LRU 上限 64 张，超出就 Destroy 最旧的，翻几十页也不会漏显存。
    /// </summary>
    internal sealed class CoverCache : MonoBehaviour
    {
        private const int DstSize = 64;
        private const int DstRadius = 9;
        private const int MaxEntries = 64;
        private const int MaxConcurrent = 3;

        private readonly Dictionary<string, Texture2D> _done = new Dictionary<string, Texture2D>();
        private readonly List<string> _lru = new List<string>();          // 0 = 最旧
        private readonly HashSet<string> _inflight = new HashSet<string>();
        private readonly HashSet<string> _failed = new HashSet<string>(); // 失败过的别再每帧重试
        private readonly List<string> _queue = new List<string>();
        private int _active;

        /// <summary>就绪则返回贴图；未就绪则排队下载并返回 null。调用方直接跳过绘制即可。</summary>
        public Texture2D Get(string rawUrl)
        {
            string url = Normalize(rawUrl);
            if (url == null) return null;

            Texture2D tex;
            if (_done.TryGetValue(url, out tex))
            {
                Touch(url);
                return tex;
            }
            if (_inflight.Contains(url)) return null;
            if (_failed.Contains(url)) return null;

            _inflight.Add(url);
            _queue.Add(url);
            Pump();
            return null;
        }

        private void Pump()
        {
            while (_active < MaxConcurrent && _queue.Count > 0)
            {
                string url = _queue[0];
                _queue.RemoveAt(0);
                _active++;
                StartCoroutine(Load(url));
            }
        }

        /// <summary>统一成小图 https 直链。netEase 的封面 CDN 与音频同一个域族，同样只认 https。</summary>
        private static string Normalize(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            string u = raw;
            int q = u.IndexOf('?');
            if (q > 0) u = u.Substring(0, q);              // 丢掉原有的 resize / 签名参数
            u = NeteaseClient.ForceHttps(u);
            return u + "?param=96y96";
        }

        private IEnumerator Load(string url)
        {
            UnityWebRequest req = null;
            UnityWebRequestAsyncOperation op = null;

            // 注意：yield 不能写在带 catch 的 try 里（CS1626），所以 SendWebRequest
            // 单独用 try 只取返回值，yield 留在外面。否则 URL 非法时协程会直接死掉，
            // _inflight / _active 永远不归零，后面所有封面都卡住。
            try
            {
                req = UnityWebRequestTexture.GetTexture(url, false);
                req.timeout = 12;
                op = req.SendWebRequest();
            }
            catch
            {
                op = null;
            }

            if (op != null) yield return op;

            Texture2D src = null;
            try
            {
                if (op != null && req.result == UnityWebRequest.Result.Success)
                    src = DownloadHandlerTexture.GetContent(req);
            }
            catch { /* 封面拉失败不影响听歌，静默跳过 */ }

            Texture2D small = src != null ? Bake(src) : null;
            if (small != null) Store(url, small);
            else if (_failed.Count < 512) _failed.Add(url);   // 失败/解码不了：记下来，别每帧重试

            if (src != null) { try { Destroy(src); } catch { } }
            if (req != null) { try { req.Dispose(); } catch { } }

            _inflight.Remove(url);
            _active--;
            Pump();
        }

        /// <summary>缩放到 64×64 并把圆角烤进 alpha。源图一般 96×96，最近邻足够。</summary>
        private Texture2D Bake(Texture2D src)
        {
            Texture2D dst = null;
            try
            {
                Color32[] sp = src.GetPixels32();
                int sw = src.width, sh = src.height;
                if (sp == null || sw <= 0 || sh <= 0) return null;

                var dp = new Color32[DstSize * DstSize];
                float half = DstSize * 0.5f;
                for (int y = 0; y < DstSize; y++)
                {
                    int sy = Mathf.Clamp(y * sh / DstSize, 0, sh - 1);
                    for (int x = 0; x < DstSize; x++)
                    {
                        int sx = Mathf.Clamp(x * sw / DstSize, 0, sw - 1);
                        Color32 c = sp[sy * sw + sx];

                        // 圆角 SDF（与面板圆角同一套算法），只在角上削 alpha
                        float dx = Mathf.Abs(x + 0.5f - half) - (half - DstRadius);
                        float dy = Mathf.Abs(y + 0.5f - half) - (half - DstRadius);
                        float ox = Mathf.Max(dx, 0f), oy = Mathf.Max(dy, 0f);
                        float d = Mathf.Sqrt(ox * ox + oy * oy) + Mathf.Min(Mathf.Max(dx, dy), 0f) - DstRadius;
                        float cover = Mathf.Clamp01(0.5f - d);

                        c.a = (byte)(c.a * cover);
                        dp[y * DstSize + x] = c;
                    }
                }

                dst = new Texture2D(DstSize, DstSize, TextureFormat.RGBA32, false);
                dst.wrapMode = TextureWrapMode.Clamp;
                dst.filterMode = FilterMode.Bilinear;
                dst.hideFlags = HideFlags.DontSave;
                dst.SetPixels32(dp);
                dst.Apply(false, false);
                return dst;
            }
            catch
            {
                if (dst != null) { try { Destroy(dst); } catch { } }
                return null;
            }
        }

        private void Store(string url, Texture2D tex)
        {
            _done[url] = tex;
            _lru.Add(url);

            while (_lru.Count > MaxEntries)
            {
                string old = _lru[0];
                _lru.RemoveAt(0);
                Texture2D t;
                if (_done.TryGetValue(old, out t))
                {
                    _done.Remove(old);
                    if (t != null) { try { Destroy(t); } catch { } }
                }
            }
        }

        private void Touch(string url)
        {
            int i = _lru.IndexOf(url);
            if (i < 0 || i == _lru.Count - 1) return;
            _lru.RemoveAt(i);
            _lru.Add(url);
        }

        private void OnDestroy()
        {
            foreach (var kv in _done)
                if (kv.Value != null) { try { Destroy(kv.Value); } catch { } }
            _done.Clear();
            _lru.Clear();
            _queue.Clear();
            _inflight.Clear();
        }
    }
}
