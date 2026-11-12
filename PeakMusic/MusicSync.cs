using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Logging;
using ExitGames.Client.Photon;
using PeakMusic.Core.Netease;
using PeakMusic.Core.Sources;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;

namespace PeakMusic
{
    /// <summary>
    /// 「一起听」网络同步层。
    ///
    /// 机制（房主拉流 → 转发 URL + 同步指令，见 r22 设计）：
    ///   房主选好歌、取到播放直链后，把「歌名/歌手/直链/平台/起始秒」经 Photon 自定义事件广播给
    ///   房间所有人；每个玩家各自下载同一份音频本地播，房主再发「暂停/继续/切歌/进度」指令对齐。
    ///   客机点歌 → 事件发给房主 → 房主面板里看到队列 → 房主批准后按顺序播。
    ///
    /// 全部走 PhotonNetwork.RaiseEvent（用户自定义事件码 90~99），可靠投递 SendReliable。
    /// 收到事件靠 IOnEventCallback.OnEvent；房主判定 PhotonNetwork.IsMasterClient。
    /// </summary>
    internal sealed class MusicSync : MonoBehaviour, IOnEventCallback
    {
        private const string Tag = "[一起听]";

        // Photon 用户自定义事件码（byte 0~199 可自定义，取 90~95 避让游戏已有值）
        private const byte EvHostPlay = 90;       // 房主广播：开始播某首歌（含 URL）
        private const byte EvHostPause = 91;      // 房主：暂停 / 继续
        private const byte EvHostStop = 92;       // 房主：停止
        private const byte EvHostSeek = 93;       // 房主：跳转进度（秒）
        private const byte EvQueueAdd = 94;       // 客机 → 房主：点歌进队列
        private const byte EvQueueState = 95;     // 房主 → 全员：队列更新（含批准/移除）

        // 房间成员把自己「装了模组 + 版本」写进 Photon 玩家属性，别人读得到。
        // 之前完全没做身份探测：客机一律把点歌塞进房主队列，房主没装模组时永远没人批准，
        // 表现就是面板一直挂着「等房主批准」，一个人装模组反而放不了歌。
        private const string ModPropKey = "pmv";

        private static ManualLogSource Log { get { return Plugin.Log; } }

        /// <summary>日志统一加毫秒时间戳 —— 联机排查没有时间轴寸步难行（r34）。</summary>
        private static string Ts() { return "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] "; }

        public static MusicSync Instance { get; private set; }

        // 回调登记的目标 client（r34：谁是活的就登记到谁身上，换实例自动重挂）
        private Photon.Realtime.LoadBalancingClient _registeredClient;
        private bool _wasInRoom;
        private int _evSeen;                 // 事件心跳：证明回调链路活着
        private string _presenceRoom = "";   // 已经在哪个房间登记过模组身份
        private DateTime _presenceTryAt = DateTime.MinValue;
        private float _evWindowStart;

        // 一起听总开关（面板里房主可切）。默认开，装了这个 mod 就参与。
        public bool Enabled = true;

        /// <summary>是否房主（= Photon 主客户端）。单人离线房间也视为房主。</summary>
        public bool IsHost { get { return PhotonNetwork.IsMasterClient; } }

        /// <summary>是否在联机房间里（Photon 已连接且进了房）。</summary>
        public bool InRoom { get { return PhotonNetwork.InRoom && PhotonNetwork.IsConnectedAndReady; } }

        /// <summary>把自己「装了模组」写进 Photon 玩家属性（每个房间写一次，失败 2 秒后重试）。</summary>
        private void PublishPresence()
        {
            if (!InRoom) { _presenceRoom = ""; return; }

            var room = PhotonNetwork.CurrentRoom;
            string name = room != null ? room.Name : "?";
            if (_presenceRoom == name) return;
            if (DateTime.UtcNow < _presenceTryAt) return;

            try
            {
                var props = new Hashtable();
                props[ModPropKey] = Plugin.Version ?? "1";
                if (PhotonNetwork.LocalPlayer.SetCustomProperties(props))
                {
                    _presenceRoom = name;
                    Log.LogInfo(Ts() + Tag + " 已登记模组身份：房间 " + name + " 版本 " + Plugin.Version);
                    return;
                }
            }
            catch (Exception e)
            {
                Log.LogWarning(Ts() + Tag + " 登记模组身份失败：" + e.Message);
            }
            _presenceTryAt = DateTime.UtcNow.AddSeconds(2);
        }

        private static bool Modded(Photon.Realtime.Player p)
        {
            if (p == null) return false;
            try
            {
                var props = p.CustomProperties;
                return props != null && props.ContainsKey(ModPropKey) && props[ModPropKey] != null;
            }
            catch { return false; }
        }

        /// <summary>
        /// 房主是否装了本模组。「一起听」的播放源在房主机器上，
        /// 房主没装就没人是播放源 —— 客机点歌只会挂在队列里没人批。
        /// </summary>
        public bool HostHasMod
        {
            get
            {
                if (!InRoom) return false;
                Photon.Realtime.Player host = PhotonNetwork.MasterClient;
                if (host == null) return false;
                if (host.IsLocal) return true;
                return Modded(host);
            }
        }

        /// <summary>房间里装了本模组的人数（含自己）。</summary>
        public int ModdedCount
        {
            get
            {
                if (!InRoom) return 0;
                int n = 0;
                try
                {
                    var list = PhotonNetwork.PlayerList;
                    if (list == null) return 0;
                    for (int i = 0; i < list.Length; i++)
                    {
                        Photon.Realtime.Player p = list[i];
                        if (p == null) continue;
                        if (p.IsLocal ? _presenceRoom.Length > 0 : Modded(p)) n++;
                    }
                }
                catch { }
                return n;
            }
        }

        /// <summary>房主不在房间时（单人），一起听退化成本地播放，不广播。</summary>
        public bool CanBroadcast { get { return InRoom && IsHost; } }

        // 房间内其他装了 mod 的玩家数（粗略：房间人数 - 1，暂不精确区分是否装 mod）
        public int ListenerCount
        {
            get { return InRoom && PhotonNetwork.CurrentRoom != null ? Mathf.Max(0, PhotonNetwork.CurrentRoom.PlayerCount - 1) : 0; }
        }

        // ── 点歌队列（房主持有权威副本；客机只读收到的广播） ──
        public readonly List<SongInfo> Queue = new List<SongInfo>();
        /// <summary>与 Queue 同下标的点歌人昵称（面板「一起听」视图显示用）。</summary>
        public readonly List<string> QueueRequesters = new List<string>();
        /// <summary>
        /// 与 Queue 同下标的播放直链（点歌人自己账号取的流，r40）。
        /// 取流权限跟「点歌人」走而不是跟房主：客机点的 VIP 歌，用客机自己的会员取流，
        /// 房主批准时直接播这条链并广播全队 —— 谁点的就用谁的权限，房主没会员也能放客机点的 VIP 歌。
        /// </summary>
        public readonly List<string> QueueUrls = new List<string>();
        public int QueueCursor = -1;   // 当前播到队列第几首；-1 = 房主手动选歌（不在队列里）

        /// <summary>入队（房主侧收点歌 / 本地补录）。队列/点歌人/直链三张表保持同长。</summary>
        public void AddQueueItem(SongInfo song, string requester, string url)
        {
            if (song == null) return;
            Queue.Add(song);
            QueueRequesters.Add(requester ?? "");
            QueueUrls.Add(url ?? "");
        }

        /// <summary>出队（批准播放 / 移除）。</summary>
        public void RemoveQueueItem(int index)
        {
            if (index < 0 || index >= Queue.Count) return;
            Queue.RemoveAt(index);
            if (index < QueueRequesters.Count) QueueRequesters.RemoveAt(index);
            if (index < QueueUrls.Count) QueueUrls.RemoveAt(index);
        }

        /// <summary>取第 i 项的点歌人（越界给空串）。</summary>
        public string RequesterAt(int index)
        {
            return index >= 0 && index < QueueRequesters.Count ? (QueueRequesters[index] ?? "") : "";
        }

        /// <summary>取第 i 项的播放直链（越界/未取到给空串，空则调用方回退自己取流）。</summary>
        public string QueueUrlAt(int index)
        {
            return index >= 0 && index < QueueUrls.Count ? (QueueUrls[index] ?? "") : "";
        }

        /// <summary>事件：客机收到「开始播放」指令，携带歌曲+URL+起始秒。</summary>
        public event Action<SongInfo, string, float> OnRemotePlay;
        /// <summary>事件：客机收到「暂停/继续」。参数 true=暂停。</summary>
        public event Action<bool> OnRemotePause;
        /// <summary>事件：客机收到「停止」。</summary>
        public event Action OnRemoteStop;
        /// <summary>事件：客机收到「跳转进度」秒数。</summary>
        public event Action<float> OnRemoteSeek;
        /// <summary>事件：房主收到「点歌」。参数：歌曲、点歌人、点歌人取好的直链（可空）。</summary>
        public event Action<SongInfo, string, string> OnQueueAdd;
        /// <summary>事件：全员收到队列快照更新（客机用它刷新队列显示）。</summary>
        public event Action<string> OnQueueState;

        private void Awake()
        {
            Instance = this;
            EnsureRegistered();
            Log.LogInfo(Ts() + Tag + " 同步层已挂载（等待进入房间）");
        }

        private void OnDestroy()
        {
            if (_registeredClient != null)
            {
                try { _registeredClient.RemoveCallbackTarget(this); } catch { }
                _registeredClient = null;
            }
            if (Instance == this) Instance = null;
        }

        /// <summary>
        /// 把自己登记到「当前活着的」Photon client 上（r34 自愈）。
        /// 之前只在 Awake 登记一次：client 实例一换（重连/游戏层重建），
        /// 回调就永久丢失，表现为「对面广播了、这边一声不吭」。
        /// </summary>
        private void EnsureRegistered()
        {
            var c = PhotonNetwork.NetworkingClient;
            if (c == null) return;
            if (_registeredClient == c) return;

            if (_registeredClient != null)
            {
                try { _registeredClient.RemoveCallbackTarget(this); } catch { }
            }
            try
            {
                c.AddCallbackTarget(this);
            }
            catch (Exception e)
            {
                Log.LogWarning(Ts() + Tag + " 回调登记失败：" + e.Message);
                return;
            }
            _registeredClient = c;
            Log.LogInfo(Ts() + Tag + " 事件回调已登记（client#" + c.GetHashCode() + " state=" + c.State + "）");
        }

        private void Update()
        {
            EnsureRegistered();
            PublishPresence();

            bool inRoom = InRoom;
            if (inRoom && !_wasInRoom)
            {
                EnsureRegistered();
                Log.LogInfo(Ts() + Tag + " 进房：room=" +
                    (PhotonNetwork.CurrentRoom != null ? PhotonNetwork.CurrentRoom.Name : "?") +
                    " actor=" + PhotonNetwork.LocalPlayer.ActorNumber +
                    " 人数=" + (PhotonNetwork.CurrentRoom != null ? PhotonNetwork.CurrentRoom.PlayerCount : 0) +
                    " 房主=" + IsHost);
            }
            else if (!inRoom && _wasInRoom)
            {
                Log.LogInfo(Ts() + Tag + " 离房");
            }
            _wasInRoom = inRoom;

            // 事件心跳：30 秒窗口里收到过任何 Photon 事件就打一条，证明回调链路活着
            if (_evSeen > 0 && UnityEngine.Time.unscaledTime - _evWindowStart > 30f)
            {
                Log.LogInfo(Ts() + Tag + " 事件链路活跃：近 30s 收到 " + _evSeen + " 个一起听事件");
                _evSeen = 0;
                _evWindowStart = UnityEngine.Time.unscaledTime;
            }
        }

        // ───────────────────────── 发送端（房主） ─────────────────────────

        /// <summary>房主广播「开始播这首歌」。</summary>
        public void BroadcastPlay(SongInfo song, string url, float startAt)
        {
            if (!CanBroadcast) { Log.LogDebug(Tag + " 不在房主位，跳过广播播放"); return; }

            var payload = new object[]
            {
                song.Name, song.Artists, song.Album, song.Platform.ToString(), song.SourceId,
                song.Id, song.DurationMs, url, startAt
            };
            Raise(EvHostPlay, payload);
            Log.LogInfo(Ts() + Tag + " 房主广播播放：" + song.Name + " - " + song.Artists + " @ " + startAt.ToString("F1") + "s");
        }

        /// <summary>房主广播「暂停/继续」。true=暂停。</summary>
        public void BroadcastPause(bool paused)
        {
            if (!CanBroadcast) return;
            Raise(EvHostPause, new object[] { paused });
        }

        /// <summary>房主广播「停止」。</summary>
        public void BroadcastStop()
        {
            if (!CanBroadcast) return;
            Raise(EvHostStop, null);
        }

        /// <summary>房主广播「跳转到 N 秒」。</summary>
        public void BroadcastSeek(float seconds)
        {
            if (!CanBroadcast) return;
            Raise(EvHostSeek, new object[] { seconds });
        }

        /// <summary>
        /// 客机点歌：发给房主。房主不在/不参与就忽略。
        /// <paramref name="url"/> 是点歌人自己账号取好的播放直链（r40，可空）——
        /// 空则房主批准时回退用房主账号取流（老行为）。
        /// </summary>
        public void RequestQueue(SongInfo song, string requester, string url)
        {
            if (!InRoom) return;
            Raise(EvQueueAdd, new object[]
            {
                song.Name, song.Artists, song.Album, song.Platform.ToString(), song.SourceId,
                song.Id, song.DurationMs, requester, url ?? ""
            });
            Log.LogInfo(Tag + " 点歌 → 房主：" + song.Name + "（" + requester + "）" +
                (string.IsNullOrEmpty(url) ? "〔无直链，房主自取〕" : "〔带点歌人直链〕"));
        }

        /// <summary>房主广播队列快照（批准后 / 移除后）。队列序列化成一行字符串。</summary>
        public void BroadcastQueueState()
        {
            if (!CanBroadcast) return;
            Raise(EvQueueState, new object[] { SerializeQueue() });
        }

        private void Raise(byte code, object content)
        {
            if (!InRoom) { Log.LogDebug(Ts() + Tag + " 跳过发送 code=" + code + "：不在房间"); return; }

            // 千万不要复用 RaiseEventOptions.Default —— 它是全局共享静态实例，
            // 游戏自带 netcode / 其他 mod 只要改过它的 TargetActors/InterestGroup，
            // 我们的事件就会被定向到错误目标（实测：同房发出去、对面零接收）。
            var opts = new RaiseEventOptions
            {
                Receivers = ReceiverGroup.All,
                CachingOption = EventCaching.DoNotCache
            };
            bool ok = PhotonNetwork.RaiseEvent(code, content, opts, SendOptions.SendReliable);
            Log.LogInfo(Ts() + Tag + " 发送 code=" + code + " ok=" + ok +
                " room=" + (PhotonNetwork.CurrentRoom != null ? PhotonNetwork.CurrentRoom.Name : "?") +
                " 人数=" + (PhotonNetwork.CurrentRoom != null ? PhotonNetwork.CurrentRoom.PlayerCount : 0) +
                " actor=" + PhotonNetwork.LocalPlayer.ActorNumber);
        }

        // ───────────────────────── 接收端（IOnEventCallback） ─────────────────────────

        public void OnEvent(EventData photonEvent)
        {
            byte code = photonEvent.Code;
            if (code < 90 || code > 95) return;   // 不是我们的事件

            // r39：心跳只数我们的事件——OnEvent 连游戏/其他 mod 的事件都会收到
            // （实测 30s 上千条），全数进来会被误判成"事件风暴"刷屏。
            _evSeen++;
            if (_evWindowStart == 0f) _evWindowStart = UnityEngine.Time.unscaledTime;

            object[] a = photonEvent.CustomData as object[];
            Log.LogInfo(Ts() + Tag + " 收到事件 code=" + code + " sender=" + photonEvent.Sender +
                " 数据=" + (a != null ? a.Length + "项" : "null"));

            try
            {
                // 自己发的也回调回来（Receivers=All），房主自己收到自己的播放指令要跳过，避免双重触发
                bool fromSelf = photonEvent.Sender == PhotonNetwork.LocalPlayer.ActorNumber;

                switch (code)
                {
                    case EvHostPlay:
                        if (a != null && a.Length >= 9 && !fromSelf)
                        {
                            var song = FromArray(a, 0, "");
                            string url = (string)a[7];
                            float start = (float)Convert.ToDouble(a[8]);
                            if (song != null && !string.IsNullOrEmpty(url))
                            {
                                Log.LogInfo(Ts() + Tag + " 收到播放指令：" + song.Name + " @ " + start.ToString("F1") + "s");
                                var h = OnRemotePlay;
                                if (h != null) h(song, url, start);
                                else Log.LogWarning(Ts() + Tag + " 没有订阅者响应播放指令（面板订阅丢了？）");
                            }
                        }
                        break;

                    case EvHostPause:
                        if (a != null && a.Length >= 1 && !fromSelf)
                        {
                            bool paused = Convert.ToBoolean(a[0]);
                            var h = OnRemotePause;
                            if (h != null) h(paused);
                        }
                        break;

                    case EvHostStop:
                        if (!fromSelf)
                        {
                            var h = OnRemoteStop;
                            if (h != null) h();
                        }
                        break;

                    case EvHostSeek:
                        if (a != null && a.Length >= 1 && !fromSelf)
                        {
                            float sec = (float)Convert.ToDouble(a[0]);
                            var h = OnRemoteSeek;
                            if (h != null) h(sec);
                        }
                        break;

                    case EvQueueAdd:
                        // 只有房主处理点歌；房主自己点自己？走本地直接入队即可
                        if (IsHost && a != null && a.Length >= 8)
                        {
                            var song = FromArray(a, 0, "");
                            string requester = (string)a[7];
                            string url = a.Length >= 9 ? (a[8] as string ?? "") : "";
                            if (song != null)
                            {
                                var h = OnQueueAdd;
                                if (h != null) h(song, requester, url);
                            }
                        }
                        break;

                    case EvQueueState:
                        if (a != null && a.Length >= 1 && !fromSelf)
                        {
                            var h = OnQueueState;
                            if (h != null) h((string)a[0]);
                        }
                        break;
                }
            }
            catch (Exception e)
            {
                Log.LogError(Ts() + Tag + " 处理事件异常 code=" + code + "：" + e);
            }
        }

        // ───────────────────────── 队列序列化（跨机传输用一行文本） ─────────────────────────

        private string SerializeQueue()
        {
            var sb = new StringBuilder();
            sb.Append(QueueCursor).Append('\n');
            for (int i = 0; i < Queue.Count; i++)
            {
                var s = Queue[i];
                sb.Append(s.Name).Append('\t')
                  .Append(s.Artists).Append('\t')
                  .Append(s.Album).Append('\t')
                  .Append(s.Platform.ToString()).Append('\t')
                  .Append(s.SourceId).Append('\t')
                  .Append(s.Id).Append('\t')
                  .Append(s.DurationMs).Append('\t')
                  .Append(RequesterAt(i)).Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>
        /// 客机收到队列快照后还原成 Queue + QueueRequesters（面板「一起听」视图直接读）。
        /// 格式：第一行 QueueCursor，之后每行一首，字段 Tab 分隔（8 列，最后一列是点歌人）。
        /// 注：直链（QueueUrls）只在房主本地用、不随快照广播（客机只看不播），这里补空串保持三表同长。
        /// </summary>
        public void ApplyQueueSnapshot(string serialized)
        {
            Queue.Clear();
            QueueRequesters.Clear();
            QueueUrls.Clear();
            if (string.IsNullOrEmpty(serialized)) return;

            string[] lines = serialized.Split('\n');
            int cursor;
            if (lines.Length > 0 && int.TryParse(lines[0].Trim(), out cursor)) QueueCursor = cursor;

            for (int i = 1; i < lines.Length; i++)
            {
                string[] parts = lines[i].Split('\t');
                if (parts.Length < 7) continue;

                var s = new SongInfo();
                s.Name = parts[0];
                s.Artists = parts[1];
                s.Album = parts[2];
                s.Platform = ParsePlatform(parts[3]);
                s.SourceId = parts[4];
                long id, dur;
                long.TryParse(parts[5], out id);
                long.TryParse(parts[6], out dur);
                s.Id = id;
                s.DurationMs = dur;
                Queue.Add(s);
                QueueRequesters.Add(parts.Length >= 8 ? parts[7] : "");
                QueueUrls.Add("");   // 快照不带直链，客机端恒空（房主本地的 QueueUrls 由 EvQueueAdd 填）
            }
        }

        /// <summary>把事件里的 object[] 还原成 SongInfo（前 7 个元素是歌曲字段）。</summary>
        internal static SongInfo FromArray(object[] a, int baseIdx, string fallbackSourceId)
        {
            if (a == null || a.Length < baseIdx + 6) return null;
            var s = new SongInfo();
            s.Name = a[baseIdx + 0] as string ?? "";
            s.Artists = a[baseIdx + 1] as string ?? "";
            s.Album = a[baseIdx + 2] as string ?? "";
            string plat = a[baseIdx + 3] as string;
            s.Platform = ParsePlatform(plat);
            s.SourceId = a[baseIdx + 4] as string ?? fallbackSourceId;
            s.Id = Convert.ToInt64(a[baseIdx + 5]);
            s.DurationMs = Convert.ToInt64(a[baseIdx + 6]);
            return s;
        }

        private static Platform ParsePlatform(string p)
        {
            if (string.Equals(p, "QQ", StringComparison.OrdinalIgnoreCase)) return Platform.QQ;
            return Platform.Netease;
        }
    }
}
