using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PeakMusic.Core.Netease
{
    /// <summary>
    /// 网易云接口客户端。**只走明文 `/api/` 接口，不做 weapi 加密。**
    ///
    /// 2026-11 实测：`/api/cloudsearch/pc`（搜索）、`/api/song/lyric/v1`（歌词）、
    /// `/api/login/qrcode/unikey`（二维码）这些明文接口匿名就能调，
    /// 只有 `/api/song/enhance/player/url/v1`（播放地址）需要登录态。
    /// 所以原文档阶段 2.1 那套 AES-128-CBC ×2 + RSA 可以整段跳过——
    /// 少 200 行密码学代码、少一个必然出错的环节。
    ///
    /// 但必须留着兜底：哪天明文接口被关掉，就得回到 weapi，
    /// 那时 `PostJson` 里加一层 params/encSecKey 就行（见 docs\施工流程-修订.md 修正 9）。
    ///
    /// 所有请求都过 Throttle（串行 + 200–500ms 随机），这是防封号的核心。
    /// </summary>
    public sealed class NeteaseClient : IDisposable
    {
        public const string BaseUri = "https://music.163.com";

        public const string UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

        /// <summary>
        /// 把 http:// 的直链升成 https://。
        ///
        /// 为什么必须做：网易 CDN 回的直链是 `http://m804.music.126.net/...`，
        /// 而 Unity 6 的 Player Settings 默认关了 "Allow downloads over HTTP"，
        /// UnityWebRequest 一碰 http 就同步抛 InvalidOperationException:
        /// Insecure connection not allowed（2026-11-07 实机日志证实，见 LogOutput.log）。
        /// 实测 m*.music.126.net 同时开了 443，改 https 后 curl 返回 206 Partial Content /
        /// Content-Type: audio/mpeg，所以升协议是安全解，不需要改游戏设置。
        ///
        /// localhost / 127.0.0.1 保持原样：Unity 对本地地址放行，而且本机测试服务没证书。
        /// </summary>
        public static string ForceHttps(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return url;

            Uri u;
            if (!Uri.TryCreate(url, UriKind.Absolute, out u)) return url;
            if (u.IsLoopback) return url;                           // 127.0.0.1 / localhost
            if (u.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return url;

            return "https://" + url.Substring("http://".Length);
        }

        private readonly HttpClient _http;

        // r44：/api/ 请求专用——不带 CookieContainer，Cookie 头手动拼（与 QQ 取流修复同因：
        // Unity 的 Mono 在游戏里会把容器 cookie 发坏，fee=8 的歌一律 404、匿名反而能拿直链）。
        private readonly HttpClient _plainHttp;
        private readonly CookieContainer _cookies = new CookieContainer();
        private readonly Throttle _throttle;
        private readonly Uri _root = new Uri(BaseUri);
        private string _savedCookie;
        private bool _disposed;

        public NeteaseClient(Throttle throttle = null)
        {
            _throttle = throttle ?? new Throttle(200, 500);

            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                UseCookies = true,
                CookieContainer = _cookies,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };
            _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
            _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
            _http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://music.163.com/");
            // Origin 少了会让登录类接口更容易被当成"非网页环境"，登录链路上必须带
            _http.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://music.163.com");
            _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");

            // eapi 专用（UseCookies=false，Cookie 头手动带设备指纹，避免和容器打架）
            // 关自动重定向：r31 实测 200+空响应，怀疑被 302 重定向走了
            _eapiHttp = new HttpClient(new HttpClientHandler
            {
                UseCookies = false,
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            })
            {
                Timeout = TimeSpan.FromSeconds(20)
            };

            // r44：明文 /api/ 也走手动 Cookie 头（见 _plainHttp 字段注释）
            _plainHttp = new HttpClient(new HttpClientHandler
            {
                UseCookies = false,
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            })
            {
                Timeout = TimeSpan.FromSeconds(20)
            };
            _plainHttp.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
            _plainHttp.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://music.163.com/");
            _plainHttp.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://music.163.com");
            _plainHttp.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");

            // 把磁盘上的登录态灌回 CookieContainer
            LoadCookiesIntoContainer(CookieStore.Load());
            _savedCookie = CookieStore.Load();
        }

        /// <summary>当前登录态（"k=v; k=v"），可能为空串。</summary>
        public string CookieText
        {
            get { return HarvestCookies() ?? _savedCookie ?? ""; }
        }

        public bool HasLogin
        {
            get { return !string.IsNullOrEmpty(LookupCookie("MUSIC_U")); }
        }

        private string LookupCookie(string name)
        {
            var d = CookieStore.Parse(CookieText);
            string v;
            return d.TryGetValue(name, out v) ? v : null;
        }

        public void SetCookie(string cookieText)
        {
            LoadCookiesIntoContainer(cookieText);
            CookieStore.Save(CookieStore.Normalize(CookieStore.Parse(cookieText)));
            _savedCookie = CookieStore.Load();
        }

        public void Logout()
        {
            CookieStore.Clear();
            _savedCookie = "";
            try { foreach (Cookie c in _cookies.GetCookies(_root)) c.Expired = true; } catch { }
        }

        // ───────────────────────────── 底层收发 ─────────────────────────────

        private void LoadCookiesIntoContainer(string cookieText)
        {
            var d = CookieStore.Parse(cookieText);
            foreach (var kv in d)
            {
                try { _cookies.Add(_root, new Cookie(kv.Key, kv.Value) { Domain = ".music.163.com", Path = "/" }); }
                catch { /* 非法 cookie 名/值就跳过，别为一条坏 cookie 炸掉整个登录态 */ }
            }
        }

        /// <summary>把容器里当前的 MUSIC_U 等捞出来（登录成功后服务端会 Set-Cookie）。</summary>
        private string HarvestCookies()
        {
            try
            {
                var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (Cookie c in _cookies.GetCookies(_root))
                {
                    if (!string.IsNullOrEmpty(c.Value)) dict[c.Name] = c.Value;
                }
                string norm = CookieStore.Normalize(dict);
                if (string.IsNullOrEmpty(norm)) return null;

                if (norm != _savedCookie && CookieStore.Parse(norm).ContainsKey("MUSIC_U"))
                {
                    CookieStore.Save(norm);
                    _savedCookie = norm;
                }
                return norm;
            }
            catch
            {
                return null;
            }
        }

        // ───────────────────────── eapi（r30）─────────────────────────
        // 明文 /api/ 扫码接口会被 8821 风控拒（实测 2026-11-08）。
        // Muonium 的 RequestUtil/CryptoUtil 揭示正确姿势：eapi 加密 +
        // interfacepc.music.163.com + 完整设备指纹 cookie（os=pc 等）。

        private const string EapiKey = "e82ckenh8dichen8";
        public const string EapiBase = "https://interfacepc.music.163.com";
        public const string EapiUa =
            "Mozilla/5.0 (Windows NT 10.0; WOW64) AppleWebKit/537.36 (KHTML, like Gecko) Safari/537.36 Chrome/91.0.4472.164 NeteaseMusicDesktop/3.0.18.203152";

        private readonly HttpClient _eapiHttp;

        // r41：eapi 响应的 Set-Cookie 必须手动收——_eapiHttp UseCookies=false 没有容器，
        // 扫码登录 803 的 MUSIC_U 就是从这里丢的（r30~r40 症状：登录成功但永远匿名取流）。
        private string _lastEapiSetCookie = "";

        private void CaptureSetCookies(HttpResponseMessage resp)
        {
            try
            {
                IEnumerable<string> vals;
                if (!resp.Headers.TryGetValues("Set-Cookie", out vals)) return;
                var sb = new StringBuilder();
                foreach (string v in vals)
                {
                    int semi = v.IndexOf(';');
                    string pair = (semi >= 0 ? v.Substring(0, semi) : v).Trim();
                    int eq = pair.IndexOf('=');
                    if (eq <= 0) continue;
                    if (sb.Length > 0) sb.Append("; ");
                    sb.Append(pair);
                }
                if (sb.Length > 0) _lastEapiSetCookie = sb.ToString();
            }
            catch { /* 收不到别炸掉登录流程 */ }
        }

        private static readonly Random _rnd = new Random();

        private static string RandomHex(int len)
        {
            var sb = new StringBuilder(len);
            for (int i = 0; i < len; i++) sb.Append("0123456789abcdef"[_rnd.Next(16)]);
            return sb.ToString();
        }

        private static string Md5Hex(string s)
        {
            using (var md5 = MD5.Create())
            {
                var b = md5.ComputeHash(Encoding.UTF8.GetBytes(s));
                var sb = new StringBuilder(b.Length * 2);
                foreach (byte x in b) sb.Append(x.ToString("x2"));
                return sb.ToString();
            }
        }

        private static string AesEcbHex(string plain)
        {
            using (var aes = Aes.Create())
            {
                aes.Key = Encoding.UTF8.GetBytes(EapiKey);
                aes.Mode = CipherMode.ECB;
                aes.Padding = PaddingMode.PKCS7;
                using (ICryptoTransform enc = aes.CreateEncryptor())
                {
                    var data = Encoding.UTF8.GetBytes(plain);
                    var cipher = enc.TransformFinalBlock(data, 0, data.Length);
                    var sb = new StringBuilder(cipher.Length * 2);
                    foreach (byte x in cipher) sb.Append(x.ToString("x2"));
                    return sb.ToString();
                }
            }
        }

        private static string Esc(string s)
        {
            return s == null ? "" : s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        /// <summary>设备指纹 header（eapi 请求体里的 header 字段 + Cookie 头共用）。</summary>
        private string DeviceHeaderJson(Dictionary<string, string> flat)
        {
            string deviceId = RandomHex(26).ToUpperInvariant() + "\\r";
            string csrf = LookupCookie("__csrf") ?? "";
            flat["osver"] = "Microsoft-Windows-11-Professional-build-26100-64bit";
            flat["deviceId"] = deviceId;
            flat["os"] = "pc";
            flat["appver"] = "3.1.12.204072";
            flat["versioncode"] = "140";
            flat["mobilename"] = "";
            flat["buildver"] = ((long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds).ToString();
            flat["resolution"] = "1920x1080";
            flat["__csrf"] = csrf;
            flat["channel"] = "netease";
            flat["requestId"] = ((long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds).ToString()
                                + "_" + _rnd.Next(1000).ToString("D4");
            if (HasLogin) flat["MUSIC_U"] = LookupCookie("MUSIC_U") ?? "";

            var sb = new StringBuilder();
            sb.Append('{');
            bool first = true;
            foreach (var kv in flat)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append('"').Append(Esc(kv.Key)).Append("\":\"").Append(Esc(kv.Value)).Append('"');
            }
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>
        /// eapi 加密调用（r30）。unikey / client/login 走这条，绕 8821 风控。
        /// 请求体 = params=AES-ECB(url-36cd479b6b5-json-36cd479b6b5-md5)，响应是明文 JSON。
        /// </summary>
        private async Task<Dictionary<string, object>> EapiCallAsync(
            string apiPath, Dictionary<string, string> data, CancellationToken ct)
        {
            string url = EapiBase + "/eapi/" + apiPath.Substring(5);   // "/api/x/y" → "x/y"

            var headerFlat = new Dictionary<string, string>();
            string headerJson = DeviceHeaderJson(headerFlat);

            var payloadSb = new StringBuilder();
            payloadSb.Append('{');
            bool first = true;
            foreach (var kv in data)
            {
                if (!first) payloadSb.Append(',');
                first = false;
                payloadSb.Append('"').Append(Esc(kv.Key)).Append("\":");
                long n;
                payloadSb.Append(long.TryParse(kv.Value, out n) ? kv.Value
                    : "\"" + Esc(kv.Value) + "\"");
            }
            payloadSb.Append(",\"header\":").Append(headerJson);
            payloadSb.Append(",\"e_r\":false}");
            string payload = payloadSb.ToString();

            string message = "nobody" + apiPath + "use" + payload + "md5forencrypt";
            string digest = Md5Hex(message);
            string dataStr = apiPath + "-36cd479b6b5-" + payload + "-36cd479b6b5-" + digest;
            string encParams = AesEcbHex(dataStr);

            // Cookie 头 = 设备指纹字段拍平（对照 Muonium：headers.put("Cookie", cookieMapToString(header))）
            var cookieSb = new StringBuilder();
            foreach (var kv in headerFlat)
            {
                if (cookieSb.Length > 0) cookieSb.Append("; ");
                cookieSb.Append(Uri.EscapeDataString(kv.Key)).Append('=')
                        .Append(Uri.EscapeDataString(kv.Value));
            }

            string body = await _throttle.RunAsync(async () =>
            {
                using (var req = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    req.Content = new FormUrlEncodedContent(new[]
                        { new KeyValuePair<string, string>("params", encParams) });
                    req.Headers.TryAddWithoutValidation("User-Agent", EapiUa);
                    req.Headers.TryAddWithoutValidation("Cookie", cookieSb.ToString());

                    using (var resp = await _eapiHttp.SendAsync(req, ct).ConfigureAwait(false))
                    {
                        // handler 已开自动解压（gzip/deflate），这里读出来就是明文
                        string text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        CaptureSetCookies(resp);   // r41：手动收 Set-Cookie（UseCookies=false 不会自动收）
                        string diag = "eapi resp: HTTP " + (int)resp.StatusCode
                            + " enc=" + (resp.Content.Headers.ContentEncoding != null
                                ? string.Join(",", resp.Content.Headers.ContentEncoding) : "-")
                            + " len=" + text.Length
                            + " type=" + resp.Content.Headers.ContentType
                            + " loc=" + resp.Headers.Location;
                        if ((int)resp.StatusCode != 200)
                            throw new InvalidOperationException(diag
                                + "，响应前200字: " + Shorten(text));
                        if (string.IsNullOrEmpty(text))
                            throw new InvalidOperationException(diag + "（200 但响应为空）");
                        return text;
                    }
                }
            }).ConfigureAwait(false);

            var root = MiniJson.AsObject(MiniJson.Parse(body));
            if (root == null)
                throw new InvalidOperationException("eapi 响应不是 JSON: " + Shorten(body));
            if (MiniJson.Lng(root, "code", 200) == -460)
            {
                await Task.Delay(5000).ConfigureAwait(false);
                throw new InvalidOperationException("触发风控（-460）");
            }
            return root;
        }

        private async Task<string> SendRawAsync(bool post, string path, IDictionary<string, string> form)
        {
            string url = path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? path : BaseUri + path;

            return await _throttle.RunAsync(async () =>
            {
                using (var req = new HttpRequestMessage(post ? HttpMethod.Post : HttpMethod.Get, url))
                {
                    if (post)
                    {
                        var list = new List<KeyValuePair<string, string>>();
                        if (form != null)
                        {
                            foreach (var kv in form) list.Add(new KeyValuePair<string, string>(kv.Key, kv.Value ?? ""));
                        }
                        req.Content = new FormUrlEncodedContent(list);
                    }

                    // r44：Cookie 头手动带（_plainHttp 不带容器，见字段注释）
                    string ck = CookieText;
                    if (!string.IsNullOrEmpty(ck))
                        req.Headers.TryAddWithoutValidation("Cookie", ck);

                    using (var resp = await _plainHttp.SendAsync(req).ConfigureAwait(false))
                    {
                        string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        _lastRaw = body;
                        HarvestCookies();
                        return body;
                    }
                }
            }).ConfigureAwait(false);
        }

        /// <summary>最近一次 /api/ 原始响应（短截后进失败提示，排查取流被拒用）。</summary>
        private volatile string _lastRaw = "";

        /// <summary>
        /// 发请求并解析成字典。code == -460（风控）会退避 5 秒重试一次，
        /// 再失败就把原文抛出来——绝不静默当成"没结果"。
        /// </summary>
        private async Task<Dictionary<string, object>> CallAsync(bool post, string path, IDictionary<string, string> form)
        {
            string body = await SendRawAsync(post, path, form).ConfigureAwait(false);
            var root = MiniJson.AsObject(MiniJson.Parse(body));
            if (root == null)
                throw new InvalidOperationException("响应不是 JSON 对象: " + Shorten(body));

            if (MiniJson.Lng(root, "code", 200) == -460)
            {
                await Task.Delay(5000).ConfigureAwait(false);
                body = await SendRawAsync(post, path, form).ConfigureAwait(false);
                root = MiniJson.AsObject(MiniJson.Parse(body));
                if (root != null && MiniJson.Lng(root, "code", 200) == -460)
                    throw new InvalidOperationException("触发风控（-460）：请求过于频繁，歇一会儿再试");
            }
            return root;
        }

        private static string Shorten(string s)
        {
            if (string.IsNullOrEmpty(s)) return "(空)";
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length <= 200 ? s : s.Substring(0, 200) + "…";
        }

        // ───────────────────────────── 搜索 ─────────────────────────────

        /// <summary>搜索单曲。返回的每首都带 fee / privileges.st，调用方据此判断能不能播。</summary>
        public async Task<List<SongInfo>> SearchAsync(string keyword, int limit = 30, int offset = 0,
            CancellationToken ct = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(keyword)) throw new ArgumentException("关键词不能为空", "keyword");
            if (limit <= 0) limit = 30;
            if (limit > 100) limit = 100;

            var form = new Dictionary<string, string>
            {
                { "s", keyword },
                { "type", "1" },
                { "limit", limit.ToString() },
                { "offset", offset.ToString() },
                { "total", "true" },
            };
            var root = await CallAsync(true, "/api/cloudsearch/pc", form).ConfigureAwait(false);
            var result = MiniJson.Dict(root, "result");
            var songs = MiniJson.List(result, "songs");

            var list = new List<SongInfo>();
            if (songs == null) return list;

            foreach (object o in songs)
            {
                var s = MiniJson.AsObject(o);
                if (s == null) continue;

                var info = new SongInfo
                {
                    Id = MiniJson.Lng(s, "id"),
                    Name = MiniJson.Str(s, "name", ""),
                    DurationMs = MiniJson.Lng(s, "dt"),
                    Fee = (int)MiniJson.Lng(s, "fee"),
                };

                var ar = MiniJson.List(s, "ar");
                if (ar != null)
                {
                    var names = new List<string>();
                    foreach (object a in ar)
                    {
                        var ao = MiniJson.AsObject(a);
                        string n = MiniJson.Str(ao, "name");
                        if (!string.IsNullOrEmpty(n)) names.Add(n);
                    }
                    info.Artists = string.Join(" / ", names.ToArray());
                }

                var al = MiniJson.Dict(s, "al");
                if (al != null)
                {
                    info.Album = MiniJson.Str(al, "name", "");
                    info.AlbumPicUrl = MiniJson.Str(al, "picUrl", "");
                }

                var priv = MiniJson.Dict(s, "privilege");
                info.PrivilegeSt = priv != null ? (int)MiniJson.Lng(priv, "st") : 0;

                list.Add(info);
            }
            return list;
        }

        // ───────────────────────────── 播放地址 ─────────────────────────────

        /// <summary>
        /// 取播放地址（r41 带音质降级）。level: standard / higher / exhigh / lossless。
        /// 先按 level 取，拿不到再降 standard 试一次——高音质被会员卡住不等于这首歌不能播，
        /// fee=8「低音质免费高音质付费」这类降档就有直链；真 VIP/付费曲（fee=1/4）降档照样
        /// 失败，提示不变。fee=0 的歌匿名就能拿直链，登录只是解锁更多曲目/音质。
        /// 调用方只应在用户真的点了播放时调它——绝不预取。
        /// </summary>
        public async Task<SongUrlResult> GetSongUrlAsync(long songId, string level = "exhigh",
            CancellationToken ct = default(CancellationToken))
        {
            SongUrlResult r = await GetSongUrlOnceAsync(songId, level, ct).ConfigureAwait(false);
            if (!r.Ok && !string.Equals(level, "standard", StringComparison.OrdinalIgnoreCase))
            {
                SongUrlResult lo = await GetSongUrlOnceAsync(songId, "standard", ct).ConfigureAwait(false);
                if (lo.Ok) return lo;
            }
            return r;
        }

        /// <summary>
        /// 按用户设置取「能拿到的最好一档」。
        /// <para>关键取舍：**先拿 320k 当保底，再考虑升无损**。原因是高音质档在非会员号上
        /// 不一定报错——它可能回一条 level=standard 的 128k 直链且 code=200，直接采信就
        /// 会让「开了无损反而更差」。所以只有确认拿到的确实比 320k 好（FLAC 或码率更高）
        /// 才采用它。</para>
        /// quality: 0 自动 / 1 标准 / 2 较高 / 3 极高 / 4 无损 / 5 Hi-Res
        /// </summary>
        public async Task<SongUrlResult> GetBestUrlAsync(long songId, int quality,
            CancellationToken ct = default(CancellationToken))
        {
            // 1) 基准档：320k MP3，匿名也基本都能拿到
            SongUrlResult baseR = await GetSongUrlOnceAsync(songId, "exhigh", ct).ConfigureAwait(false);

            string hi = null;
            if (quality == 5) hi = "hires";
            else if (quality >= 4) hi = "lossless";
            else if (quality == 3) return baseR;      // 极高 = 320k，就是基准档

            if (quality == 2)
            {
                SongUrlResult h = await GetSongUrlOnceAsync(songId, "higher", ct).ConfigureAwait(false);
                return h.Ok ? h : baseR;
            }
            if (quality == 1)
            {
                SongUrlResult st = await GetSongUrlOnceAsync(songId, "standard", ct).ConfigureAwait(false);
                return st.Ok ? st : baseR;
            }

            if (hi != null)
            {
                SongUrlResult r = await GetSongUrlOnceAsync(songId, hi, ct).ConfigureAwait(false);
                // Hi-Res 拿不到就退一级试无损，再不行才回 320k
                if (!r.Ok && quality == 5)
                    r = await GetSongUrlOnceAsync(songId, "lossless", ct).ConfigureAwait(false);

                bool better = r.Ok && (r.IsFlac || (baseR.Ok && r.Br > baseR.Br + 32000));
                if (better)
                {
                    if (PluginHooks.LogInfo != null)
                        PluginHooks.LogInfo("网易云取流 " + r.Spec + "（无损档成功）");
                    return r;
                }

                if (r.Ok && baseR.Ok && !r.IsFlac && r.Br <= baseR.Br + 32000)
                {
                    if (PluginHooks.LogInfo != null)
                        PluginHooks.LogInfo("无损档只给了 " + r.Spec + "，已回退 320k（会员/版权限制）");
                }
            }

            if (baseR.Ok) return baseR;
            return await GetSongUrlOnceAsync(songId, "standard", ct).ConfigureAwait(false);
        }

        private async Task<SongUrlResult> GetSongUrlOnceAsync(long songId, string level,
            CancellationToken ct = default(CancellationToken))
        {
            // 无损/Hi-Res 必须把 encodeType 也切成 flac，否则接口就算有版权也只回 mp3。
            bool wantFlac = string.Equals(level, "lossless", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(level, "hires", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(level, "jymaster", StringComparison.OrdinalIgnoreCase);

            var form = new Dictionary<string, string>
            {
                { "ids", "[" + songId + "]" },
                { "level", level },
                { "encodeType", wantFlac ? "flac" : "mp3" },
            };

            var root = await CallAsync(true, "/api/song/enhance/player/url/v1", form).ConfigureAwait(false);
            var data = MiniJson.List(root, "data");
            var first = data != null && data.Count > 0 ? MiniJson.AsObject(data[0]) : null;

            var r = new SongUrlResult { Id = songId, Code = (int)MiniJson.Lng(first ?? root, "code") };
            if (first == null)
            {
                r.Message = "data 为空";
                return r;
            }

            r.Url = ForceHttps(MiniJson.Str(first, "url"));
            r.Br = (int)MiniJson.Lng(first, "br");
            r.Size = MiniJson.Lng(first, "size");
            r.Fee = (int)MiniJson.Lng(first, "fee");
            r.Level = MiniJson.Str(first, "level", "");
            r.Message = MiniJson.Str(first, "message");
            r.HasFreeTrial = MiniJson.Dict(first, "freeTrialInfo") != null;

            string typ = MiniJson.Str(first, "type", "");
            if (!string.IsNullOrEmpty(typ)) r.Format = typ.ToLowerInvariant();
            else if (!string.IsNullOrEmpty(r.Url) &&
                     r.Url.IndexOf(".flac", StringComparison.OrdinalIgnoreCase) >= 0) r.Format = "flac";
            else if (!string.IsNullOrEmpty(r.Url)) r.Format = "mp3";

            // 取不到直链时附一小段原始响应（百来字，远离 BepInEx 单行 2000 字上限），排查用
            if (string.IsNullOrEmpty(r.Url))
            {
                string snippet = (_lastRaw ?? "").Replace("\r", " ").Replace("\n", " ");
                if (snippet.Length > 140) snippet = snippet.Substring(0, 140);
                r.Message = (r.Message ?? "") + " |resp=" + snippet;
            }
            return r;
        }

        // ───────────────────────────── 歌词 ─────────────────────────────

        public async Task<LyricResult> GetLyricAsync(long songId, CancellationToken ct = default(CancellationToken))
        {
            string path = "/api/song/lyric/v1?id=" + songId + "&lv=0&kv=0&tv=-1&yv=1&ytv=1&cp=false";
            var root = await CallAsync(false, path, null).ConfigureAwait(false);

            var lrc = MiniJson.Dict(root, "lrc");
            var tlrc = MiniJson.Dict(root, "tlyric");

            return new LyricResult
            {
                Id = songId,
                Lrc = MiniJson.Str(lrc, "lyric", "") ?? "",
                Translated = MiniJson.Str(tlrc, "lyric", "") ?? "",
            };
        }

        // ───────────────────────────── 扫码登录 ─────────────────────────────

        /// <summary>
        /// 拿一个 unikey。把它拼成 https://music.163.com/login?codekey=&lt;key&gt; 扫，或直接丢手机浏览器。
        /// r30：改走 eapi 加密（明文 /api/ 会被 8821 风控拒，对照 Muonium RequestUtil）。
        /// </summary>
        public async Task<string> QrBeginAsync(CancellationToken ct = default(CancellationToken))
        {
            var root = await EapiCallAsync("/api/login/qrcode/unikey",
                new Dictionary<string, string> { { "type", "3" } }, ct).ConfigureAwait(false);

            string key = MiniJson.Str(root, "unikey");
            if (string.IsNullOrEmpty(key))
                throw new InvalidOperationException("没拿到 unikey，响应: " + MiniJson.Preview(root));
            return key;
        }

        /// <summary>轮询扫码状态。建议 2 秒一次；800 过期就重新 QrBeginAsync。</summary>
        public async Task<QrLoginResult> QrPollAsync(string unikey, CancellationToken ct = default(CancellationToken))
        {
            var root = await EapiCallAsync("/api/login/qrcode/client/login",
                new Dictionary<string, string> { { "key", unikey }, { "type", "3" } }, ct).ConfigureAwait(false);

            int code = (int)MiniJson.Lng(root, "code", -1);
            var r = new QrLoginResult { Code = code, RawMessage = MiniJson.Str(root, "message", "") };

            switch (code)
            {
                case 800: r.State = QrState.Expired; break;
                case 801: r.State = QrState.Waiting; break;
                case 802: r.State = QrState.Scanned; break;
                case 803:
                {
                    r.State = QrState.Success;
                    // r41：登录 cookie 在两处——响应体的 cookie 字段（网页接口惯例）和
                    // Set-Cookie 响应头（eapi）。r30~r40 只查 HarvestCookies()（读的是 _http
                    // 的容器，eapi 从不喂它），MUSIC_U 全丢。两处合并，响应头优先。
                    string bodyCookie = MiniJson.Str(root, "cookie", "");
                    if (string.IsNullOrEmpty(bodyCookie))
                    {
                        var arr = MiniJson.AsArray(root.ContainsKey("cookie") ? root["cookie"] : null);
                        if (arr != null)
                        {
                            var parts = new List<string>();
                            foreach (object o in arr)
                            {
                                string s = o as string;
                                if (!string.IsNullOrEmpty(s)) parts.Add(s);
                            }
                            bodyCookie = string.Join("; ", parts.ToArray());
                        }
                    }
                    var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var kv in CookieStore.Parse(bodyCookie)) merged[kv.Key] = kv.Value;
                    foreach (var kv in CookieStore.Parse(_lastEapiSetCookie)) merged[kv.Key] = kv.Value;
                    string cookieText = CookieStore.Normalize(merged);
                    if (string.IsNullOrEmpty(cookieText)) cookieText = HarvestCookies() ?? "";
                    r.CookieText = cookieText;
                    break;
                }
                case 8821:
                    // 实测：poll 本身带 NMTID 时是 801；手机点确认那一下才回 8821。
                    // 说明风控挂在"完成登录"这一步，不是挂在轮询频率上。
                    r.State = QrState.RiskBlocked;
                    break;
                default: r.State = QrState.Unknown; break;
            }
            return r;
        }

        // ───────────────────────────── 账号 ─────────────────────────────

        public async Task<AccountInfo> GetAccountAsync(CancellationToken ct = default(CancellationToken))
        {
            var root = await CallAsync(true, "/api/nuser/account/get", new Dictionary<string, string>()).ConfigureAwait(false);
            var profile = MiniJson.Dict(root, "profile");

            if (profile == null) return new AccountInfo();   // 未登录时就是 null，不是错误

            return new AccountInfo
            {
                UserId = MiniJson.Lng(profile, "userId"),
                Nickname = MiniJson.Str(profile, "nickname", ""),
                AvatarUrl = MiniJson.Str(profile, "avatarUrl", ""),
            };
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _http.Dispose();
            try { _eapiHttp.Dispose(); } catch { }
            _throttle.Dispose();
        }
    }
}
