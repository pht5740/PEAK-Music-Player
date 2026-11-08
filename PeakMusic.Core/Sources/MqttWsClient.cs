using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PeakMusic.Core.Sources
{
    /// <summary>MQTT 推送下来的一条 PUBLISH。</summary>
    internal sealed class MqttWsMessage
    {
        public string Topic = "";
        public Dictionary<string, string> UserProps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string Payload = "";
    }

    /// <summary>
    /// MQTT 5.0 over WebSocket 极简客户端——只为「QQ 音乐 App 扫码登录」服务。
    ///
    /// <para>协议对照 luren-dc/QQMusicApi 的 utils/mqtt.py / checking_mobile_qrcode（2026-11-08 实测）：</para>
    /// <list type="bullet">
    /// <item>连接 <c>wss://mu.y.qq.com:443/ws/handshake</c>，MQTT keepalive 45s；</item>
    /// <item>CONNECT 带 auth-method=pass + 用户属性 tmeAppID/business/hashTag/clientTag/userID；</item>
    /// <item>服务端可能回 <b>Server moved</b>（CONNACK reason 0x9C/0x9D + ServerReference 属性）——
    ///       按参考实现把路径尾段替换后重连（最多 3 次）；实测重定向是常态，别当错误；</item>
    /// <item>订阅 <c>management.qrcode_login/{qrcodeID}</c>（用户属性 authorization=tmelogin, pubsub=unicast）；</item>
    /// <item>下行 PUBLISH 的用户属性 <c>type</c> = scanned / canceled / timeout / loginFailed / cookies。</item>
    /// </list>
    ///
    /// <para>实现要点：ws 建连后立刻开<b>唯一</b>的接收循环往队列里塞完整报文——
    /// 绝不对 ReceiveAsync 用取消令牌（cancel 会把 ClientWebSocket 直接打死）。
    /// 握手等 CONNACK/SUBACK 就是从队列 TryTake 超时；会话期每 ~30s 发 PINGREQ。
    /// 发送侧用信号量串行（PINGREQ / PUBACK 可能撞车）。</para>
    /// </summary>
    internal sealed class MqttWsClient : IDisposable
    {
        /// <summary>本流程只发一次 SUBSCRIBE，固定包标识符 1。</summary>
        private const int SubPacketId = 1;

        private ClientWebSocket _ws;
        private readonly List<byte> _rxAcc = new List<byte>();
        private BlockingCollection<byte[]> _packets;
        private Task _rxLoop;
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        private volatile bool _disposed;

        /// <summary>
        /// 建连 + 开接收循环（含 Server moved 重定向）。成功返回最终使用的 path。
        /// headers 是 WS 握手请求头（Origin/Referer/UA）。
        /// </summary>
        public async Task<string> ConnectAsync(string host, int port, string path,
                                               IDictionary<string, string> headers,
                                               string clientId,
                                               IDictionary<string, string> connectUserProps,
                                               CancellationToken ct)
        {
            string cur = path;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                var ws = new ClientWebSocket();
                try { ws.Options.AddSubProtocol("mqtt"); }
                catch { /* 运行时不支持子协议就算了 */ }
                if (headers != null)
                {
                    foreach (var kv in headers)
                    {
                        try { ws.Options.SetRequestHeader(kv.Key, kv.Value); }
                        catch { /* 受限头跳过 */ }
                    }
                }

                await ws.ConnectAsync(new Uri("wss://" + host + ":" + port + cur), ct).ConfigureAwait(false);

                _packets = new BlockingCollection<byte[]>(64);
                _rxAcc.Clear();
                StartRxLoop(ws);

                await SendAsync(ws, BuildConnect(clientId, connectUserProps), ct).ConfigureAwait(false);

                byte[] ack = TakePacket(20000);
                if (ack == null)
                {
                    Shutdown(ws);
                    throw new InvalidOperationException("MQTT CONNACK 超时");
                }

                // CONNACK：fixed header + 剩余长度 varint + ack flags + reason + properties
                // （不能拿 ack[1] 当 reason——那是剩余长度字节，0x58=88 是包长不是错误码）
                string serverRef;
                int reason;
                string extra = ParseConnAckProps(ack, out reason, out serverRef);

                if (reason == 0)
                {
                    _ws = ws;
                    return cur;
                }

                // 0x9C=156 Use another server / 0x9D=157 Server moved——按 ServerReference 换路径重来
                if ((reason == 0x9C || reason == 0x9D) && !string.IsNullOrEmpty(serverRef))
                {
                    Shutdown(ws);
                    cur = BuildRedirectPath(cur, serverRef);
                    continue;
                }

                Shutdown(ws);
                throw new InvalidOperationException("MQTT 连接被拒 reason=0x" + reason.ToString("X2") + extra);
            }
            throw new InvalidOperationException("MQTT 重定向次数超限");
        }

        /// <summary>订阅主题并等 SUBACK（超时抛异常）。</summary>
        public async Task SubscribeAsync(string topic, IDictionary<string, string> subUserProps, CancellationToken ct)
        {
            ClientWebSocket ws = _ws;
            if (ws == null) throw new InvalidOperationException("MQTT 未连接");

            await SendAsync(ws, BuildSubscribe(topic, subUserProps), ct).ConfigureAwait(false);

            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                byte[] pkt = TakePacket(2000);
                if (pkt == null) continue;
                int type = (pkt[0] >> 4) & 0x0F;
                if (type == 9) // SUBACK
                {
                    // fixed header + 剩余长度 varint + packet id(2) + properties + reason codes
                    int pos = 1;
                    ReadVarint(pkt, ref pos);
                    pos += 2;
                    int plen = ReadVarint(pkt, ref pos);
                    pos += plen;
                    for (int i = pos; i < pkt.Length; i++)
                    {
                        if (pkt[i] >= 0x80)
                            throw new InvalidOperationException("MQTT SUBACK 拒绝 reason=0x" + pkt[i].ToString("X2"));
                    }
                    return;
                }
                // PINGRESP 等忽略；PUBLISH 理论上订阅前不会来，来了也只是丢弃
            }
            throw new InvalidOperationException("MQTT SUBACK 超时");
        }

        /// <summary>
        /// 等一条 PUBLISH。wait 超时返回 null（调用方自行决定是否发 PINGREQ）。
        /// 非 PUBLISH 报文（PINGRESP 等）静默跳过。
        /// </summary>
        public MqttWsMessage ReceiveMessage(TimeSpan wait)
        {
            DateTime deadline = DateTime.UtcNow + wait;
            while (true)
            {
                int leftMs = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                if (leftMs <= 0) return null;

                byte[] pkt = TakePacket(leftMs);
                if (pkt == null) return null;

                int type = (pkt[0] >> 4) & 0x0F;
                if (type == 3) return ParsePublish(pkt);
                // 其它报文（PINGRESP/PUBACK…）忽略
            }
        }

        /// <summary>发 MQTT PINGREQ（keepalive 45s，调用方每 ~30s 发一次）。</summary>
        public Task PingAsync(CancellationToken ct)
        {
            ClientWebSocket ws = _ws;
            if (ws == null) throw new InvalidOperationException("MQTT 未连接");
            return SendAsync(ws, new byte[] { 0xC0, 0x00 }, ct);
        }

        // ───────────────────────────── 接收循环 ─────────────────────────────

        /// <summary>
        /// 唯一的 WS 接收循环：字节流攒出完整 MQTT 报文塞进 _packets。
        /// 不用取消令牌（cancel 会打死 ClientWebSocket）；Dispose 时靠 ws 异常退出。
        /// </summary>
        private void StartRxLoop(ClientWebSocket ws)
        {
            _rxLoop = Task.Run(async () =>
            {
                var buf = new byte[8192];
                try
                {
                    while (!_disposed)
                    {
                        WebSocketReceiveResult r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None)
                                                            .ConfigureAwait(false);
                        if (r.MessageType == WebSocketMessageType.Close) return;
                        for (int i = 0; i < r.Count; i++) _rxAcc.Add(buf[i]);

                        byte[] pkt;
                        while ((pkt = TryTakePacket()) != null)
                        {
                            if (_packets != null && !_packets.IsAddingCompleted)
                            {
                                try { _packets.Add(pkt); } catch { return; }
                            }
                        }
                    }
                }
                catch { /* Dispose/断线：静默结束 */ }
            });
        }

        /// <summary>从队列取一个完整报文；超时返回 null。</summary>
        private byte[] TakePacket(int timeoutMs)
        {
            BlockingCollection<byte[]> q = _packets;
            if (q == null) return null;
            byte[] pkt;
            return q.TryTake(out pkt, timeoutMs) ? pkt : null;
        }

        /// <summary>缓冲里够一个完整报文就摘出来（fixed header + varint 剩余长度 + body）。</summary>
        private byte[] TryTakePacket()
        {
            if (_rxAcc.Count < 2) return null;

            int mult = 1, len = 0, i = 1;
            bool lenDone = false;
            for (; i < _rxAcc.Count && i <= 4; i++)
            {
                int d = _rxAcc[i];
                len += (d & 0x7F) * mult;
                mult *= 128;
                if ((d & 0x80) == 0) { lenDone = true; break; }
            }
            if (!lenDone) return null; // 剩余长度的 varint 还没收完

            int headerLen = i + 1; // fixed header 1 字节 + varint 共 i 字节（i 是最后一字节下标）
            if (_rxAcc.Count < headerLen + len) return null;

            var pkt = new byte[headerLen + len];
            _rxAcc.CopyTo(0, pkt, 0, pkt.Length);
            _rxAcc.RemoveRange(0, pkt.Length);
            return pkt;
        }

        private void Shutdown(ClientWebSocket ws)
        {
            try { if (_packets != null) _packets.CompleteAdding(); } catch { }
            try { ws.Dispose(); } catch { }
        }

        // ───────────────────────────── 报文构造 ─────────────────────────────

        private Task SendAsync(ClientWebSocket ws, byte[] packet, CancellationToken ct)
        {
            return _sendLock.WaitAsync(ct).ContinueWith(async t =>
            {
                try
                {
                    await ws.SendAsync(new ArraySegment<byte>(packet), WebSocketMessageType.Binary, true, ct)
                            .ConfigureAwait(false);
                }
                finally
                {
                    try { _sendLock.Release(); } catch { }
                }
            }, CancellationToken.None).Unwrap();
        }

        /// <summary>MQTT5 CONNECT：clean start，keepalive 45，auth-method=pass + 用户属性。</summary>
        private static byte[] BuildConnect(string clientId, IDictionary<string, string> userProps)
        {
            var body = new List<byte>();
            // 协议名 MQTT + level 5 + connect flags(clean start=0x02) + keepalive 45
            body.AddRange(new byte[] { 0x00, 0x04, (byte)'M', (byte)'Q', (byte)'T', (byte)'T', 0x05, 0x02, 0x00, 0x2D });

            var props = new List<byte>();
            props.Add(0x15); // Authentication Method
            AddUtf8(props, "pass");
            if (userProps != null)
            {
                foreach (var kv in userProps)
                {
                    props.Add(0x26); // User Property
                    AddUtf8(props, kv.Key);
                    AddUtf8(props, kv.Value);
                }
            }
            AddVarint(body, props.Count);
            body.AddRange(props);

            AddUtf8(body, clientId);
            return Wrap(0x10, body);
        }

        /// <summary>MQTT5 SUBSCRIBE（QoS 0）。</summary>
        private static byte[] BuildSubscribe(string topic, IDictionary<string, string> userProps)
        {
            var body = new List<byte>();
            body.Add((byte)(SubPacketId >> 8));
            body.Add((byte)(SubPacketId & 0xFF));

            var props = new List<byte>();
            if (userProps != null)
            {
                foreach (var kv in userProps)
                {
                    props.Add(0x26);
                    AddUtf8(props, kv.Key);
                    AddUtf8(props, kv.Value);
                }
            }
            AddVarint(body, props.Count);
            body.AddRange(props);

            AddUtf8(body, topic);
            body.Add(0x00); // subscription options：QoS 0
            return Wrap(0x82, body);
        }

        private static byte[] Wrap(int typeFlags, List<byte> body)
        {
            var pkt = new List<byte>();
            pkt.Add((byte)typeFlags);
            AddVarint(pkt, body.Count);
            pkt.AddRange(body);
            return pkt.ToArray();
        }

        private static void AddUtf8(List<byte> buf, string s)
        {
            byte[] b = Encoding.UTF8.GetBytes(s ?? "");
            buf.Add((byte)(b.Length >> 8));
            buf.Add((byte)(b.Length & 0xFF));
            buf.AddRange(b);
        }

        private static void AddVarint(List<byte> buf, int value)
        {
            do
            {
                int d = value % 128;
                value /= 128;
                if (value > 0) d |= 0x80;
                buf.Add((byte)d);
            } while (value > 0);
        }

        // ───────────────────────────── 报文解析 ─────────────────────────────

        /// <summary>
        /// 解析 CONNACK：跳过 fixed header + 剩余长度 varint，取 ack flags / reason / properties。
        /// reason 通过 out 返回（0=成功，0x9C/0x9D=Server moved）。
        /// </summary>
        private static string ParseConnAckProps(byte[] ack, out int reason, out string serverRef)
        {
            serverRef = null;
            reason = -1;
            if (ack.Length < 3) return null;
            int pos = 1;
            ReadVarint(ack, ref pos); // 跳过剩余长度
            if (pos + 2 > ack.Length) return null;
            pos++;                    // ack flags
            reason = ack[pos++];      // reason code
            int plen = ReadVarint(ack, ref pos);
            string extra = "";
            int end = Math.Min(ack.Length, pos + plen);
            while (pos < end)
            {
                int id = ack[pos++];
                switch (id)
                {
                    case 0x13: pos += 2; break;                     // Server Keep Alive
                    case 0x1C: serverRef = ReadUtf8(ack, ref pos); break;
                    case 0x1F: extra += " reason=" + ReadUtf8(ack, ref pos); break;
                    case 0x26:                                        // user property
                        string k = ReadUtf8(ack, ref pos);
                        string v = ReadUtf8(ack, ref pos);
                        extra += " " + k + "=" + v;
                        break;
                    default:
                        pos = end; // 未知属性不冒险解析
                        break;
                }
            }
            return extra;
        }

        private static MqttWsMessage ParsePublish(byte[] pkt)
        {
            int flags = pkt[0] & 0x0F;
            int qos = (flags >> 1) & 0x03;
            int pos = 1;
            ReadVarint(pkt, ref pos); // 剩余长度

            var msg = new MqttWsMessage();
            msg.Topic = ReadUtf8(pkt, ref pos);
            if (qos > 0) pos += 2; // packet id（QoS0 订阅不会收到，留位防御）

            int plen = ReadVarint(pkt, ref pos);
            int end = Math.Min(pkt.Length, pos + plen);
            while (pos < end)
            {
                int id = pkt[pos++];
                switch (id)
                {
                    case 0x01: pos += 1; break;
                    case 0x02: case 0x11: case 0x18: case 0x27: pos += 4; break;
                    case 0x03: case 0x08: case 0x12: case 0x15: case 0x1A: case 0x1C: case 0x1F:
                        ReadUtf8(pkt, ref pos); break;
                    case 0x09: case 0x16:                                    // binary
                        int bl = (pkt[pos] << 8) | pkt[pos + 1]; pos += 2 + bl; break;
                    case 0x0B: ReadVarint(pkt, ref pos); break;
                    case 0x13: case 0x21: case 0x22: case 0x23: pos += 2; break;
                    case 0x17: case 0x19: case 0x24: case 0x25: case 0x28: case 0x29: case 0x2A:
                        pos += 1; break;
                    case 0x26:
                        string k = ReadUtf8(pkt, ref pos);
                        string v = ReadUtf8(pkt, ref pos);
                        msg.UserProps[k] = v;
                        break;
                    default:
                        pos = end;
                        break;
                }
            }
            if (pos < pkt.Length)
                msg.Payload = Encoding.UTF8.GetString(pkt, pos, pkt.Length - pos);
            return msg;
        }

        private static int ReadVarint(byte[] buf, ref int pos)
        {
            int mult = 1, len = 0;
            for (int i = 0; i < 4 && pos < buf.Length; i++)
            {
                int d = buf[pos++];
                len += (d & 0x7F) * mult;
                mult *= 128;
                if ((d & 0x80) == 0) break;
            }
            return len;
        }

        private static string ReadUtf8(byte[] buf, ref int pos)
        {
            if (pos + 2 > buf.Length) { pos = buf.Length; return ""; }
            int n = (buf[pos] << 8) | buf[pos + 1];
            pos += 2;
            if (pos + n > buf.Length) n = buf.Length - pos;
            string s = Encoding.UTF8.GetString(buf, pos, n);
            pos += n;
            return s;
        }

        /// <summary>Server moved 的路径改写（对照 QQMusicApi 的 _build_redirect_path）。</summary>
        private static string BuildRedirectPath(string path, string serverRef)
        {
            string trimmed = path.TrimEnd('/');
            string[] parts = trimmed.Split('/');
            if (parts.Length > 0 && parts[parts.Length - 1].Contains(":"))
            {
                parts[parts.Length - 1] = serverRef;
                return string.Join("/", parts);
            }
            return trimmed + "/" + serverRef;
        }

        public void Dispose()
        {
            _disposed = true;
            try { if (_packets != null) _packets.CompleteAdding(); } catch { }
            try { if (_ws != null) _ws.Dispose(); } catch { }
            _ws = null;
        }
    }
}
