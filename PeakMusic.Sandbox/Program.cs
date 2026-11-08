using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using PeakMusic.Core;
using PeakMusic.Core.Netease;
using PeakMusic.Core.Sources;

namespace PeakMusic.Sandbox
{
    /// <summary>
    /// 命令行沙盒。协议层所有东西先在这里跑通，再去碰 Unity。
    /// 游戏冷启动 30–60 秒，这里改一行 2 秒出结果——六成调试时间省在这。
    ///
    /// 用法（阶段 1）：
    ///   PeakMusic.Sandbox.exe env
    ///   PeakMusic.Sandbox.exe url  &lt;任意音频直链&gt;
    ///   PeakMusic.Sandbox.exe netease &lt;songId&gt;
    ///   PeakMusic.Sandbox.exe speed &lt;直链&gt; [字节数]
    ///
    /// 用法（阶段 2）：
    ///   PeakMusic.Sandbox.exe search &lt;关键词&gt; [数量]
    ///   PeakMusic.Sandbox.exe song  &lt;songId&gt; [level]
    ///   PeakMusic.Sandbox.exe lyric &lt;songId&gt;
    ///   PeakMusic.Sandbox.exe login           扫码登录（打印链接，浏览器打开即可）
    ///   PeakMusic.Sandbox.exe me              当前账号
    ///   PeakMusic.Sandbox.exe logout          清掉本地登录态
    /// </summary>
    internal static class Program
    {
        private const string GameDir = @"D:\Steam\steamapps\common\PEAK";

        private static int Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;

            string cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "env";
            try
            {
                switch (cmd)
                {
                    case "env": return CmdEnv();
                    case "url":
                        if (args.Length < 2) { Console.WriteLine("用法: url <直链>"); return 2; }
                        return CmdProbe(args[1], "自定义直链");
                    case "netease":
                        if (args.Length < 2) { Console.WriteLine("用法: netease <songId>"); return 2; }
                        return CmdProbe(NeteaseOuterUrl(args[1]), "网易外链 songId=" + args[1]);
                    case "speed":
                        if (args.Length < 2) { Console.WriteLine("用法: speed <直链> [字节数]"); return 2; }
                        int n = args.Length > 2 ? int.Parse(args[2]) : 2 * 1024 * 1024;
                        return CmdSpeed(args[1], n);
                    case "search": return CmdSearch(args);
                    case "song": return CmdSong(args);
                    case "lyric": return CmdLyric(args);
                    case "login": return CmdLogin();
                    case "setcookie":
                        if (args.Length < 2) { Console.WriteLine("用法: setcookie \"MUSIC_U=xxxx; __csrf=yyyy\""); return 2; }
                        return CmdSetCookie(args[1]);
                    case "me": return CmdMe();
                    case "logout": return CmdLogout();
                    case "qq": return CmdQq(args);
                    case "src": return CmdSources(args);
                    case "qrtest": return CmdQrTest(args);
                    case "pngtest": return CmdPngTest(args);
                    default:
                        Console.WriteLine("未知命令: " + cmd);
                        Console.WriteLine("阶段1: env | url <直链> | netease <songId> | speed <直链> [字节数]");
                        Console.WriteLine("阶段2: search <关键词> [数量] | song <songId> [level] | lyric <songId> | login | setcookie <cookie> | me | logout");
                        Console.WriteLine("阶段4: qq search <关键词> | qq song <songmid> | qq lyric <songmid>");
                        Console.WriteLine("       src search <netease|qq> <关键词>   —— 走统一多音源入口");
                        return 2;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[异常] " + ex);
                return 1;
            }
        }

        private static int CmdEnv()
        {
            Console.WriteLine("== 运行时 ==");
            Console.WriteLine("CLR          : " + Environment.Version);
            Console.WriteLine("OS           : " + Environment.OSVersion);
            Console.WriteLine("64-bit       : " + Environment.Is64BitProcess);
            Console.WriteLine("工作目录     : " + Environment.CurrentDirectory);
            Console.WriteLine();
            Console.WriteLine("== 游戏侧依赖 ==");
            string[] must =
            {
                @"PEAK_Data\Managed\Assembly-CSharp.dll",
                @"PEAK_Data\Managed\UnityEngine.CoreModule.dll",
                @"PEAK_Data\Managed\UnityEngine.AudioModule.dll",
                @"PEAK_Data\Managed\UnityEngine.UnityWebRequestAudioModule.dll",
                @"BepInEx\core\BepInEx.dll",
                @"winhttp.dll",
            };
            bool allOk = true;
            foreach (string rel in must)
            {
                bool ok = File.Exists(Path.Combine(GameDir, rel));
                allOk &= ok;
                Console.WriteLine((ok ? "[ok]   " : "[miss] ") + rel);
            }
            Console.WriteLine(allOk ? "=> 依赖齐全" : "=> 有缺失，先修环境再往下走");
            Console.WriteLine();

            Console.WriteLine("== 节流器自检（串行 + 随机抖动）==");
            using (var throttle = new Throttle(200, 500))
            {
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < 3; i++)
                {
                    Task<int> t = throttle.RunAsync(async () =>
                    {
                        await Task.Delay(50).ConfigureAwait(false);
                        return 1;
                    });
                    t.GetAwaiter().GetResult();
                    Console.WriteLine("  第 " + (i + 1) + " 次完成 @" + sw.ElapsedMilliseconds + "ms");
                }
                Console.WriteLine("=> 3 次共 " + sw.ElapsedMilliseconds + "ms（应 >= 3×(200+50)=750ms，且是严格串行）");
            }
            return 0;
        }

        private static string NeteaseOuterUrl(string songId)
        {
            return "https://music.163.com/song/media/outer/url?id=" + songId + ".mp3";
        }

        // ═══════════════════════ 阶段 2：协议层 ═══════════════════════

        private static int CmdSearch(string[] args)
        {
            if (args.Length < 2) { Console.WriteLine("用法: search <关键词> [数量]"); return 2; }
            string keyword = args[1];
            int limit = 30;
            if (args.Length > 2) int.TryParse(args[2], out limit);

            using (var client = new NeteaseClient())
            {
                Console.WriteLine("== 搜索: " + keyword + "（limit=" + limit + "）");
                Console.WriteLine("登录态: " + DescribeLogin(client));
                Console.WriteLine();

                var songs = client.SearchAsync(keyword, limit).GetAwaiter().GetResult();
                if (songs.Count == 0)
                {
                    Console.WriteLine("没搜到。可能是关键词、也可能是接口变了——先看上面有没有异常。");
                    return 0;
                }

                for (int i = 0; i < songs.Count; i++)
                {
                    SongInfo s = songs[i];
                    string mark = s.PrivilegeSt < 0 ? "  ×无版权" : (s.Fee == 0 ? "" : "  $付费");
                    Console.WriteLine(string.Format("{0,2}. {1}{2}", i + 1, s, mark));
                }

                Console.WriteLine();
                Console.WriteLine("取播放地址:  song <id>        看歌词:  lyric <id>");
                Console.WriteLine("提示：不要批量取播放地址（每首都会真实请求一次，是封号主因）。");
            }
            return 0;
        }

        private static int CmdSong(string[] args)
        {
            if (args.Length < 2) { Console.WriteLine("用法: song <songId> [level]"); return 2; }
            long id = long.Parse(args[1]);
            string level = args.Length > 2 ? args[2] : "exhigh";

            using (var client = new NeteaseClient())
            {
                Console.WriteLine("== 播放地址 songId=" + id + " level=" + level);
                Console.WriteLine("登录态: " + DescribeLogin(client));

                var sw = Stopwatch.StartNew();
                SongUrlResult r = client.GetSongUrlAsync(id, level).GetAwaiter().GetResult();
                sw.Stop();

                Console.WriteLine("耗时   : " + sw.ElapsedMilliseconds + "ms");
                Console.WriteLine("结果   : " + r.Explain());

                if (!r.Ok)
                {
                    Console.WriteLine();
                    if (r.Code == -110)
                    {
                        if (client.HasLogin)
                        {
                            // 实测：登录态有效时仍 -110，就是这首歌本身没权限——
                            // 常见原因是账号不是黑胶VIP，或这首歌要单曲/专辑购买，或地区无版权。
                            // 换音质等级（standard/exhigh/higher）不会有帮助，已验证。
                            Console.WriteLine("=> 已登录但这首歌不给播放地址。三种可能：");
                            Console.WriteLine("   a) 账号不是黑胶VIP（会员曲一律拿不到）");
                            Console.WriteLine("   b) 这首歌要单曲/数字专辑购买");
                            Console.WriteLine("   c) 当前地区无版权");
                            Console.WriteLine("   换音质等级无效（standard 也试过）。先 `search` 找同名的免费版本。");
                        }
                        else
                        {
                            Console.WriteLine("=> 没登录，会员曲一律 -110。先 `login` 或 `setcookie`。");
                        }
                    }
                    return 3;
                }

                Console.WriteLine("直链   : " + r.Url);
                Console.WriteLine();

                // 文档的阶段 2 验收要求"curl 这条链接能下到文件"——这里直接代劳
                int probe = CmdProbe(r.Url, "播放直链（songId=" + id + "）");

                Console.WriteLine();
                Console.WriteLine("== 歌词 ==");
                var ly = client.GetLyricAsync(id).GetAwaiter().GetResult();
                EchoLyric(ly, 6);

                return probe;
            }
        }

        private static int CmdLyric(string[] args)
        {
            if (args.Length < 2) { Console.WriteLine("用法: lyric <songId>"); return 2; }
            long id = long.Parse(args[1]);

            using (var client = new NeteaseClient())
            {
                var ly = client.GetLyricAsync(id).GetAwaiter().GetResult();
                Console.WriteLine("== 歌词 songId=" + id);
                if (!ly.HasLyric)
                {
                    Console.WriteLine("没有歌词（纯音乐/未收录）。");
                    return 0;
                }
                EchoLyric(ly, 20);
                Console.WriteLine();
                Console.WriteLine("翻译歌词: " + (string.IsNullOrEmpty(ly.Translated) ? "无" : "有（" + ly.Translated.Length + " 字符）"));
            }
            return 0;
        }

        private static void EchoLyric(LyricResult ly, int maxLines)
        {
            if (!ly.HasLyric) { Console.WriteLine("（无歌词）"); return; }

            LrcDocument doc = LrcDocument.Parse(ly.Lrc);
            Console.WriteLine("行数   : " + doc.Lines.Count + "（解析后，已丢掉时间戳）");
            int n = 0;
            foreach (LrcLine line in doc.Lines)
            {
                if (n++ >= maxLines) { Console.WriteLine("…"); break; }
                Console.WriteLine("  " + line.Time.ToString(@"mm\:ss\.ff") + "  " + line.Text);
            }
        }

        private static string DescribeLogin(NeteaseClient client)
        {
            if (!client.HasLogin) return "无（匿名 —— 只能搜索/看歌词，取不到播放地址）";
            return "已登录  cookie=" + CookieStore.Mask(client.CookieText);
        }

        /// <summary>
        /// 扫码登录。不碰账号密码：拿 unikey → 打印链接 → 你在浏览器/手机打开并扫码 → 轮询到 803 → cookie 落盘。
        /// </summary>
        private static int CmdLogin()
        {
            using (var client = new NeteaseClient())
            {
                Console.WriteLine("== 扫码登录");
                if (client.HasLogin)
                    Console.WriteLine("当前已有登录态: " + CookieStore.Mask(client.CookieText) + "（继续登录会覆盖）");

                string key = client.QrBeginAsync().GetAwaiter().GetResult();
                string url = "https://music.163.com/login?codekey=" + key;

                Console.WriteLine();
                Console.WriteLine("1) 复制下面这条链接，用浏览器打开（页面里会显示二维码）");
                Console.WriteLine("   " + url);
                Console.WriteLine("2) 打开网易云 App → 右上角扫一扫 → 确认登录");
                Console.WriteLine("3) 这个窗口会自动继续，最多等 300 秒");
                Console.WriteLine();
                Console.WriteLine("（也可以把链接发到手机上直接打开，App 会拉起授权）");
                Console.WriteLine("会话 cookie: " + CookieStore.Mask(client.CookieText) + "  ← 必须含 NMTID，否则轮询会被风控");
                Console.WriteLine();

                QrState last = QrState.Unknown;
                var deadline = DateTime.UtcNow.AddSeconds(300);

                while (DateTime.UtcNow < deadline)
                {
                    QrLoginResult r = client.QrPollAsync(key).GetAwaiter().GetResult();
                    if (r.State != last)
                    {
                        last = r.State;
                        Console.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] code=" + r.Code + " " + r.Explain());
                    }

                    if (r.State == QrState.RiskBlocked)
                    {
                        Console.WriteLine();
                        Console.WriteLine("=> 不是二维码的问题，重扫也没用。两条路：");
                        Console.WriteLine("   a) 浏览器登录 music.163.com → F12 → Application → Cookies → 复制 MUSIC_U 的值");
                        Console.WriteLine("      然后：setcookie \"MUSIC_U=粘贴值\"");
                        Console.WriteLine("   b) 不登录也能用：免费歌照样拿 320k 直链，只有会员曲拿不到");
                        return 6;
                    }

                    if (r.State == QrState.Success)
                    {
                        Console.WriteLine();
                        Console.WriteLine("cookie 已保存到: " + CookieStore.FilePath);
                        Console.WriteLine("内容(打码): " + CookieStore.Mask(r.CookieText));
                        var acc = client.GetAccountAsync().GetAwaiter().GetResult();
                        if (acc.LoggedIn)
                        {
                            Console.WriteLine("账号: " + acc.Nickname + "（uid=" + acc.UserId + "）");
                            Console.WriteLine("头像: " + acc.AvatarUrl);
                        }
                        else
                        {
                            Console.WriteLine("警告：登录返回成功但 account/get 没拿到 profile，cookie 可能不完整。");
                        }
                        return 0;
                    }

                    if (r.State == QrState.Expired)
                    {
                        Console.WriteLine("二维码过期了，重新跑一次 login。");
                        return 4;
                    }

                    Thread.Sleep(3000);
                }

                Console.WriteLine("等超时了（300 秒）。重新跑一次 login。");
                return 5;
            }
        }

        /// <summary>
        /// 手动写入登录态。绕开扫码风控的兜底路：
        /// 浏览器登录 music.163.com → F12 → Application → Cookies → 复制 MUSIC_U 的值。
        /// 写进去后立刻打一次账号接口验证，别存进去一个死 cookie 就当成功。
        /// </summary>
        private static int CmdSetCookie(string cookieText)
        {
            using (var client = new NeteaseClient())
            {
                client.SetCookie(cookieText);
                Console.WriteLine("已写入: " + CookieStore.FilePath);
                Console.WriteLine("内容(打码): " + CookieStore.Mask(client.CookieText));

                if (!client.HasLogin)
                {
                    Console.WriteLine("=> 里面没有 MUSIC_U，不算登录态。至少要 MUSIC_U 一项。");
                    return 3;
                }

                var acc = client.GetAccountAsync().GetAwaiter().GetResult();
                if (!acc.LoggedIn)
                {
                    Console.WriteLine("=> MUSIC_U 有了但账号接口不认：cookie 过期或值被截断了。");
                    return 4;
                }
                Console.WriteLine("账号: " + acc.Nickname + "（uid=" + acc.UserId + "）");
                Console.WriteLine("=> 登录态有效");
            }
            return 0;
        }

        private static int CmdMe()
        {
            using (var client = new NeteaseClient())
            {
                Console.WriteLine("== 当前登录态");
                Console.WriteLine("cookie 文件: " + CookieStore.FilePath + (File.Exists(CookieStore.FilePath) ? "（存在）" : "（不存在）"));
                Console.WriteLine("cookie 内容: " + CookieStore.Mask(client.CookieText));

                if (!client.HasLogin)
                {
                    Console.WriteLine("=> 没有 MUSIC_U，等于未登录。跑 `login` 或手动写入 cookie。");
                    return 3;
                }

                var acc = client.GetAccountAsync().GetAwaiter().GetResult();
                if (!acc.LoggedIn)
                {
                    Console.WriteLine("=> cookie 里有 MUSIC_U 但账号接口没认，可能已过期。重新 login。");
                    return 4;
                }
                Console.WriteLine("账号: " + acc.Nickname + "（uid=" + acc.UserId + "）");
                Console.WriteLine("头像: " + acc.AvatarUrl);
                Console.WriteLine("=> 登录态有效");
            }
            return 0;
        }

        private static int CmdLogout()
        {
            CookieStore.Clear();
            Console.WriteLine("已清掉 " + CookieStore.FilePath);
            return 0;
        }

        /// <summary>
        /// 二维码编码器的对拍测试：把 C# 生成的矩阵和 python-qrcode 生成的参考矩阵
        /// 逐模块比对（3 组文本 × 8 种掩码 = 24 个矩阵）。
        /// 二维码这种东西"看着像"没有意义，扫不出来就是白干，所以必须逐位对。
        /// </summary>
        /// <summary>
        /// 验证 PngDecoder：读一张内容已知的 1-bit 灰度 PNG（行滤波轮换 0/1/2/3/4），
        /// 解码后与期望矩阵逐像素对比。
        /// 用法: pngtest <目录含 test1bit.png + expected.txt>
        /// </summary>
        private static int CmdPngTest(string[] args)
        {
            string dir = args.Length > 1
                ? args[1]
                : @"C:\Users\Admin\WorkBuddy\2026-11-07-20-32-32\_qrtest";
            string pngName = args.Length > 2 ? args[2] : "test1bit.png";
            string expName = args.Length > 3 ? args[3] : "expected.txt";
            string pngFile = Path.Combine(dir, pngName);
            string expFile = Path.Combine(dir, expName);
            if (!File.Exists(pngFile) || !File.Exists(expFile))
            {
                Console.WriteLine("缺文件: " + pngFile + " 或 " + expFile);
                return 2;
            }

            byte[] png = File.ReadAllBytes(pngFile);
            var img = PngDecoder.Decode(png);
            if (img == null) { Console.WriteLine("Decode 返回 null"); return 1; }
            Console.WriteLine("解码成功: " + img.Width + "x" + img.Height + " RGBA " + img.Pixels.Length + " 字节");

            string[] rows = File.ReadAllLines(expFile);
            if (img.Width != rows[0].Length || img.Height != rows.Length)
            {
                Console.WriteLine("尺寸不符: 期望 " + rows[0].Length + "x" + rows.Length);
                return 1;
            }

            int diff = 0, firstBadX = -1, firstBadY = -1;
            for (int y = 0; y < img.Height; y++)
            {
                for (int x = 0; x < img.Width; x++)
                {
                    int p = (y * img.Width + x) * 4;
                    int v = img.Pixels[p];              // 灰度 r=g=b
                    int want = rows[y][x] == '1' ? 255 : 0;
                    if (Math.Abs(v - want) > 0)
                    {
                        diff++;
                        if (firstBadX < 0) { firstBadX = x; firstBadY = y; }
                    }
                }
            }
            Console.WriteLine("像素差异: " + diff + " / " + (img.Width * img.Height) +
                              (diff > 0 ? "（首个错位 x=" + firstBadX + " y=" + firstBadY + "）" : "  —— 完全一致"));
            return diff == 0 ? 0 : 1;
        }

        private static int CmdQrTest(string[] args)
        {
            string dir = args.Length > 1
                ? args[1]
                : Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    @"..\..\..\..\tools\qr_tests"));

            if (!Directory.Exists(dir))
            {
                Console.WriteLine("参考矩阵目录不存在：" + dir);
                Console.WriteLine("先跑： python tools/qr_reference.py matrices");
                return 2;
            }

            string hex = "";
            for (int i = 0; i < 4; i++) hex += "0123456789abcdef";

            var cases = new Dictionary<string, string>();
            cases["url"] = "https://music.163.com/login?codekey=" + hex;
            cases["ascii"] = "HELLO PEAK 123";
            cases["cjk"] = "网易云音乐扫码登录测试";

            int ok = 0, bad = 0;
            foreach (var kv in cases)
            {
                for (int mask = 0; mask < 8; mask++)
                {
                    string file = Path.Combine(dir, kv.Key + "_m" + mask + ".txt");
                    if (!File.Exists(file)) { Console.WriteLine("缺文件: " + file); bad++; continue; }

                    string[] lines = File.ReadAllLines(file);
                    int refVer = int.Parse(lines[0].Substring("version=".Length));

                    var qr = PeakMusic.Core.Qr.QrEncoder.Encode(kv.Value, mask);
                    if (qr.Version != refVer)
                    {
                        Console.WriteLine("✗ " + kv.Key + " mask" + mask + " 版本不一致：C#=" + qr.Version + " ref=" + refVer);
                        bad++;
                        continue;
                    }

                    int diff = 0;
                    string firstBad = "";
                    for (int y = 0; y < qr.Size; y++)
                    {
                        string row = lines[2 + y];
                        for (int x = 0; x < qr.Size; x++)
                        {
                            bool want = row[x] == '1';
                            if (qr.Dark(x, y) != want)
                            {
                                diff++;
                                if (firstBad.Length == 0) firstBad = "(" + x + "," + y + ")";
                            }
                        }
                    }

                    if (diff == 0) { ok++; }
                    else
                    {
                        bad++;
                        Console.WriteLine("✗ " + kv.Key + " mask" + mask + " V" + qr.Version +
                                          " 有 " + diff + " 个模块不一致，首个在 " + firstBad);
                    }
                }
            }

            Console.WriteLine("== 二维码对拍：" + ok + " 通过 / " + bad + " 失败");
            if (bad == 0)
            {
                // 顺便报一下真实登录链接会用到哪个版本，方便判断屏幕上的码有多大
                var real = PeakMusic.Core.Qr.QrEncoder.Encode(cases["url"]);
                Console.WriteLine("真实登录链接：" + cases["url"].Length + " 字节 → V" + real.Version +
                                  " （" + real.Size + "×" + real.Size + " 模块，掩码 " + real.Mask + "）");
            }
            return bad == 0 ? 0 : 1;
        }

        /// <summary>
        /// QQ 音乐三件事：search / song / lyric。
        /// 这里的每个请求都和插件里跑的是同一份代码（QqClient），
        /// 所以这里通 = 游戏里也能通，剩下的只是 Unity 侧解码问题。
        /// </summary>
        private static int CmdQq(string[] args)
        {
            string sub = args.Length > 1 ? args[1].ToLowerInvariant() : "";
            using (var qq = new QqClient())
            {
                if (sub == "search")
                {
                    if (args.Length < 3) { Console.WriteLine("用法: qq search <关键词>"); return 2; }
                    var songs = qq.SearchAsync(args[2], 8, 0).GetAwaiter().GetResult();
                    Console.WriteLine("== QQ 搜索「" + args[2] + "」：" + songs.Count + " 首");
                    foreach (var s in songs)
                    {
                        Console.WriteLine("  " + s.SourceId + "  " + s.Name + " — " + s.Artists +
                                          "  [" + s.Album + "]  " + s.DurationText +
                                          "  (" + (s.Fee == 1 ? "付费" : "免费") + ")");
                    }
                    return 0;
                }

                if (sub == "song")
                {
                    if (args.Length < 3) { Console.WriteLine("用法: qq song <songmid>"); return 2; }
                    var song = new SongInfo { Platform = Platform.QQ, SourceId = args[2], Name = args[2] };
                    var r = qq.GetSongUrlAsync(song).GetAwaiter().GetResult();
                    Console.WriteLine("== QQ 取流 " + args[2]);
                    Console.WriteLine("code    : " + r.Code);
                    Console.WriteLine("level   : " + r.Level + "  br=" + r.Br);
                    Console.WriteLine("url     : " + (r.Url ?? "(空)"));
                    if (!string.IsNullOrEmpty(r.Message)) Console.WriteLine("message : " + r.Message);

                    if (r.Ok)
                    {
                        // 顺手验一下直链是不是真 mp3（ID3 或 0xFFFB 帧头），免得拿回一段 HTML 报错页
                        try
                        {
                            using (var hc = new HttpClient())
                            {
                                hc.DefaultRequestHeaders.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 2047);
                                var resp = hc.GetAsync(r.Url).GetAwaiter().GetResult();
                                byte[] head = resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                                string magic = head.Length > 3 && head[0] == 'I' && head[1] == 'D' && head[2] == '3'
                                    ? "ID3 (mp3)"
                                    : (head.Length > 1 && head[0] == 0xFF && (head[1] & 0xE0) == 0xE0 ? "MPEG 帧头 (mp3)" : "未知");
                                Console.WriteLine("探测    : HTTP " + (int)resp.StatusCode + "  " +
                                                  resp.Content.Headers.ContentType + "  " +
                                                  head.Length + " bytes  魔数=" + magic);
                            }
                        }
                        catch (Exception ex) { Console.WriteLine("探测失败: " + ex.Message); }
                    }
                    return 0;
                }

                if (sub == "lyric")
                {
                    if (args.Length < 3) { Console.WriteLine("用法: qq lyric <songmid>"); return 2; }
                    var song = new SongInfo { Platform = Platform.QQ, SourceId = args[2], Name = args[2] };
                    var ly = qq.GetLyricAsync(song).GetAwaiter().GetResult();
                    Console.WriteLine("== QQ 歌词 " + args[2]);
                    if (!ly.HasLyric) { Console.WriteLine("没有歌词。"); return 0; }
                    EchoLyric(ly, 12);
                    Console.WriteLine();
                    Console.WriteLine("翻译歌词: " + (string.IsNullOrEmpty(ly.Translated) ? "无" : "有（" + ly.Translated.Length + " 字符）"));
                    return 0;
                }

                Console.WriteLine("用法: qq search <关键词> | qq song <songmid> | qq lyric <songmid>");
                return 2;
            }
        }

        /// <summary>
        /// 走统一多音源入口（MusicSources），验证平台分发是否正确 ——
        /// 这是插件真正会走的那条路径，qq 子命令只是绕开抽象直接打协议层。
        /// </summary>
        private static int CmdSources(string[] args)
        {
            string sub = args.Length > 1 ? args[1].ToLowerInvariant() : "";
            if (sub != "search") { Console.WriteLine("用法: src search <netease|qq> <关键词>"); return 2; }
            if (args.Length < 4) { Console.WriteLine("用法: src search <netease|qq> <关键词>"); return 2; }

            Platform p = args[2].ToLowerInvariant() == "qq" ? Platform.QQ : Platform.Netease;

            using (var src = new MusicSources())
            {
                Console.WriteLine("== 统一入口 · " + Platforms.DisplayName(p) +
                                  "（品牌色 " + Platforms.BrandHex(p) + "）");
                Console.WriteLine("登录态: " + src.LoginLine(p));

                var songs = src.SearchAsync(p, args[3], 5, 0).GetAwaiter().GetResult();
                foreach (var s in songs)
                    Console.WriteLine("  [" + s.Key + "] " + s.Name + " — " + s.Artists + "  " + s.DurationText);

                if (songs.Count == 0) return 0;

                // 只取第一首的直链，别批量取——那是最容易被风控盯上的行为
                var song = songs[0];
                var url = src.GetUrlAsync(song).GetAwaiter().GetResult();
                Console.WriteLine("首曲取流: code=" + url.Code + " level=" + url.Level +
                                  " url=" + (url.Url != null ? url.Url.Substring(0, Math.Min(80, url.Url.Length)) + "…" : "(空)"));

                var ly = src.GetLyricAsync(song).GetAwaiter().GetResult();
                Console.WriteLine("首曲歌词: " + (ly.HasLyric ? ly.Lrc.Length + " 字符" : "无"));
            }
            return 0;
        }

        /// <summary>
        /// 实打实拉一段数据，量出带宽。用来判断"起播超时"到底是网太慢还是链接有问题：
        ///   8.9MB 的歌，若只有 50KB/s → 需要 180s，Unity 的 25s 超时必挂。
        /// </summary>
        private static int CmdSpeed(string url, int bytes)
        {
            Console.WriteLine("== 测速: " + url);
            Console.WriteLine("拉取上限: " + (bytes / 1024) + " KB");

            using (var handler = new HttpClientHandler { AllowAutoRedirect = true })
            using (var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(90) })
            {
                http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120 Safari/537.36");

                var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("Range", "bytes=0-" + (bytes - 1));

                var sw = Stopwatch.StartNew();
                var resp = http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
                long? total = resp.Content.Headers.ContentRange != null
                    ? resp.Content.Headers.ContentRange.Length
                    : resp.Content.Headers.ContentLength;

                long got = 0;
                using (var stream = resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                {
                    var buf = new byte[81920];
                    int read;
                    while (got < bytes && (read = stream.Read(buf, 0, buf.Length)) > 0)
                    {
                        got += read;
                        if (sw.ElapsedMilliseconds > 20000) break;
                    }
                }
                sw.Stop();

                double sec = sw.Elapsed.TotalSeconds;
                double kbps = sec > 0 ? got / 1024.0 / sec : 0;

                Console.WriteLine("状态码   : " + (int)resp.StatusCode);
                Console.WriteLine("总大小   : " + (total.HasValue ? (total.Value / 1024) + " KB" : "未知"));
                Console.WriteLine("实收     : " + (got / 1024) + " KB");
                Console.WriteLine("耗时     : " + sec.ToString("F2") + " s");
                Console.WriteLine("速度     : " + kbps.ToString("F1") + " KB/s");

                if (total.HasValue && kbps > 1)
                {
                    double need = total.Value / 1024.0 / kbps;
                    Console.WriteLine("按此速度拉完整首需: " + need.ToString("F0") + " s");
                    if (need > 20) Console.WriteLine("=> 结论：Unity 里 25s 超时不够，要么换小文件，要么放宽 timeout 并显示缓冲进度");
                }
            }
            return 0;
        }

        /// <summary>
        /// 只拉前 1KB：判断是不是真的音频直链、码率大概多少、是否支持 Range。
        /// 不下载整首，几毫秒出结果。
        /// </summary>
        private static int CmdProbe(string url, string label)
        {
            Console.WriteLine("== 探针: " + label + " ==");
            Console.WriteLine("URL: " + url);

            using (var handler = new HttpClientHandler { AllowAutoRedirect = true, AutomaticDecompression = System.Net.DecompressionMethods.GZip })
            using (var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) })
            {
                http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120 Safari/537.36");
                http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://music.163.com/");

                // 想带登录态测真实播放地址：先 set NETEASE_COOKIE=MUSIC_U=xxx; __csrf=yyy
                string cookie = Environment.GetEnvironmentVariable("NETEASE_COOKIE");
                if (!string.IsNullOrEmpty(cookie))
                {
                    http.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", cookie);
                    Console.WriteLine("Cookie      : 已携带（长度 " + cookie.Length + "）");
                }
                else
                {
                    Console.WriteLine("Cookie      : 无（匿名请求）");
                }

                var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("Range", "bytes=0-1023");

                var sw = Stopwatch.StartNew();
                var resp = http.SendAsync(req).GetAwaiter().GetResult();
                sw.Stop();

                Console.WriteLine("状态码      : " + (int)resp.StatusCode + " " + resp.StatusCode);
                Console.WriteLine("耗时        : " + sw.ElapsedMilliseconds + "ms");
                Console.WriteLine("最终地址    : " + resp.RequestMessage.RequestUri);

                if (resp.Content.Headers.ContentType != null)
                    Console.WriteLine("Content-Type: " + resp.Content.Headers.ContentType);
                if (resp.Content.Headers.ContentRange != null)
                    Console.WriteLine("Content-Range: " + resp.Content.Headers.ContentRange);
                if (resp.Content.Headers.ContentLength.HasValue)
                    Console.WriteLine("本次长度    : " + resp.Content.Headers.ContentLength + " bytes");

                byte[] head = resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                int n = Math.Min(head.Length, 16);
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < n; i++) sb.Append(head[i].ToString("X2")).Append(' ');
                Console.WriteLine("首 16 字节  : " + sb.ToString().Trim());

                bool isMp3 = head.Length >= 3 &&
                             ((head[0] == 0x49 && head[1] == 0x44 && head[2] == 0x33) || // "ID3"
                              (head[0] == 0xFF && (head[1] & 0xE0) == 0xE0));            // MPEG frame sync
                bool isHtml = head.Length > 1 && head[0] == 0x3C;                          // '<'
                bool ranOk = (int)resp.StatusCode >= 200 && (int)resp.StatusCode < 400;

                Console.WriteLine();
                if (isMp3) Console.WriteLine("=> 判定：MP3 音频流，可以丢给 UnityWebRequestMultimedia");
                else if (isHtml) Console.WriteLine("=> 判定：返回的是网页/错误页，不是音频（多半要 cookie 或被风控）");
                else Console.WriteLine("=> 判定：不是 mp3 文件头，先确认这个链接类型");

                if (!ranOk) return 3;
                return isMp3 ? 0 : 4;
            }
        }
    }
}
