using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PeakMusic.Core.Netease;
using PeakMusic.Core.Sources;

namespace PeakMusic.Core.Sources
{
    /// <summary>
    /// QQ 扫码登录结果。状态码沿用手 Q 的 ptlogin 语义 + 本项目私有扩展。
    /// </summary>
    public sealed class QqLoginResult
    {
        public enum QqLoginState
        {
            Unknown = -1,
            /// <summary>等待扫码（ptqrlogin 返回 未登录）。</summary>
            Waiting = 0,
            /// <summary>已扫码，等手机确认。</summary>
            Scanned = 1,
            /// <summary>登录成功，cookie 已写入。</summary>
            Success = 2,
            /// <summary>二维码过期。</summary>
            Expired = 3,
            /// <summary>登录失败 / 被拒绝。</summary>
            Failed = 4,
        }

        public QqLoginState State = QqLoginState.Unknown;
        public string CookieText = "";
        public string RawMessage = "";

        public string Explain()
        {
            switch (State)
            {
                case QqLoginState.Waiting: return "等待手机扫码";
                case QqLoginState.Scanned: return "已扫码，请在手机上确认";
                case QqLoginState.Success: return "登录成功，cookie 已保存";
                case QqLoginState.Expired: return "二维码已过期，请换一张";
                case QqLoginState.Failed: return "登录失败：" + RawMessage;
                default: return "未知状态：" + RawMessage;
            }
        }
    }

    /// <summary>
    /// QQ 音乐客户端（匿名）。三个接口，全部实测通过（2026-11-07）：
    ///
    /// <list type="bullet">
    /// <item>搜索 <c>c.y.qq.com/soso/fcgi-bin/client_search_cp</c> —— 明文 JSON，必须带 Referer。</item>
    /// <item>取流 <c>u.y.qq.com/cgi-bin/musicu.fcg</c> —— 两段式：先 GetCdnDispatch 拿 CDN 列表，
    ///       再 CgiGetVkey 拿带 vkey 的 purl。**关键技巧**：param 里带 <c>filename</c> 数组
    ///       可以指定音质前缀，要 <c>M500{mid}.mp3</c> 才会给 mp3；不给就是 <c>C400{mid}.m4a</c>，
    ///       而 Unity 的 UnityWebRequestMultimedia 解不了 AAC，等于不能播。</item>
    /// <item>歌词 <c>c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg</c> —— lyric 字段是 base64。</item>
    /// </list>
    ///
    /// <para><b>匿名限制</b>：只有 <c>pay.payplay == 0</c> 的曲子 purl 非空；付费曲 purl 为空字符串。
    /// 匿名也只能拿 128k（M800 即 320k 会被拒），这是接口行为，不是 bug。</para>
    /// </summary>
    public sealed class QqClient : IDisposable
    {
        private const string SearchUrl = "https://c.y.qq.com/soso/fcgi-bin/client_search_cp";
        private const string FcgUrl = "https://u.y.qq.com/cgi-bin/musicu.fcg";
        private const string LyricUrl = "https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg";
        private const string CoverUrl = "https://y.gtimg.cn/music/photo_new/T002R300x300M000";

        // QQ 扫码登录三步（ptlogin2.qq.com 老逆向接口，2026 仍可用）
        private const string PtXLogin = "https://xui.ptlogin2.qq.com/cgi-bin/xlogin";
        private const string PtQrShow = "https://ssl.ptlogin2.qq.com/ptqrshow";
        private const string PtQrLogin = "https://ssl.ptlogin2.qq.com/ptqrlogin";

        /// <summary>匿名音质前缀：M500 = 128k mp3（可用），M800 = 320k mp3（登录后），C400 = m4a（Unity 不能解）。</summary>
        private const string Mp3Prefix = "M500";
        private const string Mp3PrefixHi = "M800";

        private const string Referer = "https://y.qq.com/portal/player.html";

        /// <summary>ptlogin 域（xlogin/ptqrshow/ptqrlogin）必须用自己家的 Referer，
        /// 用 y.qq.com 会被拒种 qrsig / login_sig cookie（r23 实测：qrsig 0 字符）。</summary>
        private const string PtReferer = "https://xui.ptlogin2.qq.com/";

        /// <summary>最近一次 ptlogin 请求响应头里的原始 Set-Cookie（qrsig 兜底解析用）。</summary>
        private string _lastSetCookie = "";

        private const string UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
            "Chrome/120.0.0.0 Safari/537.36";

        // r35：整套 handler+client 可重建——绝不在请求发出后给共享实例改属性（见 NewSession 注释）
        private HttpClient _http;

        // r44：取流等敏感请求专用——不带 CookieContainer，Cookie 头每次手动拼。
        // Unity 的 Mono 在游戏里会把容器 cookie 发坏/漏发：表现为所有歌（含免费歌）
        // 空 purl，而游戏外同代码同 cookie 全通。手动 Cookie 头与 PostFormRedirectAsync 同款姿势。
        private HttpClient _cookielessHttp;
        private HttpClientHandler _handler;
        private CookieContainer _cookies = new CookieContainer();
        private readonly Throttle _throttle;
        private readonly Random _rng = new Random(unchecked(Environment.TickCount * 17));

        // 登录态
        private string _savedCookie;
        private string _uin = "";
        private string _qrsig = "";
        private string _loginSig = "";

        public QqClient(Throttle throttle = null)
        {
            NewSession();
            _throttle = throttle ?? new Throttle();

            _cookielessHttp = new HttpClient(new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                UseCookies = false,
            }) { Timeout = TimeSpan.FromSeconds(20) };
            _cookielessHttp.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
            _cookielessHttp.DefaultRequestHeaders.TryAddWithoutValidation("Referer", Referer);

            // 灌回磁盘登录态
            _savedCookie = QqCookieStore.Load();
            LoadCookiesIntoContainer(_savedCookie);
            RefreshUin();
        }

        /// <summary>
        /// 整套换新 handler + client + cookie 容器（r35）。
        /// 千万别在请求发出后给共享实例改属性（哪怕是换 CookieContainer）：
        /// Mono 的 System.Net.Http 在首个请求之后改属性会同步抛
        /// InvalidOperationException「This instance has already started one or more requests.
        /// Properties can only be modified before sending the first request.」——
        /// r33 的 `_handler.CookieContainer = _cookies` 就是这么炸的：
        /// 第一张二维码成功，之后每张必挂（实机日志证实）。要新会话就整套重建，别动旧实例。
        /// 调用方都走 Throttle 串行，这里换引用不会有请求并发踩旧实例。
        /// </summary>
        private void NewSession()
        {
            HttpClient old = _http;
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                UseCookies = true,
                CookieContainer = new CookieContainer(),
            };
            _handler = handler;
            _cookies = handler.CookieContainer;

            var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
            http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", Referer);
            _http = http;

            if (old != null) { try { old.Dispose(); } catch { } }
        }

        /// <summary>当前登录态（"k=v; k=v"），可能为空串。</summary>
        public string CookieText
        {
            get { return HarvestCookies() ?? _savedCookie ?? ""; }
        }

        public bool HasLogin
        {
            get { return !string.IsNullOrEmpty(_uin) && !string.IsNullOrEmpty(LookupCookie("qqmusic_key")); }
        }

        /// <summary>取流用的 QQ 号。未登录时返回 "0"（匿名）。</summary>
        public string Uin { get { return string.IsNullOrEmpty(_uin) ? "0" : _uin; } }

        private string LookupCookie(string name)
        {
            var d = QqCookieStore.Parse(CookieText);
            string v;
            return d.TryGetValue(name, out v) ? v : null;
        }

        private void RefreshUin()
        {
            _uin = LookupCookie("uin") ?? LookupCookie("wxuin") ?? "";
            if (string.IsNullOrEmpty(_uin))
            {
                // r36：新版 ptlogin 只发 p_uin（"o" + 补零数字），没有 uin cookie。
                // 剥掉 o 前缀和前导零还原 QQ 号，否则 _uin 恒空 → HasLogin 恒 false。
                string p = LookupCookie("p_uin") ?? "";
                if (p.Length > 1)
                {
                    string s = p.TrimStart('o', 'O').TrimStart('0');
                    if (s.Length > 0) _uin = s;
                }
            }
        }

        public void SetCookie(string cookieText)
        {
            LoadCookiesIntoContainer(cookieText);
            QqCookieStore.Save(QqCookieStore.Normalize(QqCookieStore.Parse(cookieText)));
            _savedCookie = QqCookieStore.Load();
            RefreshUin();
        }

        public void Logout()
        {
            QqCookieStore.Clear();
            _savedCookie = "";
            _uin = "";
            _qrsig = "";
            _loginSig = "";
            try
            {
                foreach (Cookie c in _cookies.GetCookies(new Uri("https://y.qq.com/"))) c.Expired = true;
                foreach (Cookie c in _cookies.GetCookies(new Uri("https://ptlogin2.qq.com/"))) c.Expired = true;
            }
            catch { }
        }

        // ────────────────────────────── 搜索 ──────────────────────────────

        public Task<List<SongInfo>> SearchAsync(string keyword, int limit = 40, int offset = 0,
                                                CancellationToken ct = default(CancellationToken))
        {
            return _throttle.RunAsync(async () =>
            {
                int page = offset / Math.Max(1, limit) + 1;
                // 别加 new_json=1：那个参数会把 song.list 的字段整体改名（songmid→mid、
                // singer→singer 嵌套到 album 里），实测加完之后一首歌都解析不出来。
                // 这条老接口只吃 format/t/p/n/w 这五个参数。
                string url = SearchUrl
                    + "?format=json&t=0"
                    + "&p=" + page + "&n=" + limit
                    + "&w=" + Uri.EscapeDataString(keyword ?? "");

                string text = await GetAsync(url, ct).ConfigureAwait(false);
                return ParseSearch(text);
            }, ct);
        }

        private static List<SongInfo> ParseSearch(string json)
        {
            var list = new List<SongInfo>();
            var root = MiniJson.AsObject(MiniJson.Parse(json));
            if (root == null) return list;

            var data = MiniJson.Dict(root, "data");
            var song = data != null ? MiniJson.Dict(data, "song") : null;
            var arr = song != null ? MiniJson.List(song, "list") : null;
            if (arr == null) return list;

            foreach (var o in arr)
            {
                var d = MiniJson.AsObject(o);
                if (d == null) continue;

                string mid = MiniJson.Str(d, "songmid", "");
                if (string.IsNullOrEmpty(mid)) continue;

                var info = new SongInfo
                {
                    Platform = Platform.QQ,
                    SourceId = mid,
                    Id = StableId(mid),
                    Name = MiniJson.Str(d, "songname", "?"),
                    Album = MiniJson.Str(d, "albumname", ""),
                    DurationMs = MiniJson.Lng(d, "interval", 0) * 1000L,
                };

                // 多歌手用 / 连接，跟网易的 Artists 字段保持同一种展示形态
                var singers = MiniJson.List(d, "singer");
                if (singers != null)
                {
                    var names = new List<string>();
                    foreach (var so in singers)
                    {
                        var sd = MiniJson.AsObject(so);
                        string n = sd != null ? MiniJson.Str(sd, "name", "") : "";
                        if (!string.IsNullOrEmpty(n)) names.Add(n);
                    }
                    info.Artists = string.Join(" / ", names.ToArray());
                }

                string albumMid = MiniJson.Str(d, "albummid", "");
                if (!string.IsNullOrEmpty(albumMid))
                    info.AlbumPicUrl = CoverUrl + albumMid + ".jpg?max_age=2592000";

                // pay.payplay：0 免费可播，1 需付费/会员。映射到 Fee 让面板的 VIP 标能复用同一套逻辑
                var pay = MiniJson.Dict(d, "pay");
                int payplay = pay != null ? (int)MiniJson.Lng(pay, "payplay", 0) : 0;
                info.Fee = payplay != 0 ? 1 : 0;
                info.PrivilegeSt = 0;

                list.Add(info);
            }

            return list;
        }

        // ────────────────────────────── 登录态 ──────────────────────────────

        private void LoadCookiesIntoContainer(string cookieText)
        {
            var d = QqCookieStore.Parse(cookieText);
            foreach (var kv in d)
            {
                try
                {
                    _cookies.Add(new Uri("https://y.qq.com/"), new Cookie(kv.Key, kv.Value) { Domain = ".qq.com", Path = "/" });
                }
                catch { }
            }
        }

        private string HarvestCookies()
        {
            try
            {
                var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (Uri root in new[]
                {
                    new Uri("https://y.qq.com/"),
                    new Uri("https://qq.com/"),
                    new Uri("https://ptlogin2.qq.com/"),
                    new Uri("https://ssl.ptlogin2.qq.com/"),
                    new Uri("https://ssl.ptlogin2.graph.qq.com/"),
                    new Uri("https://graph.qq.com/"),
                })
                {
                    foreach (Cookie c in _cookies.GetCookies(root))
                        if (!string.IsNullOrEmpty(c.Value)) dict[c.Name] = c.Value;
                }

                string norm = QqCookieStore.Normalize(dict);
                if (string.IsNullOrEmpty(norm)) return null;
                if (norm != _savedCookie && QqCookieStore.Parse(norm).ContainsKey("qqmusic_key"))
                {
                    QqCookieStore.Save(norm);
                    _savedCookie = norm;
                }
                return norm;
            }
            catch { return null; }
        }

        /// <summary>ptlogin 的 hash33：hash = hash * 33 + charCode，用于把 qrsig 算成 ptqrtoken。</summary>
        private static int Hash33(string s)
        {
            return Hash33(s, 0);
        }

        /// <summary>带种子的 hash33。g_tk 用 seed=5381（对照 Muonium 的 MuHash33.hash33）。</summary>
        private static int Hash33(string s, int seed)
        {
            int hash = seed;
            if (string.IsNullOrEmpty(s)) return hash & 0x7FFFFFFF;
            for (int i = 0; i < s.Length; i++)
                hash = (hash << 5) + hash + s[i];   // hash * 33 + c，保留溢出（JS 语义）
            return hash & 0x7FFFFFFF;
        }

        // ────────────────────────────── 扫码登录 ──────────────────────────────

        /// <summary>
        /// 第一步：拉取二维码图片（PNG 字节）。同时缓存 qrsig / pt_login_sig，
        /// 供后续轮询使用。返回二维码 PNG 原始字节，调用方转 Texture2D 显示。
        /// </summary>
        public Task<byte[]> QrBeginAsync(CancellationToken ct = default(CancellationToken))
        {
            return _throttle.RunAsync(async () =>
            {
                // r33：每轮扫码换新 cookie 容器（Muonium 语义：登录 jar 只带本次流程收到的 cookie）。
                // 100035「用户未登录」根因：上轮扫码/磁盘灌入的旧登录 cookie 挂在 .qq.com 域，
                // 拍平时压过本次新种的 host-only 登录态，authorize 带着死会话被拒。
                // r35：改成 NewSession() 整套重建——直接给已启动的 handler 换 CookieContainer
                // 会抛「already started one or more requests」，第一张二维码之后全灭。
                NewSession();
                _qrsig = "";
                _loginSig = "";

                // 先访问 xlogin 拿 pt_login_sig cookie。s_url 必须是 OAuth authorize 地址：
                // check_sig 登录后会 302 到它，它再 302 到 redirect_uri 带 code。
                string xlogin = PtXLogin
                    + "?appid=716027609&daid=383&style=33&login_text=授权并登录&hide_title_bar=1"
                    + "&hide_border=1&target=self"
                    + "&s_url=" + Uri.EscapeDataString(OAuthAuthorizeUrl())
                    + "&pt_3rd_aid=100497308"
                    + "&pt_feedback_link=" + Uri.EscapeDataString("https://support.qq.com/products/77942");
                try
                {
                    await GetWithCaptureAsync(xlogin, ct).ConfigureAwait(false);
                    // xlogin 会 Set-Cookie pt_login_sig；容器可能吃不到（host-only），从头部兜底
                    string sig = ExtractSetCookie(_lastSetCookie, "pt_login_sig");
                    if (!string.IsNullOrEmpty(sig)) _loginSig = sig;
                }
                catch { /* xlogin 失败不致命，继续拿二维码 */ }

                if (string.IsNullOrEmpty(_loginSig))
                    _loginSig = LookupCookie("pt_login_sig") ?? "";

                string t = _rng.NextDouble().ToString("R");
                string qrshow = PtQrShow
                    + "?appid=716027609&e=2&l=M&s=3&d=72&v=4&t=" + t
                    + "&daid=383&pt_3rd_aid=100497308";

                byte[] img = await GetWithCaptureAsync(qrshow, ct).ConfigureAwait(false);

                // qrsig 关键：优先从 Set-Cookie 响应头直接抓（不依赖 CookieContainer 的域规则）
                string qr = ExtractSetCookie(_lastSetCookie, "qrsig");
                _qrsig = !string.IsNullOrEmpty(qr) ? qr : (LookupCookie("qrsig") ?? "");

                if (img == null || img.Length == 0)
                    throw new InvalidOperationException("ptqrshow 没返回二维码图片");

                return img;
            }, ct);
        }

        /// <summary>
        /// 第二步+第三步：轮询扫码状态，成功后走 graph.qq.com OAuth 拿 code，
        /// 再用 code 调 musicu.fcg 的 QQConnectLogin 换 musickey。
        /// </summary>
        public Task<QqLoginResult> QrPollAsync(CancellationToken ct = default(CancellationToken))
        {
            return _throttle.RunAsync(async () =>
            {
                var r = new QqLoginResult();

                int token = Hash33(_qrsig);
                string action = "0-0-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                string loginSig = string.IsNullOrEmpty(_loginSig) ? "" : _loginSig;

                string url = PtQrLogin
                    + "?u1=" + Uri.EscapeDataString(OAuthAuthorizeUrl())
                    + "&ptqrtoken=" + token
                    + "&ptredirect=0&h=1&t=1&g=1&from_ui=1&ptlang=2052"
                    + "&action=" + Uri.EscapeDataString(action)
                    + "&js_ver=20102616&js_type=1"
                    + "&login_sig=" + Uri.EscapeDataString(loginSig)
                    + "&pt_uistyle=40&aid=716027609&daid=383&pt_3rd_aid=100497308";

                string text;
                try { text = await GetRawAsync(url, ct, PtReferer).ConfigureAwait(false); }
                catch (Exception e) { r.State = QqLoginResult.QqLoginState.Failed; r.RawMessage = e.Message; return r; }

                // ptuiCB('状态码','0','跳转URL','0','消息')。状态码：
                //   0=登录成功（第三参是 check_sig 跳转 URL）
                //   66=二维码未失效（还在等扫码）  67=已扫码待手机确认
                //   65=二维码已失效               68=已授权但需要继续
                string state = "", jumpUrl = "", msg = "";
                ParsePtuiCb(text, ref state, ref jumpUrl, ref msg);

                if (state == "0")
                {
                    // 成功响应第三参是 check_sig 跳转 URL（带 ptsigx 和 uin）。
                    // 正确链路（对照 Muonium 的 MuQQApi.authorizeQq）：
                    //   check_sig(带 ptsigx) → 拿 p_skey cookie
                    //   POST authorize(带 g_tk=hash33(p_skey,5381)) → 拿 OAuth code
                    //   musicu QQLogin(tmeLoginType=2) → 换 musickey
                    string oauthCode = await AuthorizeQq(jumpUrl, ct).ConfigureAwait(false);

                    if (string.IsNullOrEmpty(oauthCode))
                    {
                        r.State = QqLoginResult.QqLoginState.Failed;
                        r.RawMessage = "登录成功但没拿到 OAuth code。轨迹：" + _lastOauthTrace;
                        return r;
                    }

                    string musickey = await MusicuLogin(oauthCode, ct).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(musickey))
                    {
                        r.State = QqLoginResult.QqLoginState.Failed;
                        r.RawMessage = "OAuth code 拿到了但没换到 musickey";
                        return r;
                    }

                    // r36：musickey 必须落进 cookie —— HasLogin 判定查的就是 qqmusic_key。
                    // r35 及之前只把 musickey 当"成功凭证"看一眼就丢，存盘的只有 ptlogin
                    // 三件套（p_skey/p_uin/pt4_token），判定永远查不到 → 面板永远"未登录"。
                    // 语义对齐 Muonium：musickey 同时写 qqmusic_key（主）和 musickey（兼容）。
                    var jar = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var kv in QqCookieStore.Parse(HarvestCookies() ?? "")) jar[kv.Key] = kv.Value;
                    jar["qqmusic_key"] = musickey;
                    jar["musickey"] = musickey;
                    if (!jar.ContainsKey("uin") && !string.IsNullOrEmpty(_uin)) jar["uin"] = _uin;

                    string fullCookie = QqCookieStore.Normalize(jar);
                    if (!string.IsNullOrEmpty(fullCookie)) SetCookie(fullCookie);
                    r.State = QqLoginResult.QqLoginState.Success;
                    r.CookieText = fullCookie;
                    return r;
                }

                if (state == "66") { r.State = QqLoginResult.QqLoginState.Waiting; return r; }
                if (state == "67") { r.State = QqLoginResult.QqLoginState.Scanned; return r; }
                if (state == "65") { r.State = QqLoginResult.QqLoginState.Expired; return r; }

                // 未知状态码：记下原文便于诊断（面板会把它写进日志）。
                // 空响应几乎都是 qrsig 没种上（Referer 不对/二维码没取到），带上长度好定位
                r.State = QqLoginResult.QqLoginState.Failed;
                r.RawMessage = string.IsNullOrEmpty(text)
                    ? "ptqrlogin 空响应（qrsig " + _qrsig.Length + " 字符，login_sig " + loginSig.Length +
                      " 字符；为 0 说明二维码会话没建立，请点「换一张」重拉）"
                    : "ptqrlogin 未知响应[" + (state.Length > 0 ? state : "?") + "] " +
                               Summarize(text);
                return r;
            }, ct);
        }

        /// <summary>graph.qq.com 的 OAuth 授权地址（check_sig 登录后 302 到这里，再 302 到 redirect_uri 带 code）。</summary>
        private static string OAuthAuthorizeUrl()
        {
            return "https://graph.qq.com/oauth2.0/authorize?client_id=100497308&response_type=code" +
                   "&redirect_uri=" + Uri.EscapeDataString("https://y.qq.com/portal/wx_redirect.html") +
                   "&scope=" + Uri.EscapeDataString("get_user_basic,get_vip_info,get_user_detail") +
                   "&state=peak";
        }

        /// <summary>
        /// 解析 ptuiCB('码','0','URL','0','消息') 的字段。
        /// r25：不再假设 ASCII 直引号——QQ 有时回弯引号（' ' “ ”），老解析器只认 '，
        /// 导致状态码抠不出来、明明 67（已扫码）却报"未知响应[?]"。
        /// 现在按"任意引号对"切字段，只看 ptuiCB 括号内的内容。
        /// </summary>
        private static void ParsePtuiCb(string text, ref string state, ref string url, ref string msg)
        {
            if (string.IsNullOrEmpty(text)) return;
            int p = text.IndexOf("ptuiCB", StringComparison.Ordinal);
            if (p < 0) return;

            int open = text.IndexOf('(', p);
            int close = text.LastIndexOf(')');
            if (open < 0 || close <= open) return;
            string body = text.Substring(open + 1, close - open - 1);

            // 抓所有引号对里的内容。直引号/弯引号/单双引号都认；
            // 弯引号必须开闭配对（'…' "…"），直引号允许同字符闭合。
            var fields = new List<string>();
            int i = 0;
            while (i < body.Length && fields.Count < 8)
            {
                char c = body[i];
                int openIdx = "'\"\u2018\u201c".IndexOf(c);
                if (openIdx < 0) { i++; continue; }

                char closing;
                if (c == '\u2018') closing = '\u2019';          // ‘ → ’
                else if (c == '\u201c') closing = '\u201d';     // “ → ”
                else closing = c;                                // ' 或 " 同字符闭合

                int end = body.IndexOf(closing, i + 1);
                if (end < 0) break;
                fields.Add(body.Substring(i + 1, end - i - 1));
                i = end + 1;
            }

            if (fields.Count > 0) state = fields[0] ?? "";
            if (fields.Count > 2) url = fields[2] ?? "";
            if (fields.Count > 4) msg = fields[4] ?? "";
        }

        /// <summary>压缩一段响应文本，用于日志（去换行、限 150 字符）。</summary>
        private static string Summarize(string text)
        {
            if (string.IsNullOrEmpty(text)) return "(空)";
            string s = text.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length <= 150 ? s : s.Substring(0, 150) + "…";
        }

        /// <summary>最近一次 QQ 授权链路的逐步轨迹（失败时诊断用）。</summary>
        private string _lastOauthTrace = "";

        /// <summary>
        /// QQ 扫码授权链路（r29，对照 Muonium 的 MuQQApi.authorizeQq）：
        ///  1. 从 ptqrlogin 返回的 check_sig URL 里抠 ptsigx + uin
        ///  2. GET check_sig（带 ptsigx）→ 种 p_skey
        ///  3. POST authorize（带 g_tk=hash33(p_skey,5381)）→ 302 到 redirect_uri 带 code=
        ///  4. 返回 code
        /// </summary>
        private async Task<string> AuthorizeQq(string checkSigUrl, CancellationToken ct)
        {
            var trace = new StringBuilder();
            try
            {
                // 1. 抠 ptsigx / uin
                string ptsigx = ExtractParam(checkSigUrl, "ptsigx");
                string uin = ExtractParam(checkSigUrl, "uin");
                if (string.IsNullOrEmpty(ptsigx) || string.IsNullOrEmpty(uin))
                {
                    _lastOauthTrace = "check_sig URL 里缺 ptsigx/uin：" + ShortHost(checkSigUrl);
                    return "";
                }
                trace.Append("ptsigx/uin ✓ ");

                // 2. check_sig → 种 p_skey
                string checkSig = "https://ssl.ptlogin2.graph.qq.com/check_sig"
                    + "?uin=" + uin + "&pttype=1&service=ptqrlogin&nodirect=0"
                    + "&ptsigx=" + Uri.EscapeDataString(ptsigx)
                    + "&s_url=" + Uri.EscapeDataString("https://graph.qq.com/oauth2.0/login_jump")
                    + "&ptlang=2052&ptredirect=100&aid=716027609&daid=383"
                    + "&j_later=0&low_login_hour=0&regmaster=0"
                    + "&pt_login_type=3&pt_aid=0&pt_aaid=16&pt_light=0"
                    + "&pt_3rd_aid=100497308";
                await GetRawAsync(checkSig, ct, PtReferer).ConfigureAwait(false);
                // g_tk 必须和实际发出的 Cookie 头用同一个 p_skey（r32：100046 根因）
                string pSkey;
                if (!FlattenCookies().TryGetValue("p_skey", out pSkey) || string.IsNullOrEmpty(pSkey))
                {
                    _lastOauthTrace = trace + "check_sig 后没拿到 p_skey cookie";
                    return "";
                }
                trace.Append("p_skey ✓(len=").Append(pSkey.Length).Append(") ");

                // 3. POST authorize
                string gtk = Hash33(pSkey, 5381).ToString();
                trace.Append("gtk=").Append(gtk).Append(' ');
                var form = new Dictionary<string, string>
                {
                    { "response_type", "code" },
                    { "client_id", "100497308" },
                    { "redirect_uri", "https://y.qq.com/portal/wx_redirect.html?login_type=1&surl=https://y.qq.com/" },
                    { "scope", "get_user_info,get_app_friends" },
                    { "state", "state" },
                    { "switch", "" },
                    { "from_ptlogin", "1" },
                    { "src", "1" },
                    { "update_auth", "1" },
                    { "openapi", "1010_1030" },
                    { "g_tk", gtk },
                    { "auth_time", ((long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds).ToString() },
                    { "ui", Guid.NewGuid().ToString() },
                };
                // 轨迹带上实际发出的 cookie 名单（只列名不列值），缺哪个一眼看出
                var names = new List<string>(FlattenCookies().Keys);
                names.Sort(StringComparer.Ordinal);
                trace.Append("cookies[").Append(string.Join(",", names.ToArray())).Append("] ");
                var authResp = await PostFormRedirectAsync(
                    "https://graph.qq.com/oauth2.0/authorize", form,
                    "https://graph.qq.com/", ct).ConfigureAwait(false);
                // 4. 从 Location / 正文里抠 code
                string code = ExtractCodeParam(authResp.Location) ?? ExtractCodeParam(authResp.Body);
                if (!string.IsNullOrEmpty(code))
                {
                    trace.Append("authorize→code ✓");
                    _lastOauthTrace = trace.ToString();
                    return code;
                }

                _lastOauthTrace = trace + "authorize 没回 code，Location=" +
                                  ((authResp.Location ?? "(无)").Length > 220
                                      ? authResp.Location.Substring(0, 220)
                                      : authResp.Location ?? "(无)") +
                                  " 正文前80字=" + (authResp.Body != null && authResp.Body.Length > 80
                                      ? authResp.Body.Substring(0, 80).Replace("\n", " ") : authResp.Body);
                return "";
            }
            catch (Exception e)
            {
                _lastOauthTrace = trace + "异常：" + e.Message;
                return "";
            }
        }

        /// <summary>从 URL query 里抠参数值。</summary>
        private static string ExtractParam(string url, string name)
        {
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(name)) return "";
            int p = url.IndexOf(name + "=", StringComparison.Ordinal);
            if (p < 0) return "";
            int start = p + name.Length + 1;
            int end = url.IndexOf('&', start);
            string v = end < 0 ? url.Substring(start) : url.Substring(start, end - start);
            return Uri.UnescapeDataString(v);
        }

        /// <summary>日志用：URL 压成 域名+路径前40字符。</summary>
        private static string ShortHost(string url)
        {
            if (string.IsNullOrEmpty(url)) return "(空)";
            int p = url.IndexOf("://", StringComparison.Ordinal);
            string s = p >= 0 ? url.Substring(p + 3) : url;
            int slash = s.IndexOf('/');
            string host = slash > 0 ? s.Substring(0, slash) : s;
            string path = slash > 0 ? s.Substring(slash, Math.Min(40, s.Length - slash)) : "";
            return host + path;
        }

        /// <summary>从 URL 或文本里抠出 code= 后面那段（到 & 或引号/空白为止）。</summary>
        private static string ExtractCodeParam(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            int idx = text.IndexOf("code=", StringComparison.Ordinal);
            if (idx < 0) return "";

            int start = idx + "code=".Length;
            int end = start;
            while (end < text.Length)
            {
                char c = text[end];
                if (c == '&' || c == '\'' || c == '"' || c == ',' || c == ')' || c == ']' ||
                    char.IsWhiteSpace(c)) break;
                end++;
            }
            return end > start ? text.Substring(start, end - start) : "";
        }

        /// <summary>用 OAuth code 走 musicu.fcg 的 QQLogin 换 musickey（tmeLoginType=2，QQ 扫码通道）。</summary>
        private async Task<string> MusicuLogin(string code, CancellationToken ct)
        {
            try
            {
                // 正确方法：QQConnectLogin.LoginServer / QQLogin，param 里是 code + strAppid
                // （对照 Muonium QQWechatLogin.exchange / MuQQApi.authorizeQq）
                string guid = _rng.Next(100000000, 999999999).ToString();
                string body =
                    "{\"req_0\":{\"module\":\"QQConnectLogin.LoginServer\",\"method\":\"QQLogin\"," +
                    "\"param\":{\"code\":\"" + code + "\",\"strAppid\":\"100497308\"}}," +
                    "\"comm\":{\"ct\":11,\"cv\":14090008,\"v\":14090008,\"chid\":\"10003505\"," +
                    "\"tmeAppID\":\"qqmusic\",\"QIMEI\":\"\",\"QIMEI36\":\"\",\"OpenUDID\":\"" + guid + "\"," +
                    "\"udid\":\"" + guid + "\",\"OpenUDID2\":\"" + guid + "\",\"aid\":\"" + guid + "\"," +
                    "\"os_ver\":\"10\",\"phonetype\":\"MI 6\",\"devicelevel\":\"29\",\"newdevicelevel\":\"29\"," +
                    "\"tmeLoginType\":2}}";

                string text = await PostJsonAsync(FcgUrl, body, ct).ConfigureAwait(false);
                var root = MiniJson.AsObject(MiniJson.Parse(text));
                var req = root != null ? MiniJson.Dict(root, "req_0") : null;
                var data = req != null ? MiniJson.Dict(req, "data") : null;

                if (data != null)
                {
                    string key = MiniJson.Str(data, "musickey", "");
                    if (!string.IsNullOrEmpty(key)) return key;
                }

                // 兜底：musickey 可能在 cookie 里
                return LookupCookie("musickey") ?? "";
            }
            catch
            {
                return "";
            }
        }

        // ──────────────────── QQ 音乐 App 扫码登录（r43） ────────────────────
        //
        // 与「手机 QQ 扫码」（ptlogin OAuth）平行的另一条登录通道：
        // 二维码是 QQ 音乐自己的授权码（music.login.LoginServer.CreateQRCode），
        // 只有 QQ 音乐 App 认得；扫码状态通过 MQTT 5（wss://mu.y.qq.com）推送。
        // 协议对照 luren-dc/QQMusicApi（2026-11-08 实测：CreateQRCode 通、MQTT 握手+订阅通）。
        //
        // 线程模型：AppQrBeginAsync 取码后起后台 MQTT 会话任务写 _appState；
        // 面板按老习惯每 2 秒调 AppQrPoll() 读快照——不动面板的轮询结构。

        private string _appQrId = "";
        private MqttWsClient _appMqtt;
        private CancellationTokenSource _appCts;
        private readonly object _appLock = new object();
        private QqLoginResult _appState = new QqLoginResult { State = QqLoginResult.QqLoginState.Unknown };

        /// <summary>QQ 音乐 App 扫码：第一步取二维码（PNG 字节），并启动后台 MQTT 监听。</summary>
        public Task<byte[]> AppQrBeginAsync(CancellationToken ct = default(CancellationToken))
        {
            return _throttle.RunAsync(async () =>
            {
                AppQrStop();

                // comm 用 ct:23（开放平台客户端），param 带版本号——实测这组即可拿到码
                string body =
                    // param.ct/cv 必须是安卓客户端档案(11/20090008)：服务端按它判「码属于哪个 App」，
                    // 用网页档案(24/4747474)创建的码会被 App 扫码侧 GetQRCodeStatus 拒绝
                    // （104610 "can NOT scan the qrCode with this APP, not in cts-white-list"），
                    // 手机端授权页取不到数据只显白屏、MQTT 也永远等不到 scanned 事件。
                    "{\"req_0\":{\"module\":\"music.login.LoginServer\",\"method\":\"CreateQRCode\"," +
                    "\"param\":{\"tmeAppID\":\"qqmusic\",\"ct\":11,\"cv\":20090008}}," +
                    "\"comm\":{\"ct\":23,\"cv\":0,\"tmeAppID\":\"qqmusic\"}}";

                string text = await PostJsonAsync(FcgUrl, body, ct).ConfigureAwait(false);
                var root = MiniJson.AsObject(MiniJson.Parse(text));
                var req = root != null ? MiniJson.Dict(root, "req_0") : null;
                var data = req != null ? MiniJson.Dict(req, "data") : null;
                string b64 = data != null ? MiniJson.Str(data, "qrcode", "") : "";
                string qrId = data != null ? MiniJson.Str(data, "qrcodeID", "") : "";
                if (string.IsNullOrEmpty(b64) || string.IsNullOrEmpty(qrId))
                    throw new InvalidOperationException("CreateQRCode 没返回二维码");

                _appQrId = qrId;
                SetAppState(new QqLoginResult { State = QqLoginResult.QqLoginState.Waiting });

                // qrcode 是 "data:image/png;base64,xxx" 形式，剥前缀解码
                int comma = b64.IndexOf(',');
                string pure = comma >= 0 ? b64.Substring(comma + 1) : b64;
                byte[] png = Convert.FromBase64String(pure);

                // 后台 MQTT 会话：长连接不占 throttle（throttle 是给短请求串行防封的）。
                // 显式丢弃 _ = 有意 fire-and-forget：状态经 _appState 回传，异常也在里面落状态。
                CancellationTokenSource cts = new CancellationTokenSource();
                lock (_appLock) { _appCts = cts; }
                string id = qrId;
                _ = Task.Run(() => RunAppMqttAsync(id, cts.Token));

                return png;
            }, ct);
        }

        /// <summary>App 扫码状态快照（后台 MQTT 任务写、面板轮询读）。</summary>
        public QqLoginResult AppQrPoll()
        {
            lock (_appLock)
            {
                return new QqLoginResult
                {
                    State = _appState.State,
                    CookieText = _appState.CookieText,
                    RawMessage = _appState.RawMessage,
                };
            }
        }

        /// <summary>停掉 App 扫码的后台 MQTT 会话（换二维码/换扫码方式/退出登录时调用）。</summary>
        public void AppQrStop()
        {
            CancellationTokenSource cts;
            MqttWsClient mqtt;
            lock (_appLock)
            {
                cts = _appCts; _appCts = null;
                mqtt = _appMqtt; _appMqtt = null;
            }
            try { if (cts != null) cts.Cancel(); } catch { }
            try { if (mqtt != null) mqtt.Dispose(); } catch { }
        }

        private void SetAppState(QqLoginResult r)
        {
            lock (_appLock) { _appState = r; }
        }

        /// <summary>MQTT 会话主循环：连接 → 订阅 → 等推送 → cookies 到手就换 musickey 落盘。</summary>
        private async Task RunAppMqttAsync(string qrId, CancellationToken ct)
        {
            try
            {
                var mqtt = new MqttWsClient();
                lock (_appLock) { _appMqtt = mqtt; }

                var headers = new Dictionary<string, string>
                {
                    { "Origin", "https://y.qq.com" },
                    { "Referer", "https://y.qq.com/" },
                    { "User-Agent", UserAgent },
                };
                var connProps = new Dictionary<string, string>
                {
                    { "tmeAppID", "qqmusic" },
                    { "business", "management" },
                    { "hashTag", qrId },
                    { "clientTag", "management.user" },
                    { "userID", qrId },
                };
                string clientId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString()
                                + _rng.Next(1000, 10000).ToString();
                await mqtt.ConnectAsync("mu.y.qq.com", 443, "/ws/handshake", headers, clientId, connProps, ct)
                          .ConfigureAwait(false);

                var subProps = new Dictionary<string, string>
                {
                    { "authorization", "tmelogin" },
                    { "pubsub", "unicast" },
                };
                await mqtt.SubscribeAsync("management.qrcode_login/" + qrId, subProps, ct).ConfigureAwait(false);

                while (!ct.IsCancellationRequested)
                {
                    MqttWsMessage msg = mqtt.ReceiveMessage(TimeSpan.FromSeconds(15));
                    if (msg == null)
                    {
                        // 空窗期发 PINGREQ 保活（keepalive 45s）
                        try { await mqtt.PingAsync(ct).ConfigureAwait(false); } catch { return; }
                        continue;
                    }

                    string type = "";
                    msg.UserProps.TryGetValue("type", out type);

                    if (type == "scanned")
                    {
                        SetAppState(new QqLoginResult { State = QqLoginResult.QqLoginState.Scanned });
                    }
                    else if (type == "canceled")
                    {
                        SetAppState(new QqLoginResult
                        {
                            State = QqLoginResult.QqLoginState.Failed,
                            RawMessage = "手机上取消了登录",
                        });
                        return;
                    }
                    else if (type == "timeout")
                    {
                        SetAppState(new QqLoginResult { State = QqLoginResult.QqLoginState.Expired });
                        return;
                    }
                    else if (type == "loginFailed")
                    {
                        string p = msg.Payload ?? "";
                        SetAppState(new QqLoginResult
                        {
                            State = QqLoginResult.QqLoginState.Failed,
                            RawMessage = "手机端登录失败" + (p.Length > 0 ? "：" + (p.Length > 60 ? p.Substring(0, 60) : p) : ""),
                        });
                        return;
                    }
                    else if (type == "cookies")
                    {
                        QqLoginResult r = await FinishAppLoginAsync(qrId, msg.Payload ?? "", ct).ConfigureAwait(false);
                        SetAppState(r);
                        return;
                    }
                    // 其它 type 忽略
                }
            }
            catch (OperationCanceledException) { /* 主动停止 */ }
            catch (Exception e)
            {
                SetAppState(new QqLoginResult
                {
                    State = QqLoginResult.QqLoginState.Failed,
                    RawMessage = "App 扫码通道异常：" + e.Message,
                });
            }
        }

        /// <summary>
        /// cookies 推送到手后的收尾：拿 qqmusic_uin/qqmusic_key，
        /// 再调 music.login.LoginServer.Login（tmeLoginType=6）换正式 musickey 落盘。
        /// Login 换票失败时用推送里的 key 兜底（它本身就是 qqmusic_key 票据）。
        /// </summary>
        private async Task<QqLoginResult> FinishAppLoginAsync(string qrId, string payload, CancellationToken ct)
        {
            var r = new QqLoginResult();
            try
            {
                var root = MiniJson.AsObject(MiniJson.Parse(payload));
                var cookies = root != null ? MiniJson.Dict(root, "cookies") : null;
                var uinObj = cookies != null ? MiniJson.Dict(cookies, "qqmusic_uin") : null;
                var keyObj = cookies != null ? MiniJson.Dict(cookies, "qqmusic_key") : null;
                string rawUin = uinObj != null ? MiniJson.Str(uinObj, "value", "") : "";
                string token = keyObj != null ? MiniJson.Str(keyObj, "value", "") : "";
                if (string.IsNullOrEmpty(rawUin) || string.IsNullOrEmpty(token))
                {
                    r.State = QqLoginResult.QqLoginState.Failed;
                    r.RawMessage = "cookies 推送里缺 qqmusic_uin / qqmusic_key";
                    return r;
                }

                // uin 规整成纯数字（对齐 RefreshUin 的剥 o 前缀/前导零语义）
                string digits = rawUin.Trim();
                if (digits.StartsWith("o") || digits.StartsWith("O")) digits = digits.Substring(1);
                digits = digits.TrimStart('0');
                if (digits.Length == 0) digits = "0";

                string musickey = token;
                try
                {
                    string body =
                        "{\"req_0\":{\"module\":\"music.login.LoginServer\",\"method\":\"Login\"," +
                        "\"param\":{\"musicid\":" + digits + ",\"qrCodeID\":\"" + qrId + "\",\"token\":\"" + token + "\"}}," +
                        "\"comm\":{\"ct\":23,\"cv\":0,\"tmeAppID\":\"qqmusic\",\"tmeLoginType\":6}}";
                    string text = await _throttle.RunAsync(() => PostJsonAsync(FcgUrl, body, ct), ct).ConfigureAwait(false);
                    var r2 = MiniJson.AsObject(MiniJson.Parse(text));
                    var req = r2 != null ? MiniJson.Dict(r2, "req_0") : null;
                    var data = req != null ? MiniJson.Dict(req, "data") : null;
                    string mk = data != null ? MiniJson.Str(data, "musickey", "") : "";
                    if (!string.IsNullOrEmpty(mk)) musickey = mk;
                }
                catch { /* 换票失败不致命，用推送的 key 兜底 */ }

                var jar = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                jar["uin"] = digits;
                jar["qqmusic_key"] = musickey;
                jar["musickey"] = musickey;
                string fullCookie = QqCookieStore.Normalize(jar);
                if (!string.IsNullOrEmpty(fullCookie)) SetCookie(fullCookie);

                r.State = QqLoginResult.QqLoginState.Success;
                r.CookieText = fullCookie;
            }
            catch (Exception e)
            {
                r.State = QqLoginResult.QqLoginState.Failed;
                r.RawMessage = "处理 cookies 推送失败：" + e.Message;
            }
            return r;
        }

        // ────────────────────────────── 取流 ──────────────────────────────

        public async Task<SongUrlResult> GetSongUrlAsync(SongInfo song,
                                                   CancellationToken ct = default(CancellationToken))
        {
            string mid = song != null
                ? (string.IsNullOrEmpty(song.SourceId) ? song.Id.ToString() : song.SourceId)
                : "";

            if (string.IsNullOrEmpty(mid))
            {
                return new SongUrlResult
                {
                    Code = -1,
                    Message = "缺少 QQ songmid",
                };
            }

            // 登录后可用 320k，匿名回落到 128k
            bool wantHi = HasLogin;
            SongUrlResult r = await GetSongUrlOnceAsync(song, mid, wantHi ? Mp3PrefixHi : Mp3Prefix, ct)
                .ConfigureAwait(false);

            // r41：M800(320k) 只是被高音质会员权限卡住时，降 M500(128k) 再试——
            // 低音质免费的歌不该因为登录了反而放不出来。真付费曲两个前缀都空 purl，无副作用。
            if (!r.Ok && wantHi)
            {
                SongUrlResult lo = await GetSongUrlOnceAsync(song, mid, Mp3Prefix, ct).ConfigureAwait(false);
                if (lo.Ok) return lo;
            }
            return r;
        }

        private Task<SongUrlResult> GetSongUrlOnceAsync(SongInfo song, string mid, string prefix, CancellationToken ct)
        {
            string uin = Uin;

            return _throttle.RunAsync(async () =>
            {
                string guid = _rng.Next(100000000, 999999999).ToString();
                string body =
                    "{\"req\":{\"module\":\"CDN.SrfCdnDispatchServer\",\"method\":\"GetCdnDispatch\"," +
                    "\"param\":{\"guid\":\"" + guid + "\",\"calltype\":0,\"userip\":\"\"}}," +
                    "\"req_0\":{\"module\":\"vkey.GetVkeyServer\",\"method\":\"CgiGetVkey\"," +
                    "\"param\":{\"guid\":\"" + guid + "\"," +
                    "\"songmid\":[\"" + mid + "\"],\"songtype\":[0]," +
                    "\"filename\":[\"" + prefix + mid + ".mp3\"]," +
                    "\"uin\":\"" + uin + "\",\"loginflag\":1,\"platform\":\"20\"}}}";

                // r44：手动带 Cookie 头（见 _cookielessHttp 注释），别再走 Mono 的容器
                string text = await PostJsonWithCookieAsync(FcgUrl, body, CookieText, ct).ConfigureAwait(false);

                var root = MiniJson.AsObject(MiniJson.Parse(text));
                var res = root != null ? MiniJson.Dict(root, "req_0") : null;
                var data = res != null ? MiniJson.Dict(res, "data") : null;
                if (data == null)
                {
                    return new SongUrlResult
                    {
                        Id = song != null ? song.Id : 0,
                        Code = -500,
                        Message = "QQ 取流响应异常",
                    };
                }

                string sip = "";
                var sips = MiniJson.List(data, "sip");
                if (sips != null && sips.Count > 0) sip = Convert.ToString(sips[0]);

                string purl = "";
                var infos = MiniJson.List(data, "midurlinfo");
                if (infos != null && infos.Count > 0)
                {
                    var first = MiniJson.AsObject(infos[0]);
                    if (first != null) purl = MiniJson.Str(first, "purl", "");
                }

                if (string.IsNullOrEmpty(purl))
                {
                    // purl 为空 = 付费/会员/无版权。和网易的 -110 语义一致，面板按同一条提示处理。
                    // |resp= 附上原始响应片段（百来字，远离 BepInEx 单行 2000 字上限），排查取流被拒用。
                    string snippet = (text ?? "").Replace("\r", " ").Replace("\n", " ");
                    if (snippet.Length > 140) snippet = snippet.Substring(0, 140);
                    return new SongUrlResult
                    {
                        Id = song != null ? song.Id : 0,
                        Code = -110,
                        Fee = song != null ? song.Fee : 1,
                        Message = "QQ 音乐未返回直链（会员曲 / 付费曲 / 无版权）|resp=" + snippet,
                    };
                }

                string full = sip.TrimEnd('/') + "/" + purl.TrimStart('/');
                full = NeteaseClient.ForceHttps(full);   // Unity 6 禁止明文 http，必须升

                return new SongUrlResult
                {
                    Id = song != null ? song.Id : 0,
                    Url = full,
                    Code = 200,
                    Br = prefix == Mp3PrefixHi ? 320000 : 128000,
                    Level = prefix == Mp3PrefixHi ? "320k" : "128k",
                    Fee = song != null ? song.Fee : 0,
                };
            }, ct);
        }

        // ────────────────────────────── 歌词 ──────────────────────────────

        public Task<LyricResult> GetLyricAsync(SongInfo song,
                                               CancellationToken ct = default(CancellationToken))
        {
            string mid = song != null
                ? (string.IsNullOrEmpty(song.SourceId) ? song.Id.ToString() : song.SourceId)
                : "";

            long id = song != null ? song.Id : 0;

            if (string.IsNullOrEmpty(mid))
                return Task.FromResult(new LyricResult { Id = id });

            return _throttle.RunAsync(async () =>
            {
                string url = LyricUrl
                    + "?songmid=" + Uri.EscapeDataString(mid)
                    + "&format=json&nobase64=0&g_tk=5381&loginUin=0&hostUin=0"
                    + "&platform=yqq&needNewCode=0";

                string text = await GetAsync(url, ct).ConfigureAwait(false);

                var root = MiniJson.AsObject(MiniJson.Parse(text));
                if (root == null) return new LyricResult { Id = id };

                return new LyricResult
                {
                    Id = id,
                    Lrc = DecodeBase64(MiniJson.Str(root, "lyric", "")),
                    Translated = DecodeBase64(MiniJson.Str(root, "trans", "")),
                };
            }, ct);
        }

        /// <summary>QQ 的 lyric / trans 字段是 base64(UTF-8)，空串或非法输入统一返回空字符串。</summary>
        private static string DecodeBase64(string b64)
        {
            if (string.IsNullOrEmpty(b64)) return "";
            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(b64));
            }
            catch
            {
                // 极少数情况会直接给明文 LRC，此时原样返回反而更好
                return b64.IndexOf('[') >= 0 ? b64 : "";
            }
        }

        // ────────────────────────────── HTTP ──────────────────────────────

        private async Task<string> GetAsync(string url, CancellationToken ct)
        {
            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            {
                req.Headers.TryAddWithoutValidation("Referer", Referer);
                using (var resp = await _http.SendAsync(req, ct).ConfigureAwait(false))
                {
                    resp.EnsureSuccessStatusCode();
                    return await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                }
            }
        }

        /// <summary>GET 返回原始字符串（不校验状态码，登录轮询的 ptqrlogin 偶发非 200）。</summary>
        private async Task<string> GetRawAsync(string url, CancellationToken ct, string referer = null)
        {
            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            {
                req.Headers.TryAddWithoutValidation("Referer", referer ?? Referer);
                using (var resp = await _http.SendAsync(req, ct).ConfigureAwait(false))
                {
                    return await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// ptlogin 域专用 GET：带 xui Referer，并把响应头里的 Set-Cookie 原文存到
        /// <see cref="_lastSetCookie"/>，供 qrsig / pt_login_sig 兜底解析。
        /// r24 实测教训：CookieContainer 对 ssl.ptlogin2.qq.com 的 host-only cookie
        /// 取不可靠，qrsig 必须直接从响应头拿。
        /// </summary>
        private async Task<byte[]> GetWithCaptureAsync(string url, CancellationToken ct)
        {
            _lastSetCookie = "";
            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            {
                req.Headers.TryAddWithoutValidation("Referer", PtReferer);
                using (var resp = await _http.SendAsync(req, ct).ConfigureAwait(false))
                {
                    try
                    {
                        if (resp.Headers.Contains("Set-Cookie"))
                            _lastSetCookie = string.Join("\n", resp.Headers.GetValues("Set-Cookie"));
                    }
                    catch { }
                    return await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                }
            }
        }

        /// <summary>从 Set-Cookie 原文（多行）里抠 name=value 的 value。</summary>
        private static string ExtractSetCookie(string setCookieText, string name)
        {
            if (string.IsNullOrEmpty(setCookieText) || string.IsNullOrEmpty(name)) return null;
            foreach (string line in setCookieText.Split('\n'))
            {
                int p = line.IndexOf(name + "=", StringComparison.Ordinal);
                if (p != 0 && !(p > 0 && (line[p - 1] == ' ' || line[p - 1] == ';'))) continue;
                int start = p + name.Length + 1;
                int end = line.IndexOf(';', start);
                string v = (end < 0 ? line.Substring(start) : line.Substring(start, end - start)).Trim();
                if (v.Length > 0) return v;
            }
            return null;
        }

        /// <summary>GET 返回二进制（二维码图片）。</summary>
        private async Task<byte[]> GetBinaryAsync(string url, CancellationToken ct)
        {
            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            {
                req.Headers.TryAddWithoutValidation("Referer", Referer);
                using (var resp = await _http.SendAsync(req, ct).ConfigureAwait(false))
                {
                    resp.EnsureSuccessStatusCode();
                    return await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                }
            }
        }

        private async Task<string> PostJsonAsync(string url, string body, CancellationToken ct)
        {
            using (var content = new StringContent(body, Encoding.UTF8, "application/json"))
            using (var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content })
            {
                req.Headers.TryAddWithoutValidation("Referer", "https://y.qq.com/");
                using (var resp = await _http.SendAsync(req, ct).ConfigureAwait(false))
                {
                    resp.EnsureSuccessStatusCode();
                    return await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// 带手动 Cookie 头的 JSON POST（r44）。取流等敏感请求专用——绕开 Mono 的
        /// CookieContainer（游戏里会把登录 cookie 发坏/漏发），Cookie 头由调用方拼好。
        /// </summary>
        private async Task<string> PostJsonWithCookieAsync(string url, string body, string cookieHeader, CancellationToken ct)
        {
            using (var content = new StringContent(body, Encoding.UTF8, "application/json"))
            using (var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content })
            {
                req.Headers.TryAddWithoutValidation("Referer", "https://y.qq.com/");
                if (!string.IsNullOrEmpty(cookieHeader))
                    req.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
                using (var resp = await _cookielessHttp.SendAsync(req, ct).ConfigureAwait(false))
                {
                    resp.EnsureSuccessStatusCode();
                    return await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                }
            }
        }

        /// <summary>authorize 授权响应的 Location + 正文（不跟随重定向，code 在 Location 里）。</summary>
        private sealed class RedirectResult
        {
            public string Location;
            public string Body;
        }

        /// <summary>
        /// 把容器里所有 qq.com 相关 cookie 拍平成 "k=v; k=v" 的 Cookie 头。
        /// 模拟 Muonium 的不分域扁平 jar：p_skey 种在 ssl.ptlogin2.graph.qq.com，
        /// authorize（graph.qq.com）也必须带上它，否则 g_tk 校验失败甩 error 页。
        /// </summary>
        /// <summary>
        /// 把容器里所有 qq.com 相关 cookie 拍平成一份（按名去重，后面的域覆盖前面——
        /// 新鲜的登录 cookie 种在更具体的子域上，应该赢过之前保存的旧 cookie）。
        /// r32：g_tk 和 Cookie 头必须用同一份值，否则 100046（g_tk 校验失败）。
        /// </summary>
        private Dictionary<string, string> FlattenCookies()
        {
            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            // r33：具体 host 排前面 + 先到先得——host-only 的新登录态优先于 .qq.com 泛域旧值。
            // xui.ptlogin2.qq.com 必须收（pt_login_sig 种在这，漏了 authorize 会缺登录凭据）。
            foreach (Uri root in new[]
            {
                new Uri("https://ssl.ptlogin2.graph.qq.com/"),
                new Uri("https://graph.qq.com/"),
                new Uri("https://ssl.ptlogin2.qq.com/"),
                new Uri("https://ptlogin2.qq.com/"),
                new Uri("https://xui.ptlogin2.qq.com/"),
                new Uri("https://y.qq.com/"),
                new Uri("https://qq.com/"),
            })
            {
                try
                {
                    foreach (Cookie c in _cookies.GetCookies(root))
                    {
                        if (string.IsNullOrEmpty(c.Name) || string.IsNullOrEmpty(c.Value)) continue;
                        if (!dict.ContainsKey(c.Name)) dict[c.Name] = c.Value;
                    }
                }
                catch { }
            }
            return dict;
        }

        private string BuildCookieHeader()
        {
            var sb = new StringBuilder();
            foreach (var kv in FlattenCookies())
            {
                if (sb.Length > 0) sb.Append("; ");
                sb.Append(kv.Key).Append('=').Append(kv.Value);
            }
            return sb.ToString();
        }

        /// <summary>
        /// POST 表单（graph.qq.com/oauth2.0/authorize），关掉自动重定向，拿 Location 里的 code=。
        /// authorize 是 POST 不是 GET——用 GET 会一直被甩回登录页（r28 之前踩的坑）。
        /// </summary>
        private async Task<RedirectResult> PostFormRedirectAsync(
            string url, Dictionary<string, string> form, string referer, CancellationToken ct)
        {
            // UseCookies=false + 手动 Cookie 头（r30）：Muonium 用不分域的扁平 jar，
            // check_sig 种的 p_skey 会随 authorize 请求发出；.NET 的 CookieContainer
            // 把 p_skey 锁在 ssl.ptlogin2.graph.qq.com，authorize（graph.qq.com）带不上，
            // 服务端验 g_tk 失败 → 302 show?which=error。
            using (var handler = new HttpClientHandler
            {
                UseCookies = false,
                AllowAutoRedirect = false,
            })
            using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) })
            {
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
                client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", referer);

                using (var content = new FormUrlEncodedContent(form))
                using (var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content })
                {
                    string ck = BuildCookieHeader();
                    if (ck.Length > 0)
                        req.Headers.TryAddWithoutValidation("Cookie", ck);
                    using (var resp = await client.SendAsync(req, ct).ConfigureAwait(false))
                {
                    var r = new RedirectResult();
                    var loc = resp.Headers.Location;
                    if (loc != null) r.Location = loc.IsAbsoluteUri ? loc.ToString()
                        : new Uri(new Uri(url), loc).ToString();
                    r.Body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    return r;
                }
            }
            }
        }

        /// <summary>
        /// QQ 的 songmid 是 14 位 base62 字符串，塞不进 long。这里做一个稳定的 63 位哈希，
        /// 保证同一首 QQ 歌每次搜索得到同一个 Id —— 列表选中态、封面缓存键都靠它。
        /// </summary>
        private static long StableId(string mid)
        {
            unchecked
            {
                ulong h = 1469598103934665603UL;               // FNV-1a 64
                for (int i = 0; i < mid.Length; i++)
                {
                    h ^= mid[i];
                    h *= 1099511628211UL;
                }
                return (long)(h & 0x7FFFFFFFFFFFFFFFUL);        // 砍掉符号位，永远是正数
            }
        }

        public void Dispose()
        {
            try { _http.Dispose(); } catch { }
        }
    }
}
