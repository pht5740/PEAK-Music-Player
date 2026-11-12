using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using BepInEx.Logging;
using FlacBox;
using UnityEngine;
using UnityEngine.Networking;

namespace PeakMusic
{
    /// <summary>
    /// 无损（FLAC）加载器。
    ///
    /// 为什么必须自己解：Unity 的 UnityWebRequestMultimedia 运行时只解 MP3 / OGG / WAV，
    /// 喂 FLAC 进去不报错、只是拿不到 AudioClip（结果就是"点了没声音"）。
    ///
    /// 三个刻意的设计：
    ///   1) DownloadHandlerFile 直接落盘，整首歌的压缩数据不驻留内存；
    ///   2) 边解边 SetData，峰值内存只有一个 AudioClip；
    ///   3) 每帧只解 ~12ms 就 yield，宁可多花几帧，也不让游戏掉帧。
    ///
    /// 关于位深（2026-11-12 用真实 FLAC 实测）：
    ///   Wave16OverFlacStream 会把 24bit 源强行降成 16bit（96kHz 那条 dataBytes 直接少 1/3）；
    ///   WaveOverFlacStream 保留原始位深。所以用后者，自己按 16/24/32bit 转 float，
    ///   Hi-Res 才不会白拿。
    /// </summary>
    internal static class FlacLoader
    {
        private const string Tag = "[无损]";
        private static ManualLogSource Log { get { return Plugin.Log; } }

        /// <summary>一帧最多占用多少毫秒解码。太小预处理很久，太大掉帧。</summary>
        private const int FrameBudgetMs = 12;

        /// <summary>
        /// SetData 的运行时绑定。
        ///
        /// 为什么要绕反射：Unity 6 的 AudioModule 给 SetData 加了 ReadOnlySpan&lt;float&gt; 重载，
        /// 直接写 clip.SetData(...) 会让编译器去解析 Span，而 net472 引用集里没有它（CS0518 编不过）。
        ///
        /// **为什么不能直接用 Action 委托**：Unity 6 的 SetData 签名是
        /// <c>Boolean SetData(Single[], Int32)</c>（2026-11-12 反射确认），返回 bool；
        /// 之前按 void 委托去绑，Delegate.CreateDelegate 会抛
        /// "method return type is incompatible" → 绑定失败 → 整首歌退回 320k。
        /// 所以按 mi.ReturnType 选委托，并且再留一层 MethodInfo.Invoke 兜底。
        /// </summary>
        private static readonly MethodInfo _setDataMi =
            typeof(AudioClip).GetMethod("SetData", new[] { typeof(float[]), typeof(int) });

        private static readonly Func<AudioClip, float[], int, bool> _setDataBool = BuildSetDataBool();
        private static readonly Action<AudioClip, float[], int> _setDataVoid = BuildSetDataVoid();

        private static Func<AudioClip, float[], int, bool> BuildSetDataBool()
        {
            try
            {
                if (_setDataMi == null || _setDataMi.ReturnType != typeof(bool)) return null;
                return (Func<AudioClip, float[], int, bool>)Delegate.CreateDelegate(
                    typeof(Func<AudioClip, float[], int, bool>), _setDataMi);
            }
            catch (Exception e)
            {
                if (Log != null) Log.LogWarning(Tag + " SetData 强类型绑定失败（改用反射调用）：" + e.Message);
                return null;
            }
        }

        private static Action<AudioClip, float[], int> BuildSetDataVoid()
        {
            try
            {
                if (_setDataMi == null || _setDataMi.ReturnType != typeof(void)) return null;
                return (Action<AudioClip, float[], int>)Delegate.CreateDelegate(
                    typeof(Action<AudioClip, float[], int>), _setDataMi);
            }
            catch { return null; }
        }

        /// <summary>SetData 是否可用（三选一，任一路可用就算可用）。</summary>
        private static bool SetDataReady
        {
            get { return _setDataBool != null || _setDataVoid != null || _setDataMi != null; }
        }

        /// <summary>把一段 PCM 写进 clip。返回是否成功。</summary>
        private static bool SetSamples(AudioClip clip, float[] data, int offsetFrames)
        {
            try
            {
                if (_setDataBool != null) return _setDataBool(clip, data, offsetFrames);
                if (_setDataVoid != null) { _setDataVoid(clip, data, offsetFrames); return true; }
                if (_setDataMi != null)
                {
                    object r = _setDataMi.Invoke(clip, new object[] { data, offsetFrames });
                    return !(r is bool) || (bool)r;
                }
            }
            catch (Exception e)
            {
                if (Log != null) Log.LogError(Tag + " 写入采样失败：" + e.Message);
            }
            return false;
        }

        /// <summary>
        /// 下载候选地址：先 https，再原始地址（有些腾讯云节点只认 http 或只认 https）。
        /// </summary>
        private static string[] BuildCandidates(string url)
        {
            if (string.IsNullOrEmpty(url)) return new string[0];

            bool loopback = url.IndexOf("127.0.0.1", StringComparison.OrdinalIgnoreCase) >= 0
                         || url.IndexOf("localhost", StringComparison.OrdinalIgnoreCase) >= 0;
            if (loopback) return new[] { url };

            if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return new[] { url, "http://" + url.Substring(8) };

            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                return new[] { "https://" + url.Substring(7), url };

            return new[] { url };
        }

        /// <summary>把 URL 下载到文件；progress 报的是已收字节。</summary>
        private static void DownloadOnce(string url, string path, Action<long> progress, int timeoutSec)
        {
            using (var http = new System.Net.Http.HttpClient())
            using (var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, url))
            {
                if (timeoutSec > 0) http.Timeout = TimeSpan.FromSeconds(timeoutSec * 2);
                else http.Timeout = TimeSpan.FromMinutes(10);

                // 部分 CDN 节点按来源拦，带一个正常来源头最省事
                try { req.Headers.Referrer = new Uri("https://y.qq.com/"); } catch { }
                req.Headers.TryAddWithoutValidation("User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) "
                    + "Chrome/131.0.0.0 Safari/537.36");
                req.Headers.TryAddWithoutValidation("Accept", "*/*");

                using (var resp = http.SendAsync(req, System.Net.Http.HttpCompletionOption.ResponseHeadersRead)
                                       .GetAwaiter().GetResult())
                {
                    if (!resp.IsSuccessStatusCode)
                        throw new Exception("HTTP " + (int)resp.StatusCode + " " + resp.ReasonPhrase);

                    using (var src = resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                    using (var dst = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                    {
                        byte[] buf = new byte[1 << 16];
                        long total = 0;
                        int n;
                        while ((n = src.Read(buf, 0, buf.Length)) > 0)
                        {
                            dst.Write(buf, 0, n);
                            total += n;
                            if (progress != null) progress(total);
                        }
                    }
                }
            }
        }

        internal sealed class Result
        {
            internal AudioClip Clip;
            internal string Error;
            internal int Rate;
            internal int Channels;
            internal int Bits;
            internal float Seconds;
            internal long CompressedBytes;
            internal long PcmBytes;

            internal bool Ok { get { return Clip != null; } }
        }

        internal static IEnumerator Load(string url, Action<string> status, Action<Result> done)
        {
            var res = new Result();
            string tmp = null;
            FileStream fs = null;
            Stream dec = null;

            try
            {
                // ── ① 下载到临时文件 ──
                try
                {
                    string dir = Path.Combine(Path.GetTempPath(), "PeakMusic");
                    Directory.CreateDirectory(dir);
                    tmp = Path.Combine(dir, "flac_" + DateTime.UtcNow.Ticks + ".flac");
                }
                catch (Exception e)
                {
                    res.Error = "建临时目录失败：" + e.Message;
                    yield break;
                }

                // ── 下载：用 HttpClient，不用 UnityWebRequest ──
                // 为什么换掉：QQ 有一部分直链在 aqqmusic.tc.qq.com 这类腾讯云节点上，
                // Unity 自带的下载器连上去直接 0 字节、报 code=0 Unknown Error（不是 403、不是超时），
                // 但同一台机器用系统栈能正常下。HttpClient 走 Windows 的 TLS 实现，并且不受
                // Unity 6「禁止明文 http」的限制，所以更稳；顺手带上 Referer，避免部分节点挑来源。
                string dlUrl = url;
                string dlErr = null;
                long dlBytes = 0;

                var dlTask = System.Threading.Tasks.Task.Run(delegate
                {
                    string[] candidates = BuildCandidates(dlUrl);
                    string last = null;
                    for (int i = 0; i < candidates.Length; i++)
                    {
                        try
                        {
                            DownloadOnce(candidates[i], tmp,
                                delegate(long n) { dlBytes = n; },
                                Plugin.TimeoutSeconds != null ? Plugin.TimeoutSeconds.Value : 0);
                            dlErr = null;
                            return;                       // 成功就结束，不再试下一个协议
                        }
                        catch (Exception e)
                        {
                            // 注意：这里不能写 `dlErr = ...` 当循环条件 —— 第一轮失败就会把
                            // 条件判死，永远走不到「换 http 再试一次」。用局部变量记最后一条。
                            last = candidates[i] + " → " + e.Message;
                        }
                    }
                    dlErr = last;
                });

                while (!dlTask.IsCompleted)
                {
                    if (status != null) status("无损下载 " + (dlBytes / 1024) + " KB");
                    yield return null;
                }

                if (dlTask.IsFaulted || dlErr != null)
                {
                    string msg = dlErr;
                    if (string.IsNullOrEmpty(msg) && dlTask.Exception != null)
                    {
                        var inner = dlTask.Exception.GetBaseException();
                        msg = inner.GetType().Name + ": " + inner.Message;
                    }
                    res.Error = "下载失败：" + msg;
                    string host = "";
                    try { host = new Uri(dlUrl).Host; } catch { }
                    Log.LogError(Tag + " 下载失败 host=" + host + " 已收 " + dlBytes + "B → " + msg);
                    yield break;
                }

                long size = 0;
                try { size = new FileInfo(tmp).Length; } catch { }
                res.CompressedBytes = size;
                if (size < 64 * 1024)
                {
                    res.Error = "文件太小（" + size + " B），不是有效 FLAC";
                    yield break;
                }

                // ── ② 打开解码流 ──
                if (status != null) status("无损解码准备…");
                yield return null;

                try
                {
                    fs = new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
                    dec = new WaveOverFlacStream(fs, WaveOverFlacStreamMode.Decode, true);
                }
                catch (Exception e)
                {
                    res.Error = "打不开解码器：" + e.Message;
                    Log.LogError(Tag + " " + res.Error);
                    yield break;
                }

                // ── ③ 解析 WAV 头（按 RIFF 块走，不假设固定 44 字节）──
                int fmtTag = 0, channels = 0, rate = 0, bits = 0;
                long dataBytes = -1;
                ParseHeader(dec, ref fmtTag, ref channels, ref rate, ref bits, ref dataBytes);

                if (channels <= 0 || rate <= 0 || bits <= 0)
                {
                    res.Error = "WAV 头解析失败（ch=" + channels + " rate=" + rate + " bits=" + bits + "）";
                    Log.LogError(Tag + " " + res.Error);
                    yield break;
                }
                if (bits != 16 && bits != 24 && bits != 32)
                {
                    res.Error = "不支持的位深 " + bits + "bit";
                    yield break;
                }

                int frameBytes = channels * (bits / 8);
                long totalFrames = dataBytes > 0 && dataBytes < 0x7FFFF000L ? dataBytes / frameBytes : -1;

                // data 块长度缺失时退回「整段解完再建 clip」；内存高但极少走到
                byte[] all = null;
                Stream feed = dec;
                if (totalFrames <= 0)
                {
                    if (status != null) status("无损解码（长度未知，整段读取）…");
                    yield return null;
                    var ms = new MemoryStream();
                    byte[] b = new byte[1 << 16];
                    int n;
                    var sw0 = Stopwatch.StartNew();
                    while ((n = dec.Read(b, 0, b.Length)) > 0)
                    {
                        ms.Write(b, 0, n);
                        if (sw0.ElapsedMilliseconds > FrameBudgetMs) { sw0.Reset(); sw0.Start(); yield return null; }
                    }
                    all = ms.ToArray();
                    totalFrames = all.Length / frameBytes;
                    feed = new MemoryStream(all);
                }

                if (totalFrames <= 0)
                {
                    res.Error = "解码后长度为 0";
                    yield break;
                }

                // 时长上限：10 分钟的无损在内存里是几百 MB，低配机会直接卡死
                float seconds = (float)totalFrames / rate;
                int maxSec = 900;
                try { if (Plugin.MaxLosslessSeconds != null) maxSec = Plugin.MaxLosslessSeconds.Value; } catch { }
                if (maxSec > 0 && seconds > maxSec + 1f)
                {
                    res.Error = "曲目过长（" + Mathf.RoundToInt(seconds) + "s > " + maxSec + "s），已跳过无损";
                    Log.LogWarning(Tag + " " + res.Error);
                    yield break;
                }

                if (totalFrames > int.MaxValue)
                {
                    res.Error = "样本数溢出：" + totalFrames;
                    yield break;
                }

                if (!SetDataReady)
                {
                    res.Error = "AudioClip.SetData 不可用（找不到 SetData(float[], int)）";
                    yield break;
                }

                // ── ④ 建 clip 并分块填充 ──
                AudioClip clip = null;
                try
                {
                    clip = AudioClip.Create("peakmusic_flac", (int)totalFrames, channels, rate, false);
                }
                catch (Exception e)
                {
                    res.Error = "建 AudioClip 失败：" + e.Message;
                    Log.LogError(Tag + " " + res.Error);
                    yield break;
                }

                if (clip == null || clip.samples <= 0)
                {
                    res.Error = "AudioClip 创建为空（内存不足？）";
                    Log.LogError(Tag + " " + res.Error);
                    yield break;
                }

                int bytesPer = bits / 8;
                int chunkBytes = 1 << 16;
                if (chunkBytes % frameBytes != 0) chunkBytes -= chunkBytes % frameBytes;
                if (chunkBytes < frameBytes) chunkBytes = frameBytes;

                byte[] buf = new byte[chunkBytes];
                float[] fbuf = new float[chunkBytes / bytesPer];

                long offsetFrames = 0;
                var sw = Stopwatch.StartNew();
                int lastPct = -10;

                while (offsetFrames < totalFrames)
                {
                    int want = (int)Math.Min((long)chunkBytes, (totalFrames - offsetFrames) * frameBytes);
                    int got = 0;
                    while (got < want)
                    {
                        int n;
                        try { n = feed.Read(buf, got, want - got); }
                        catch (Exception e) { Log.LogError(Tag + " 解码中断：" + e.Message); n = 0; }
                        if (n <= 0) break;
                        got += n;
                    }
                    if (got <= 0) break;

                    int samples = got / bytesPer;
                    ToFloat(buf, got, bits, fmtTag, fbuf);

                    // 最后一块通常不满：SetData 的数组长度必须正好是要写的样本数
                    float[] data = fbuf;
                    if (samples != fbuf.Length)
                    {
                        data = new float[samples];
                        Array.Copy(fbuf, data, samples);
                    }
                    if (!SetSamples(clip, data, (int)offsetFrames))
                    {
                        res.Error = "写入 AudioClip 失败（第 " + offsetFrames + " 帧）";
                        yield break;
                    }
                    offsetFrames += samples / channels;

                    if (sw.ElapsedMilliseconds > FrameBudgetMs)
                    {
                        int pct = (int)(offsetFrames * 100L / totalFrames);
                        if (status != null && pct != lastPct) { lastPct = pct; status("无损解码 " + pct + "%"); }
                        sw.Reset();
                        sw.Start();
                        yield return null;
                    }
                }

                if (all != null) { try { ((MemoryStream)feed).Dispose(); } catch { } }

                res.Clip = clip;
                res.Rate = rate;
                res.Channels = channels;
                res.Bits = bits;
                res.Seconds = clip.length;
                res.PcmBytes = offsetFrames * frameBytes;

                if (offsetFrames < totalFrames)
                    Log.LogWarning(Tag + " 只解出 " + offsetFrames + "/" + totalFrames + " 帧，尾部会是静音");
                else
                    Log.LogInfo(Tag + " 解码完成 " + res.Seconds.ToString("F1") + "s / " + rate + "Hz / "
                              + bits + "bit / " + channels + "ch / 压缩 " + (res.CompressedBytes / 1024) + "KB");
            }
            finally
            {
                try { if (dec != null) dec.Dispose(); } catch { }
                try { if (fs != null) fs.Dispose(); } catch { }
                try { if (tmp != null && File.Exists(tmp)) File.Delete(tmp); } catch { }

                if (done != null) done(res);
            }
        }

        /// <summary>16/24/32bit 整数（或 32bit 浮点）→ Unity 的 float 采样（-1~1）。</summary>
        private static void ToFloat(byte[] buf, int bytes, int bits, int fmtTag, float[] dst)
        {
            if (bits == 16)
            {
                int n = bytes / 2;
                for (int i = 0; i < n; i++) dst[i] = (short)(buf[i * 2] | (buf[i * 2 + 1] << 8)) / 32768f;
                return;
            }
            if (bits == 24)
            {
                int n = bytes / 3;
                for (int i = 0; i < n; i++)
                {
                    int o = i * 3;
                    int v = buf[o] | (buf[o + 1] << 8) | (buf[o + 2] << 16);
                    if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000);   // 符号扩展
                    dst[i] = v / 8388608f;
                }
                return;
            }
            int m = bytes / 4;
            if (fmtTag == 3)
            {
                for (int i = 0; i < m; i++) dst[i] = BitConverter.ToSingle(buf, i * 4);
            }
            else
            {
                for (int i = 0; i < m; i++) dst[i] = BitConverter.ToInt32(buf, i * 4) / 2147483648f;
            }
        }

        /// <summary>按 RIFF 块顺序解析，读到 data 块停下（此时流正好停在 PCM 起点）。</summary>
        private static void ParseHeader(Stream s, ref int fmtTag, ref int channels, ref int rate,
                                        ref int bits, ref long dataBytes)
        {
            byte[] riff = new byte[12];
            if (!ReadFull(s, riff, 12)) return;
            if (riff[0] != 'R' || riff[1] != 'I' || riff[2] != 'F' || riff[3] != 'F') return;

            byte[] ck = new byte[8];
            for (int guard = 0; guard < 16; guard++)
            {
                if (!ReadFull(s, ck, 8)) return;
                // 不用 new string(char[])：netstandard 2.1 下会解析到 ReadOnlySpan 重载，net472 没有
                string id = ((char)ck[0]).ToString() + (char)ck[1] + (char)ck[2] + (char)ck[3];
                int size = BitConverter.ToInt32(ck, 4);

                if (id == "data")
                {
                    dataBytes = (uint)size;
                    return;
                }

                if (id == "fmt " && size >= 16 && size <= 4096)
                {
                    byte[] fmt = new byte[size];
                    if (!ReadFull(s, fmt, size)) return;
                    fmtTag = BitConverter.ToInt16(fmt, 0);
                    channels = BitConverter.ToInt16(fmt, 2);
                    rate = BitConverter.ToInt32(fmt, 4);
                    bits = BitConverter.ToInt16(fmt, 14);
                    if ((size & 1) == 1) s.ReadByte();
                    continue;
                }

                long left = size + (size & 1);
                byte[] skip = new byte[4096];
                while (left > 0)
                {
                    int n = s.Read(skip, 0, (int)Math.Min(left, skip.Length));
                    if (n <= 0) return;
                    left -= n;
                }
            }
        }

        private static bool ReadFull(Stream s, byte[] buf, int count)
        {
            int got = 0;
            while (got < count)
            {
                int n = s.Read(buf, got, count - got);
                if (n <= 0) return false;
                got += n;
            }
            return true;
        }
    }
}
