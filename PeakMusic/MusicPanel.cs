using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BepInEx.Logging;
using PeakMusic.Core.Netease;
using PeakMusic.Core.Qr;
using PeakMusic.Core.Sources;
using Photon.Pun;
using UnityEngine;
using UnityEngine.InputSystem;

namespace PeakMusic
{
    /// <summary>
    /// 阶段 3 的"丑面板"：F1 开关 → 输入歌名 → 搜索 → 点一首 → 出声。
    ///
    /// 为什么不用 GUI.Button / GUI.TextField：
    ///   PEAK 只打开了新输入系统（实测热键走的是 Input System），IMGUI 的键盘/鼠标事件
    ///   在新输入系统下不可靠。所以这里**只把 IMGUI 当画笔**（Box / Label），
    ///   点击和打字全部自己从 Keyboard.current / Mouse.current 读，不依赖 Event.current。
    /// </summary>
    internal sealed class MusicPanel : MonoBehaviour
    {
        private const string Tag = "[面板]";

        private const float PanelW = 832f;
        private const float PanelH = 696f;
        private const float GripH = 26f;     // 顶部标题栏杆高（拖动区 + 窗口按钮）
        private const float RowH = 40f;
        private const int PageSize = 8;      // 一屏几行
        private const int FetchLimit = 40;   // 一次搜多少首（本地翻页，5 页；不重复请求，省风控）
        private const float CoverSize = 30f; // 列表行里的小封面
        private const float BigCover = 52f;  // 头部正在播放的封面
        private const float NavW = 96f;      // 左侧音源导航宽度
        private const float ScaleMin = 0.7f;
        private const float ScaleMax = 1.5f;

        private static readonly string[] Presets = { "雨爱", "下雨天", "江南", "晴天" };

        private bool _open;
        private bool _typing;
        private string _keyword = "";
        private string _status = "面板就绪";
        private string _loginLine = "";

        // ── 扫码登录层 ──
        // 网易云的会员曲必须登录（未登录一律 -110）。之前只在命令行沙盒里做过扫码，
        // 游戏里连入口都没有，所以"进游戏没弹二维码"——不是没弹，是压根没实现。
        private const string LoginUrlPrefix = "https://music.163.com/login?codekey=";
        private bool _loginOpen;
        private bool _isQqLogin;            // 当前登录卡片是 QQ 还是网易云
        private bool _qqAppScan = true;     // QQ 扫码方式：true=QQ音乐App扫码（r43），false=手机QQ扫码（ptlogin 老路）
        private bool _loginAutoTried;       // 每次游戏只在第一次开面板时自动弹一次
        private int _loginState;            // 0关 1取码 2待扫 3已扫 4成功 5过期 6风控 7失败
        private string _loginMsg = "";
        private string _qrText = "";
        private string _qrKey = "";
        private byte[] _qrPng;              // QQ 二维码 PNG 字节
        private string _qrCookie = "";
        private QrState _qrResult = QrState.Unknown;
        private QqLoginResult.QqLoginState _qqResult = QqLoginResult.QqLoginState.Unknown;
        private string _qrError = "";
        private Texture2D _qrTex;
        private Task _qrTask;
        private int _qrTaskKind;            // 1 取 unikey / 2 轮询
        private float _nextPollAt;
        private float _loginDoneAt;

        private readonly List<SongInfo> _songs = new List<SongInfo>();
        private int _page;              // 本地列表的起始下标（0 / 8 / 16 …）
        private int _pendingScroll;     // 滚轮方向：-1 上一页 / +1 下一页 / 0 无

        // 服务器翻页：本地列表翻到底后，自动向平台继续拉下一批
        private string _kw = "";        // 当前搜索词（拉下一批要用）
        private int _offSearch;         // 当前平台已拉到哪（r37 起按平台各搜各的，一个游标就够）
        private bool _hasMore;          // 服务器还有没有更多（返回不满一批就当没有了）
        private const int MoreBatch = 40;

        private MusicPlayer _player;
        private MusicSources _client;

        // ── 列表视图：0 搜索结果 / 1 收藏 ──
        private int _view;                          // 0 搜索 / 1 收藏
        private readonly List<SongInfo> _favorites = new List<SongInfo>();
        private readonly List<string> _history = new List<string>();
        private bool _libDirty = true;              // 收藏/历史有变动，下次进视图重读
        private bool _wasPlaying;                   // 上一帧播放中标记（检测播完切下一首）
        private float _playEndGrace;                // 播完后短暂防抖，别连跳两首

        /// <summary>当前选中的音源平台。切平台只换搜索目标，已搜到的列表照旧保留（点谁回谁的源取流）。</summary>
        private Platform _platform = Platform.Netease;

        // 异步任务（搜索 / 取播放地址 / 取歌词）在后台线程跑，主线程只轮询结果
        private Task _task;
        private int _taskKind;          // 0 无 / 1 搜索 / 2 取地址 / 3 续拉 / 4 客机点歌（先取流再入队）
        private List<SongInfo> _pendingSongs;
        private SongUrlResult _pendingUrl;
        private SongInfo _pendingSong;
        private string _pendingRequester;   // 客机点歌：等取完流一起发给房主的点歌人昵称
        private string _taskError;
        private bool _busy;
        private bool _queueFallback;   // r41：队列回退房主自取时置位，失败提示要说清「点歌人和房主都没权限」

        // 键盘文本输入
        private Keyboard _textKb;

        // 鼠标
        private Vector2 _mousePos;
        private bool _mouseValid;
        private bool _clicked;
        private bool _held;          // 左键持续按住（拖动窗口用）
        private Vector2 _lm;         // 鼠标换算到面板本地坐标（已除缩放）

        // ── 窗口几何 ──
        // 面板不再钉死在屏幕正中：位置/缩放/折叠都是自己的状态，会写回 cfg。
        // 绘制用 GUI.matrix 整体缩放，命中测试用 _lm 反算，两套坐标靠 ComputeLayout 对齐。
        private Vector2 _panelPos;
        private float _scale = 1f;
        private bool _collapsed;
        private bool _dragging;
        private Vector2 _dragGrab;      // 按下时鼠标相对面板左上角的偏移
        private bool _resizing;
        private float _resizeBaseScale;
        private Vector2 _resizeGrabPos;

        // ── 鼠标光标接管 ──
        // 光锁着的时候 Mouse.current.position 恒等于屏幕中心，点击全打在中心那一点上，
        // 所以之前"必须按 Esc 弹出游戏光标才能点面板"。开面板就自己把光标放出来。
        private bool _cursorOwned;
        private CursorLockMode _prevLock;
        private bool _prevVisible;

        // 样式：全部自己画（圆角九宫格贴图 + 动态系统字体），不用 GUI.skin 默认外观。
        // 颜色一律从 _pal（Palette）取，换主题只需重建这套样式。
        private Font _font;
        private GUIStyle _sPanel, _sPanelThin, _sShadow, _sCard, _sField, _sFieldFocus, _sBar;
        private GUIStyle _sBtn, _sBtnHi, _sBtnOff, _sPrimary, _sPrimaryHi;
        private GUIStyle _sChip, _sChipHi, _sRowHi, _sRowPlay, _sBadge, _sCover;
        private GUIStyle _h1, _sub, _body, _bodyHi, _dim, _placeholder, _tiny;
        private GUIStyle _rowText, _rowTextHi, _artist, _dur;
        private GUIStyle _statusOk, _statusErr, _hintCenter, _stateRight, _timeText, _barDim;
        private GUIStyle _btnText, _btnTextOff, _btnTextPrimary, _btnTextSmall, _chipText, _badgeOk, _badgeBusy;
        private GUIStyle _sLight, _loginStatus, _loginStatusHint;
        private Texture2D _texDivider, _texAccent, _texProgBg, _texProgFill, _texCoverEmpty, _texDot, _loginMask;
        private readonly List<Texture2D> _texs = new List<Texture2D>();
        private bool _styleReady;
        private bool _lyricHotkeyDead;       // 歌词热键与面板热键冲突：这个键不处理
        private bool _lyricHotkeyCollides;   // 仅用于日志/Tips
        private string _accentHex = "#FFFFFF";   // 富文本用，随主题重建
        private string _faintHex = "#808080";

        private Palette _pal;
        private int _themeIdx;
        private CoverCache _covers;

        // 布局（Update 与 OnGUI 用同一套，保证命中区域和画出来的一致）
        private Rect _rPanel, _rTitle, _rArtist, _rBadge, _rTheme, _rHeaderLine;
        private Rect _rSearch, _rSearchBtn, _rClear, _rStatus, _rCover;

        // 标题栏（拖动区 + 关闭/折叠/缩放按钮 + 右下角缩放手柄）
        private Rect _rGrip, _rClose, _rCollapse, _rZoomIn, _rZoomOut, _rResize;

        // 左侧音源导航（照 MuoniumPlayer 的侧栏骨架）
        private Rect _rNav, _rNavLine, _rLyricPos, _rLyricColor, _rLyricSize, _rLyricHeight, _rLyricEdge, _rLyricDist;
        private readonly Rect[] _rNavItem = new Rect[Platforms.All.Length];
        private Rect _rList, _rPrev, _rNext, _rPageInfo, _rBar, _rProgress;
        private Rect _rPause, _rStop, _rVolDown, _rVolUp, _rVolText, _rTime, _rHint;
        private readonly Rect[] _rPreset = new Rect[Presets.Length];
        private Rect _rFavNav;                                  // 导航底部「收藏」入口
        private Rect _rQueueNav;                                // 收藏下方「一起听」入口（点歌队列）
        private readonly Rect[] _rFavBtn = new Rect[PageSize];    // 列表行右侧收藏按钮

        // 登录层
        private Rect _rLoginCard, _rLoginQr, _rLoginRefresh, _rLoginClip, _rLoginClose, _rLoginLogout, _rLoginMode; // r43: _rLoginMode=扫码方式切换

        private static ManualLogSource Log { get { return Plugin.Log; } }

        private MusicSources Client
        {
            get
            {
                if (_client == null) _client = new MusicSources();
                return _client;
            }
        }

        private void Awake()
        {
            _player = gameObject.AddComponent<MusicPlayer>();
            _covers = gameObject.AddComponent<CoverCache>();
            _themeIdx = Mathf.Clamp(Plugin.Theme.Value, 0, Palettes.Count - 1);
            _pal = Palettes.Get(_themeIdx);
            _platform = Platforms.FromInt(Plugin.SourcePlatform.Value);

            _scale = Mathf.Clamp(Plugin.PanelScale.Value, ScaleMin, ScaleMax);
            _collapsed = Plugin.PanelCollapsed.Value;

            // 歌词热键和面板热键撞车（比如 PanelKey=F4）时，整个忽略歌词热键：
            // 否则按一次面板键会顺手把歌词关掉，看起来就像"必须按 F4 才有歌词"。
            string lk = NormalizeKeys(Plugin.LyricKey != null ? Plugin.LyricKey.Value : "");
            string pk = NormalizeKeys(PanelKeyCfg);
            _lyricHotkeyDead = lk.Length == 0 || lk == pk;
            _lyricHotkeyCollides = lk.Length > 0 && lk == pk;

            RefreshLibrary();   // 启动时载入收藏与搜索历史

            // 「一起听」订阅放在 Start()（见下方）——
            // Awake 在 AddComponent 瞬间就跑，而 Plugin 先加面板后加 MusicSync，
            // 在这里订阅时 MusicSync.Instance 还是 null，整段会被静默跳过（r34 修）。
        }

        // ───────────────────── 一起听：客机接收房主指令 ─────────────────────

        private void HandleRemotePlay(SongInfo song, string url, float startAt)
        {
            if (_player == null) return;
            Log.LogInfo(Tag + " 一起听：同步播放 " + song.Name + " - " + song.Artists);
            _status = "一起听：" + song.Name;
            _player.Play(song, url, startAt);
            StartLyricFetch(song);
        }

        private void HandleRemotePause(bool paused)
        {
            if (_player == null || !_player.HasClip) return;
            if (paused)
            {
                if (_player.IsPlaying) _player.TogglePause();
            }
            else
            {
                if (!_player.IsPlaying) _player.TogglePause();
            }
        }

        private void HandleRemoteStop()
        {
            if (_player == null) return;
            _player.Stop();
        }

        private void HandleRemoteSeek(float seconds)
        {
            if (_player == null || !_player.HasClip) return;
            // 播放器只在整曲下载模式支持 seek；这里尽量对齐，失败也不致命
            Log.LogInfo(Tag + " 一起听：进度对齐 " + seconds.ToString("F1") + "s");
        }

        private void HandleQueueAdd(SongInfo song, string requester, string url)
        {
            var sync = MusicSync.Instance;
            if (sync == null) return;
            // r39：按平台+ID 去重（Contains 是引用比较，网络来的对象每次都是新的，
            // 同一首歌连点会堆一排重复——实测雨爱被塞了 5 份）
            bool dup = false;
            for (int i = 0; i < sync.Queue.Count; i++)
            {
                var q = sync.Queue[i];
                if (q != null && q.Platform == song.Platform && q.Key == song.Key) { dup = true; break; }
            }
            if (dup) return;
            sync.AddQueueItem(song, requester, url);
            _status = "点歌：" + song.Name + "（" + requester + "）已入队";
            Log.LogInfo(Tag + " 点歌入队：" + song.Name + "（" + requester + "）队列 " + sync.Queue.Count + " 首" +
                (string.IsNullOrEmpty(url) ? "〔无直链〕" : "〔带点歌人直链〕"));
            sync.BroadcastQueueState();
        }

        private void HandleQueueState(string serialized)
        {
            // 客机收到队列快照 → 还原进 MusicSync.Queue，「一起听」视图直接读
            var sync = MusicSync.Instance;
            if (sync == null) return;
            sync.ApplyQueueSnapshot(serialized);
            Log.LogInfo(Tag + " 一起听：队列更新（" + sync.Queue.Count + " 首）");
        }

        private float TotalH { get { return _collapsed ? GripH : GripH + PanelH; } }

        private void Start()
        {
            Log.LogInfo(Tag + " 面板已就绪。热键 " + PanelKeyName + " 开关；标题栏可拖动，Ctrl+滚轮或右下角拖拽缩放。");
            if (_lyricHotkeyCollides)
                Log.LogInfo(Tag + " 歌词热键与面板热键相同（" + NormalizeKeys(PanelKeyCfg) +
                            "），已忽略歌词热键 —— 歌词默认就是开着的，放歌自动出现；" +
                            "要临时关掉就点面板左侧「歌词」行，或把 [歌词] Hotkey 改成别的键。");

            // 「一起听」客机端：订阅房主的远程播放/暂停/停止/进度指令。
            // 必须在 Start 里订（所有 Awake 都跑完、MusicSync.Instance 已就位）——
            // 以前放 Awake 里订，Plugin 先加面板后加 MusicSync，Instance 是 null，
            // 订阅整段静默跳过，对面广播了这边也不出声（r34 修）。
            var sync = MusicSync.Instance;
            if (sync != null)
            {
                sync.OnRemotePlay += HandleRemotePlay;
                sync.OnRemotePause += HandleRemotePause;
                sync.OnRemoteStop += HandleRemoteStop;
                sync.OnRemoteSeek += HandleRemoteSeek;
                sync.OnQueueAdd += HandleQueueAdd;
                sync.OnQueueState += HandleQueueState;
                Log.LogInfo(Tag + " 一起听指令订阅完成");
            }
            else
            {
                Log.LogWarning(Tag + " 一起听：MusicSync 不存在，同步接收不可用");
            }
        }

        // ───────────────────────────── 主循环 ─────────────────────────────

        private void Update()
        {
            if (PanelKeyDown())
                Toggle();

            // 歌词热键（默认 F4）：歌词本来就会跟着音乐自动出现，这个键只是"临时关一下"。
            // 关键：如果它和面板热键是同一个键（很多人的 PanelKey 就是 F4），就整个忽略 ——
            // 否则按一次面板键会顺手把歌词关掉，看起来就像"必须按 F4 才有歌词"。
            if (!_lyricHotkeyDead && LyricKeyDown())
            {
                Plugin.LyricEnabled.Value = !Plugin.LyricEnabled.Value;
                Log.LogInfo(Tag + " 3D 歌词 " + (Plugin.LyricEnabled.Value ? "开" : "关") + "（已存进 cfg）");
            }

            // 歌词是独立通道：不占 _busy，面板关着也照样把结果交给歌词层
            PollLyric();

            // 扫码轮询同理：面板关掉也不该中断，否则一关一开就得重扫
            PollLogin();

            AutoAdvance();   // 一首播完自动接下一首（面板关着也要能切）

            if (!_open) return;

            ComputeLayout();
            UpdateMouse();
            HandleMouse();
            HandleKeyboard();
            PollTask();
        }

        /// <summary>
        /// 光标留在面板打开期间必须一直"解锁"。游戏自己的 Update 每帧都会把鼠标锁回去，
        /// 而 LateUpdate 排在它后面，所以在这儿抢回来最稳。
        /// </summary>
        private void LateUpdate()
        {
            if (!_open || !_cursorOwned) return;
            ApplyCursor();
        }

        private void Toggle()
        {
            _open = !_open;
            if (_open)
            {
                PlaceWindow();
                ComputeLayout();
                OwnCursor();
                RefreshLoginLine();
                Log.LogInfo(Tag + " 打开面板。" + _loginLine);
                if (string.IsNullOrEmpty(_keyword)) _keyword = "";

                // 未登录就自动弹二维码：这是"点会员曲没反应"的头号原因，
                // 藏着不说用户只会以为插件坏了。一次游戏只自动弹一次，别烦人。
                bool needLogin = _platform == Platform.Netease ? !HasNeteaseLogin() : !HasQqLogin();
                if (!_loginAutoTried && needLogin)
                {
                    _loginAutoTried = true;
                    StartLogin();
                }
            }
            else
            {
                StopTyping();
                CloseLogin();
                _dragging = false;
                _resizing = false;
                SaveWindow();
                ReleaseCursor();
                Log.LogInfo(Tag + " 关闭面板。");
            }
        }

        // ───────────────────────────── 光标 ─────────────────────────────

        private void OwnCursor()
        {
            if (!_cursorOwned)
            {
                try { _prevLock = Cursor.lockState; _prevVisible = Cursor.visible; } catch { }
                _cursorOwned = true;
            }
            ApplyCursor();
        }

        private void ApplyCursor()
        {
            try
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
            catch { }
        }

        private void ReleaseCursor()
        {
            if (!_cursorOwned) return;
            _cursorOwned = false;
            try
            {
                // 关掉面板就还原游戏原本的光标状态（大多是 Locked + 隐藏）
                Cursor.lockState = _prevLock;
                Cursor.visible = _prevVisible;
            }
            catch { }
        }

        // ───────────────────────────── 窗口几何 ─────────────────────────────

        private string PanelKeyName
        {
            get
            {
                string s = Plugin.PanelKey != null ? Plugin.PanelKey.Value : "";
                return string.IsNullOrEmpty(s) ? "F1/F2" : s;
            }
        }

        /// <summary>热键可自定义：cfg 里写 F8 / P / Insert，或 "F1,F2" 这种多键。</summary>
        private string PanelKeyCfg
        {
            get
            {
                string cfg = Plugin.PanelKey != null ? Plugin.PanelKey.Value : "";
                return string.IsNullOrWhiteSpace(cfg) ? "F1,F2" : cfg;
            }
        }

        private bool PanelKeyDown()
        {
            return ConfigKeyDown(PanelKeyCfg);
        }

        /// <summary>歌词开关热键（默认 F4）。和面板键冲突时由 <see cref="_lyricHotkeyDead"/> 拦掉。</summary>
        private bool LyricKeyDown()
        {
            string cfg = Plugin.LyricKey != null ? Plugin.LyricKey.Value : "F4";
            if (string.IsNullOrWhiteSpace(cfg)) return false;
            return ConfigKeyDown(cfg);
        }

        /// <summary>解析 "F8" / "F1,F2" / "P" 这种串，返回这一帧有没有按下其中任意一个键。</summary>
        private bool ConfigKeyDown(string cfg)
        {
            if (string.IsNullOrWhiteSpace(cfg)) return false;

            bool hit = false;
            string[] parts = cfg.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                string s = parts[i].Trim();
                if (s.Length == 0) continue;

                Key k;
                if (Enum.TryParse<Key>(s, true, out k) && k != Key.None)
                {
                    try
                    {
                        var kb = Keyboard.current;
                        if (kb != null)
                        {
                            var c = kb[k];
                            if (c != null && c.wasPressedThisFrame) hit = true;
                        }
                    }
                    catch { }
                    continue;
                }

                KeyCode kc;
                if (Enum.TryParse<KeyCode>(s, true, out kc) && kc != KeyCode.None)
                {
                    try { if (Input.GetKeyDown(kc)) hit = true; } catch { }
                }
            }
            return hit;
        }

        /// <summary>把 "f4" / "F4," 这类写法归一成可比较的形式。</summary>
        private static string NormalizeKeys(string cfg)
        {
            if (string.IsNullOrWhiteSpace(cfg)) return "";
            string[] parts = cfg.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            var list = new System.Collections.Generic.List<string>();
            for (int i = 0; i < parts.Length; i++)
            {
                string s = parts[i].Trim().ToUpperInvariant();
                if (s.Length > 0) list.Add(s);
            }
            list.Sort(StringComparer.Ordinal);
            return string.Join(",", list.ToArray());
        }

        /// <summary>打开时定位置：没拖过就居中，拖过就回原位（分辨率变了再夹一次）。</summary>
        private void PlaceWindow()
        {
            float px = Plugin.PanelX.Value, py = Plugin.PanelY.Value;
            if (px < 0f || py < 0f) CenterWindow();
            else _panelPos = new Vector2(px, py);
            ClampWindow();
        }

        private void CenterWindow()
        {
            _panelPos = new Vector2(
                Mathf.Max(12f, (Screen.width - PanelW * _scale) * 0.5f),
                Mathf.Max(12f, (Screen.height - TotalH * _scale) * 0.5f));
        }

        /// <summary>不让窗口拖到完全看不见：至少留 80px 宽、标题栏 26px 在屏内。</summary>
        private void ClampWindow()
        {
            float w = PanelW * _scale;
            float h = TotalH * _scale;
            _panelPos.x = Mathf.Clamp(_panelPos.x, 80f - w, Screen.width - 80f);
            _panelPos.y = Mathf.Clamp(_panelPos.y, 0f, Mathf.Max(0f, Screen.height - Mathf.Min(h, 40f)));
        }

        private void SaveWindow()
        {
            try
            {
                Plugin.PanelX.Value = _panelPos.x;
                Plugin.PanelY.Value = _panelPos.y;
                Plugin.PanelScale.Value = _scale;
                Plugin.PanelCollapsed.Value = _collapsed;
                Plugin.Instance.Config.Save();
            }
            catch (Exception e)
            {
                Log.LogWarning(Tag + " 保存窗口位置失败：" + e.Message);
            }
        }

        private void ZoomBy(float delta)
        {
            float old = _scale;
            _scale = Mathf.Clamp(_scale + delta, ScaleMin, ScaleMax);
            if (Mathf.Abs(old - _scale) < 0.0005f) return;
            ClampWindow();
            SaveWindow();
        }

        private bool HasNeteaseLogin()
        {
            try { return Client.Netease.HasLogin; }
            catch { return false; }
        }

        private bool HasQqLogin()
        {
            try { return Client.QQ.HasLogin; }
            catch { return false; }
        }

        private void RefreshLoginLine()
        {
            try
            {
                _loginLine = Client.LoginLine(_platform);
            }
            catch (Exception e)
            {
                _loginLine = "登录态读取失败：" + e.Message;
            }
        }

        /// <summary>当前音源是否已登录（QQ 看 qqmusic_key，网易看 MUSIC_U）。</summary>
        private bool IsLoggedIn
        {
            get { return _platform == Platform.QQ ? Client.QQ.HasLogin : Client.Netease.HasLogin; }
        }

        /// <summary>退出当前音源的登录态：清掉本机 cookie 并刷新页脚文案。</summary>
        private void LogoutCurrent()
        {
            if (_platform == Platform.QQ)
            {
                Client.QQ.Logout();
                Log.LogInfo(Tag + " 已退出 QQ 音乐登录");
            }
            else
            {
                Client.Netease.Logout();
                Log.LogInfo(Tag + " 已退出网易云登录");
            }
            RefreshLoginLine();
            _loginMsg = "已退出登录";
        }

        // ───────────────────────────── 扫码登录 ─────────────────────────────

        private void StartLogin()
        {
            _loginOpen = true;
            _loginState = 1;
            _isQqLogin = _platform == Platform.QQ;
            _loginMsg = _isQqLogin ? "正在向 QQ 音乐要二维码…" : "正在向网易云要二维码…";
            _qrText = "";
            _qrKey = "";
            _qrCookie = "";
            _qrError = "";

            if (_qrTex != null) { try { Destroy(_qrTex); } catch { } _qrTex = null; }

            Log.LogInfo(Tag + " 扫码登录：" + (_isQqLogin ? "QQ" : "网易云") + " 开始取二维码");

            _qrTaskKind = 1;
            _qrTask = Task.Run(() =>
            {
                try
                {
                    if (_isQqLogin)
                    {
                        // QQ：拿到的是 PNG 字节，直接转 Texture2D。
                        // r43：默认走「QQ 音乐 App 扫码」（授权码 + MQTT 推送），
                        // 面板可切回手机 QQ 扫码（ptlogin OAuth 老路）兜底。
                        byte[] png = _qqAppScan
                            ? Client.QQ.AppQrBeginAsync().GetAwaiter().GetResult()
                            : Client.QQ.QrBeginAsync().GetAwaiter().GetResult();
                        _qrPng = png;
                    }
                    else
                    {
                        _qrKey = Client.Netease.QrBeginAsync().GetAwaiter().GetResult();
                    }
                }
                catch (Exception e) { _qrError = e.Message; }
            });
        }

        private void CloseLogin()
        {
            if (!_loginOpen) return;
            _loginOpen = false;
            _qrPng = null;
            if (_qrTex != null) { try { Destroy(_qrTex); } catch { } _qrTex = null; }
            // r43：关卡片时停掉 App 扫码的后台 MQTT 会话（否则它一直挂着等扫码）
            if (_isQqLogin) Client.QQ.AppQrStop();
        }

        private void StartPoll()
        {
            string key = _qrKey;
            if (_isQqLogin)
            {
                // QQ：轮询不需要 key，qrsig 已存在 client 内部
            }
            else if (string.IsNullOrEmpty(key)) return;

            _nextPollAt = Time.unscaledTime + 2f;   // 2 秒一次，别把风控惹毛
            _qrTaskKind = 2;
            _qrTask = Task.Run(() =>
            {
                try
                {
                    if (_isQqLogin)
                    {
                        // r43：App 扫码的登录状态由后台 MQTT 会话推进，这里只读快照（毫秒级）；
                        // 手机 QQ 扫码仍走 ptlogin 轮询。
                        QqLoginResult r = _qqAppScan
                            ? Client.QQ.AppQrPoll()
                            : Client.QQ.QrPollAsync().GetAwaiter().GetResult();
                        _qqResult = r.State;
                        _qrCookie = r.CookieText ?? "";
                        if (!string.IsNullOrEmpty(r.RawMessage)) _qrError = r.RawMessage;
                    }
                    else
                    {
                        QrLoginResult r = Client.Netease.QrPollAsync(key).GetAwaiter().GetResult();
                        _qrResult = r.State;
                        _qrCookie = r.CookieText ?? "";
                        if (!string.IsNullOrEmpty(r.RawMessage)) _qrError = r.RawMessage;
                    }
                }
                catch (Exception e)
                {
                    _qrResult = QrState.Unknown;
                    _qqResult = QqLoginResult.QqLoginState.Unknown;
                    _qrError = e.Message;
                }
            });
        }

        /// <summary>
        /// 登录轮询。放在 Update 里而不是协程：协程会因为切场景/暂停停摆，
        /// 而扫码这件事用户可能切出去玩手机，回来时不应该看到"卡在已扫码"。
        /// </summary>
        private void PollLogin()
        {
            if (!_loginOpen) return;

            if (_qrTask != null && _qrTask.IsCompleted)
            {
                Task t = _qrTask;
                _qrTask = null;
                try { t.GetAwaiter().GetResult(); } catch { }

                if (_qrTaskKind == 1)
                {
                    if (_isQqLogin)
                    {
                        if (_qrPng == null || _qrPng.Length == 0)
                        {
                            _loginState = 7;
                            _loginMsg = "拿不到二维码：" + (_qrError ?? "服务端没返回图片");
                            Log.LogWarning(Tag + " 取 QQ 二维码失败：" + _loginMsg);
                        }
                        else
                        {
                            _qrTex = PngToTexture(_qrPng);
                            _loginState = 2;
                            _loginMsg = "用手机 QQ 扫这上面的码（或在浏览器打开 QQ 音乐登录）";
                            _nextPollAt = Time.unscaledTime + 1.5f;
                            Log.LogInfo(Tag + " QQ 二维码已生成：" + _qrPng.Length + " 字节，" +
                                        (_qrTex != null ? _qrTex.width + "px" : "纹理生成失败"));
                        }
                    }
                    else if (string.IsNullOrEmpty(_qrKey))
                    {
                        _loginState = 7;
                        _loginMsg = "拿不到二维码：" + (_qrError ?? "服务端没返回 unikey");
                        Log.LogWarning(Tag + " 取 unikey 失败：" + _loginMsg);
                    }
                    else
                    {
                        _qrText = LoginUrlPrefix + _qrKey;
                        _qrTex = BuildQrTexture(_qrText);
                        _loginState = 2;
                        _loginMsg = "用手机网易云 App 扫这上面的码";
                        _nextPollAt = Time.unscaledTime + 1.5f;
                        Log.LogInfo(Tag + " 二维码已生成：" + _qrText.Length + " 字节，" +
                                    (_qrTex != null ? _qrTex.width + "px" : "纹理生成失败") +
                                    "｜手机打不开 App 时可在浏览器打开：" + _qrText);
                    }
                }
                else if (_qrTaskKind == 2)
                {
                    ApplyQrResult();
                }
            }

            if ((_loginState == 2 || _loginState == 3) && _qrTask == null && Time.unscaledTime >= _nextPollAt)
                StartPoll();

            if (_loginState == 4 && _qrTask == null && Time.unscaledTime - _loginDoneAt > 1.8f)
                CloseLogin();
        }

        private void ApplyQrResult()
        {
            if (_isQqLogin)
            {
                ApplyQqResult();
                return;
            }

            switch (_qrResult)
            {
                case QrState.Waiting:
                    _loginState = 2;
                    _loginMsg = "等手机扫码…";
                    break;

                case QrState.Scanned:
                    _loginState = 3;
                    _loginMsg = "已扫码，请在手机上点「确认登录」";
                    break;

                case QrState.Success:
                    if (!string.IsNullOrEmpty(_qrCookie)) Client.Netease.SetCookie(_qrCookie);
                    RefreshLoginLine();
                    _loginState = 4;
                    _loginMsg = "登录成功 · " + _loginLine;
                    _loginDoneAt = Time.unscaledTime;
                    Log.LogInfo(Tag + " 网易云登录成功，cookie=" + CookieStore.Mask(Client.Netease.CookieText));
                    if (!Client.Netease.HasLogin)
                    {
                        // r41 兜底：803 成功却没 MUSIC_U（网络/风控丢包）——不能默默装作登录了
                        _loginMsg = "扫码成功但没拿到 MUSIC_U 登录 cookie —— 用「从剪贴板导入」补登录态";
                        Log.LogWarning(Tag + " 扫码 803 成功但 cookie 缺 MUSIC_U：qr=" + CookieStore.Mask(_qrCookie ?? "") +
                                       " 现=" + CookieStore.Mask(Client.Netease.CookieText));
                    }
                    break;

                case QrState.Expired:
                    _loginState = 5;
                    _loginMsg = "二维码过期了，点「换一张」";
                    break;

                case QrState.RiskBlocked:
                    _loginState = 6;
                    _loginMsg = "网易风控拒绝了这次扫码（8821），换码重扫没用 —— 用「从剪贴板导入」";
                    Log.LogWarning(Tag + " 扫码被风控（8821）：" + _qrError);
                    break;

                default:
                    // 网络抖一下不该把用户卡死在"失败"页：留在待扫状态，下轮继续
                    _loginMsg = "轮询异常，继续重试：" + (_qrError ?? "未知");
                    break;
            }
        }

        private void ApplyQqResult()
        {
            switch (_qqResult)
            {
                case QqLoginResult.QqLoginState.Waiting:
                    _loginState = 2;
                    _loginMsg = "等手机扫码…";
                    break;

                case QqLoginResult.QqLoginState.Scanned:
                    _loginState = 3;
                    _loginMsg = "已扫码，请在手机上确认登录";
                    break;

                case QqLoginResult.QqLoginState.Success:
                    if (!string.IsNullOrEmpty(_qrCookie)) Client.QQ.SetCookie(_qrCookie);
                    RefreshLoginLine();
                    _loginState = 4;
                    _loginMsg = "登录成功 · " + _loginLine;
                    _loginDoneAt = Time.unscaledTime;
                    Log.LogInfo(Tag + " QQ 登录成功，cookie=" + QqCookieStore.Mask(Client.QQ.CookieText));
                    break;

                case QqLoginResult.QqLoginState.Expired:
                    _loginState = 5;
                    _loginMsg = "二维码过期了，点「换一张」";
                    break;

                case QqLoginResult.QqLoginState.Failed:
                    _loginState = 7;
                    _loginMsg = "登录失败：" + (_qrError ?? "未知");
                    Log.LogWarning(Tag + " QQ 轮询失败：" + (_qrError ?? "未知"));
                    break;

                default:
                    _loginMsg = "轮询异常，继续重试：" + (_qrError ?? "未知");
                    break;
            }
        }

        /// <summary>
        /// 兜底：扫码被风控时的唯一活路——从浏览器搬 cookie。
        /// 用户复制 "MUSIC_U=xxx; __csrf=yyy" 到剪贴板后点一下按钮即可。
        /// </summary>
        private void ImportCookieFromClipboard()
        {
            string clip = "";
            try { clip = GUIUtility.systemCopyBuffer; } catch { }

            if (_isQqLogin)
            {
                // QQ 的登录态关键字段是 qqmusic_key / musickey（不是 MUSIC_U）
                if (string.IsNullOrEmpty(clip) ||
                    (clip.IndexOf("qqmusic_key", StringComparison.OrdinalIgnoreCase) < 0 &&
                     clip.IndexOf("musickey", StringComparison.OrdinalIgnoreCase) < 0 &&
                     clip.IndexOf("qm_keyst", StringComparison.OrdinalIgnoreCase) < 0))
                {
                    _loginState = 7;
                    _loginMsg = "剪贴板里没有 qqmusic_key / musickey。浏览器登录 y.qq.com → F12 → Application → Cookies → 复制 qqmusic_key 的值";
                    return;
                }

                try
                {
                    Client.QQ.SetCookie(clip.Trim());
                    RefreshLoginLine();
                    bool ok = Client.QQ.HasLogin;
                    _loginState = ok ? 4 : 7;
                    _loginMsg = ok ? "登录态已导入 · " + _loginLine : "解析后仍然没有 qqmusic_key，检查格式";
                    _loginDoneAt = Time.unscaledTime;
                    Log.LogInfo(Tag + " 从剪贴板导入 QQ 登录态：" + (ok ? "成功" : "失败"));
                }
                catch (Exception e)
                {
                    _loginState = 7;
                    _loginMsg = "导入失败：" + e.Message;
                }
                return;
            }

            if (string.IsNullOrEmpty(clip) || clip.IndexOf("MUSIC_U", StringComparison.OrdinalIgnoreCase) < 0)
            {
                _loginState = 7;
                _loginMsg = "剪贴板里没有 MUSIC_U。浏览器登录 music.163.com → F12 → Application → Cookies → 复制 MUSIC_U 的值";
                return;
            }

            try
            {
                Client.Netease.SetCookie(clip.Trim());
                RefreshLoginLine();
                bool ok = Client.Netease.HasLogin;
                _loginState = ok ? 4 : 7;
                _loginMsg = ok ? "登录态已导入 · " + _loginLine : "解析后仍然没有 MUSIC_U，检查格式";
                _loginDoneAt = Time.unscaledTime;
                Log.LogInfo(Tag + " 从剪贴板导入登录态：" + (ok ? "成功" : "失败"));
            }
            catch (Exception e)
            {
                _loginState = 7;
                _loginMsg = "导入失败：" + e.Message;
            }
        }

        /// <summary>
        /// 把二维码矩阵烤成贴图。每个模块 5px、四周留 3 个模块的白边（quiet zone）——
        /// 白边不够手机扫不出来，这是扫码失败最常见的原因，别省。
        /// </summary>
        private Texture2D BuildQrTexture(string text)
        {
            try
            {
                QrCode qr = QrEncoder.Encode(text);
                const int scale = 5;
                const int quiet = 3;
                int px = (qr.Size + quiet * 2) * scale;

                var tex = new Texture2D(px, px, TextureFormat.RGBA32, false);
                tex.filterMode = FilterMode.Point;      // 硬边，缩放后不糊
                tex.wrapMode = TextureWrapMode.Clamp;
                tex.hideFlags = HideFlags.DontSave;

                var buf = new Color32[px * px];
                Color32 white = new Color32(255, 255, 255, 255);
                Color32 black = new Color32(12, 12, 14, 255);
                for (int i = 0; i < buf.Length; i++) buf[i] = white;

                for (int row = 0; row < qr.Size; row++)
                {
                    // 纹理原点在左下，矩阵第 0 行在顶部，所以要翻转行
                    int topY = px - 1 - (row + quiet) * scale;
                    for (int col = 0; col < qr.Size; col++)
                    {
                        if (!qr.Dark(col, row)) continue;
                        int baseX = (col + quiet) * scale;
                        for (int dy = 0; dy < scale; dy++)
                        {
                            int py = topY - dy;
                            if (py < 0 || py >= px) continue;
                            int off = py * px + baseX;
                            for (int dx = 0; dx < scale; dx++) buf[off + dx] = black;
                        }
                    }
                }

                tex.SetPixels32(buf);
                tex.Apply(false, false);
                Log.LogInfo(Tag + " 二维码纹理 " + px + "px（V" + qr.Version + "，" + qr.Size + " 模块，掩码 " + qr.Mask + "）");
                return tex;
            }
            catch (Exception e)
            {
                Log.LogError(Tag + " 生成二维码失败：" + e.Message);
                return null;
            }
        }

        /// <summary>把 PNG 字节解码成 Texture2D（QQ 扫码登录的二维码是服务端直接返回的图片）。</summary>
        private static Texture2D PngToTexture(byte[] png)
        {
            try
            {
                PngDecoder.Image img = PngDecoder.Decode(png);
                if (img == null || img.Pixels == null) return null;

                var tex = new Texture2D(img.Width, img.Height, TextureFormat.RGBA32, false);
                tex.hideFlags = HideFlags.DontSave;
                tex.filterMode = FilterMode.Point;
                tex.wrapMode = TextureWrapMode.Clamp;

                var colors = new Color32[img.Width * img.Height];
                for (int i = 0; i < colors.Length; i++)
                {
                    int p = i * 4;
                    colors[i] = new Color32(img.Pixels[p], img.Pixels[p + 1], img.Pixels[p + 2], img.Pixels[p + 3]);
                }

                tex.SetPixels32(colors);
                tex.Apply(false, false);
                return tex;
            }
            catch (Exception e)
            {
                Log.LogError(Tag + " 解码二维码 PNG 失败：" + e.Message);
                return null;
            }
        }

        // ───────────────────────────── 输入 ─────────────────────────────

        private bool Pressed(KeyCode legacy, Key modern)
        {
            try
            {
                var kb = Keyboard.current;
                if (kb != null)
                {
                    var c = kb[modern];
                    if (c != null && c.wasPressedThisFrame) return true;
                    return false;
                }
            }
            catch { }

            try { return Input.GetKeyDown(legacy); }
            catch { return false; }
        }

        private void UpdateMouse()
        {
            _mouseValid = false;
            _clicked = false;
            _held = false;
            _pendingScroll = 0;
            try
            {
                var m = Mouse.current;
                if (m != null)
                {
                    Vector2 p = m.position.ReadValue();
                    _mousePos = new Vector2(p.x, Screen.height - p.y);   // GUI 坐标原点在左上
                    _clicked = m.leftButton.wasPressedThisFrame;
                    _held = m.leftButton.isPressed;
                    float wheel = m.scroll.ReadValue().y;
                    if (wheel > 0.01f) _pendingScroll = -1;
                    else if (wheel < -0.01f) _pendingScroll = 1;
                    _mouseValid = true;
                    ToLocal();
                    return;
                }
            }
            catch { }

            try
            {
                Vector3 p = Input.mousePosition;
                _mousePos = new Vector2(p.x, Screen.height - p.y);
                _clicked = Input.GetMouseButtonDown(0);
                _held = Input.GetMouseButton(0);
                _mouseValid = true;
            }
            catch { }
            ToLocal();
        }

        /// <summary>屏幕坐标 → 面板本地坐标（反算 GUI.matrix 的平移和缩放）。</summary>
        private void ToLocal()
        {
            float s = _scale < 0.01f ? 1f : _scale;
            _lm = new Vector2((_mousePos.x - _panelPos.x) / s, (_mousePos.y - _panelPos.y) / s);
        }

        private bool CtrlDown
        {
            get
            {
                try
                {
                    var kb = Keyboard.current;
                    return kb != null && kb.ctrlKey.isPressed;
                }
                catch { return false; }
            }
        }

        /// <summary>面板内控件命中（本地坐标）。</summary>
        private bool Hit(Rect r)
        {
            return _mouseValid && _clicked && r.Contains(_lm);
        }

        /// <summary>屏幕坐标命中（登录卡片那种独立居中层）。</summary>
        private bool HitScreen(Rect r)
        {
            return _mouseValid && _clicked && r.Contains(_mousePos);
        }

        private void HandleMouse()
        {
            if (!_mouseValid) return;

            // ── 拖动窗口：按住标题栏跟手走，松手才存盘 ──
            if (_dragging)
            {
                if (_held)
                {
                    _panelPos = _mousePos - _dragGrab;
                    ClampWindow();
                }
                else
                {
                    _dragging = false;
                    SaveWindow();
                }
                return;
            }

            // ── 右下角拖动缩放 ──
            if (_resizing)
            {
                if (_held)
                {
                    float d = (_mousePos.x - _resizeGrabPos.x) + (_mousePos.y - _resizeGrabPos.y);
                    _scale = Mathf.Clamp(_resizeBaseScale + d / 900f, ScaleMin, ScaleMax);
                    ClampWindow();
                }
                else
                {
                    _resizing = false;
                    SaveWindow();
                }
                return;
            }

            // 登录层在最上层：开着的时候把后面的控件全挡住，别让用户误点出歌
            if (_loginOpen)
            {
                if (!_clicked) return;
                if (HitScreen(_rLoginRefresh)) { StartLogin(); return; }
                if (HitScreen(_rLoginClip)) { ImportCookieFromClipboard(); return; }
                if (HitScreen(_rLoginClose)) { CloseLogin(); return; }
                if (_isQqLogin && HitScreen(_rLoginMode))
                {
                    // r43：换扫码方式 = 停掉旧会话 + 换一条路重新取二维码
                    Client.QQ.AppQrStop();
                    _qqAppScan = !_qqAppScan;
                    _loginMsg = _qqAppScan ? "已切到 QQ 音乐 App 扫码" : "已切到手机 QQ 扫码";
                    StartLogin();
                    return;
                }
                if (HitScreen(_rLoginLogout) && IsLoggedIn) { LogoutCurrent(); return; }
                return;
            }

            if (!_clicked) return;

            // ── 标题栏按钮先处理（折叠状态也要能用）──
            if (Hit(_rClose)) { Toggle(); return; }
            if (Hit(_rCollapse))
            {
                _collapsed = !_collapsed;
                ComputeLayout();
                ClampWindow();
                SaveWindow();
                Log.LogInfo(Tag + (_collapsed ? " 面板已折叠。" : " 面板已展开。"));
                return;
            }
            if (Hit(_rZoomIn)) { ZoomBy(+0.08f); return; }
            if (Hit(_rZoomOut)) { ZoomBy(-0.08f); return; }
            if (Hit(_rGrip))
            {
                _dragging = true;
                _dragGrab = _mousePos - _panelPos;
                return;
            }

            if (_collapsed) return;

            if (Hit(_rResize))
            {
                _resizing = true;
                _resizeGrabPos = _mousePos;
                _resizeBaseScale = _scale;
                return;
            }

            // Ctrl+滚轮 = 缩放（普通滚轮还是留给列表翻页）
            if (_pendingScroll != 0 && CtrlDown)
            {
                ZoomBy(_pendingScroll < 0 ? +0.06f : -0.06f);
                return;
            }

            // 滚轮翻页：只有鼠标压在结果列表上才响应，免得跟游戏镜头缩放抢
            if (_pendingScroll != 0 && _rList.Contains(_lm))
            {
                int dir = _pendingScroll;
                _pendingScroll = 0;
                TurnPage(dir);
                return;
            }

            if (_player == null) return;

            if (_rSearch.Contains(_lm)) { StartTyping(); return; }
            if (Hit(_rSearchBtn)) { StartSearch(); return; }
            if (Hit(_rClear)) { _keyword = ""; return; }
            if (Hit(_rTheme)) { CycleTheme(); return; }
            if (Hit(_rLyricPos)) { ToggleLyric(); return; }
            if (Hit(_rLyricSize)) { CycleLyricSize(); return; }
            if (Hit(_rLyricDist)) { CycleLyricDistance(); return; }
            if (Hit(_rLyricEdge)) { CycleLyricEdge(); return; }
            if (Hit(_rLyricColor)) { CycleLyricColor(); return; }
            if (Hit(_rLyricHeight)) { CycleLyricHeight(); return; }

            for (int i = 0; i < Platforms.All.Length; i++)
            {
                if (Hit(_rNavItem[i])) { SwitchPlatform(Platforms.All[i]); return; }
            }

            // 导航底部：收藏 / 一起听队列入口
            if (Hit(_rFavNav)) { SwitchView(_view == 1 ? 0 : 1); return; }
            if (Hit(_rQueueNav)) { SwitchView(_view == 2 ? 0 : 2); return; }

            for (int i = 0; i < Presets.Length; i++)
            {
                string label = TagLabel(i);
                if (label == null) break;
                if (Hit(_rPreset[i])) { _keyword = label; StartSearch(); return; }
            }

            List<SongInfo> list = ActiveSongs;

            // 列表行右侧的小按钮：普通视图是收藏（♥），队列视图是移除（✕）。
            // 必须先于整行判定，否则 RowRect 会把按钮区域整个吃掉（r22 修的坑，别退回）。
            for (int i = 0; i < PageSize; i++)
            {
                int idx = _page + i;
                if (idx >= list.Count) break;
                if (Hit(_rFavBtn[i]))
                {
                    if (_view == 2)
                    {
                        // 队列视图 ✕：房主把这首移出队列（不播）并广播新快照
                        var sync = MusicSync.Instance;
                        if (sync != null && sync.IsHost)
                        {
                            string name = list[idx].Name;
                            sync.RemoveQueueItem(idx);
                            sync.BroadcastQueueState();
                            _status = "已移出队列：" + name;
                            Log.LogInfo(Tag + " " + _status);
                        }
                        return;
                    }
                    ToggleFavorite(list[idx]);
                    return;
                }
            }

            for (int i = 0; i < PageSize; i++)
            {
                int idx = _page + i;
                if (idx >= list.Count) break;
                if (Hit(RowRect(i)))
                {
                    var sync = MusicSync.Instance;

                    // 一起听队列视图：房主点一行 = 批准播放（出队 + 广播）；客机只看不点
                    if (_view == 2)
                    {
                        if (sync != null && sync.IsHost)
                        {
                            // 上一个请求还在飞就别急着出队，否则会出现「移出了却没播」
                            if (_busy) { _status = "上一个请求还没回来，等一下再批准"; return; }
                            SongInfo pick = list[idx];
                            string pickUrl = sync.QueueUrlAt(idx);   // 点歌人自己取好的直链（r40）
                            sync.RemoveQueueItem(idx);
                            sync.BroadcastQueueState();
                            _status = "已批准播放：" + pick.Name;
                            Log.LogInfo(Tag + " 批准点歌：" + pick.Name + " - " + pick.Artists +
                                (string.IsNullOrEmpty(pickUrl) ? "〔无点歌人直链，房主自取〕" : "〔用点歌人直链〕"));
                            if (!string.IsNullOrEmpty(pickUrl))
                                PlaySongWithUrl(pick, pickUrl);
                            else
                            {
                                _queueFallback = true;   // r41：失败提示要说清「点歌人和房主都没权限」
                                PlaySong(pick);   // 回退：房主账号取流（老行为）
                            }
                        }
                        else
                        {
                            _status = "等房主批准播放";
                        }
                        return;
                    }

                    // 一起听：客机在房间里点歌 = 点歌进房主队列，而不是自己放。
                    // r40：先用「客机自己」的账号取好播放直链，连歌一起发给房主——
                    // 这样 VIP 歌用点歌人自己的会员权限取流，房主没会员也能放。
                    if (sync != null && sync.InRoom && !sync.IsHost)
                    {
                        string me = PhotonNetwork.NickName;
                        QueueSong(list[idx], string.IsNullOrEmpty(me) ? "我" : me);
                        return;
                    }
                    PlaySong(list[idx]);
                    return;
                }
            }

            if (Hit(_rPrev)) { TurnPage(-1); return; }
            if (Hit(_rNext)) { TurnPage(+1); return; }

            if (Hit(_rPause)) { TogglePause(); return; }
            if (Hit(_rStop)) { StopPlayback(); return; }
            if (Hit(_rVolDown)) { _player.SetVolume(_player.Volume - 0.1f); return; }
            if (Hit(_rVolUp)) { _player.SetVolume(_player.Volume + 0.1f); return; }
        }

        // ───────────────────────────── 翻页 ─────────────────────────────

        private int PageCount
        {
            get { return Mathf.Max(1, Mathf.CeilToInt(ActiveSongs.Count / (float)PageSize)); }
        }

        private int PageNo
        {
            get { return _page / PageSize + 1; }
        }

        private bool CanPrev { get { return _page > 0; } }
        private bool CanNext { get { return _page + PageSize < ActiveSongs.Count; } }

        private int MaxPageStart
        {
            get
            {
                int n = ActiveSongs.Count;
                if (n <= PageSize) return 0;
                return ((n - 1) / PageSize) * PageSize;
            }
        }

        /// <summary>
        /// dir = +1 下一页 / -1 上一页。
        /// 到边界时必须改状态行说一句话——原来是点了完全没反应，看不出是bug还是到底了。
        /// </summary>
        private void TurnPage(int dir)
        {
            if (ActiveSongs.Count == 0) { _status = "还没有结果，先搜索"; return; }

            int before = _page;
            if (dir > 0) _page = Mathf.Min(_page + PageSize, MaxPageStart);
            else _page = Mathf.Max(0, _page - PageSize);

            // 本地翻到底但服务器可能还有：续拉一批（只在搜索视图，回来后 PollTask 会自动翻到新页）
            if (dir > 0 && _page == before && _view == 0 && _page + PageSize >= _songs.Count && _hasMore)
            {
                StartLoadMore();
                return;
            }

            if (_page == before)
            {
                _status = dir > 0
                    ? "已经是最后一页（第 " + PageNo + "/" + PageCount + " 页）"
                    : "已经是第一页";
                return;
            }

            _status = "第 " + PageNo + "/" + PageCount + " 页（共 " + ActiveSongs.Count + " 首）";
            Log.LogInfo(Tag + " 翻页 → 第 " + PageNo + "/" + PageCount + " 页，起始下标 " + _page);
        }


        private void HandleKeyboard()
        {
            if (Pressed(KeyCode.Escape, Key.Escape))
            {
                if (_loginOpen) { CloseLogin(); return; }
                if (_typing) { StopTyping(); return; }
                return;   // 没在打字就不抢 Esc，留给游戏自己的菜单
            }

            if (_loginOpen) return;   // 登录层开着时不吃 T / 退格，免得改到后面的搜索框

            if (!_typing)
            {
                // 面板底部一直写着"按 T 输入"，之前根本没实现。补上。
                if (Pressed(KeyCode.T, Key.T)) StartTyping();
                return;
            }

            if (Pressed(KeyCode.Backspace, Key.Backspace) && _keyword.Length > 0)
                _keyword = _keyword.Substring(0, _keyword.Length - 1);

            if (Pressed(KeyCode.Return, Key.Enter) || Pressed(KeyCode.KeypadEnter, Key.NumpadEnter))
                StartSearch();
        }

        private void StartTyping()
        {
            if (_typing) return;
            _typing = true;
            _status = "输入中：打字 → 回车搜索 / Esc 结束输入";

            // 开启 IME：否则 Windows 输入法（微软拼音等）的候选字没法上屏，
            // 中文永远打不进搜索框 —— 这是「打不了中文」的头号原因。
            try { Input.imeCompositionMode = IMECompositionMode.On; } catch { }

            SubscribeTextInput();
        }

        private void StopTyping()
        {
            if (!_typing) return;
            _typing = false;

            // 关掉 IME，把键盘交还给游戏（否则游戏里按方向键之类可能先被输入法吃掉）
            try { Input.imeCompositionMode = IMECompositionMode.Auto; } catch { }

            UnsubscribeTextInput();
            _status = "已结束输入";
        }

        private void SubscribeTextInput()
        {
            try
            {
                var kb = Keyboard.current;
                if (kb == null) return;
                if (_textKb != null && !ReferenceEquals(_textKb, kb)) _textKb.onTextInput -= OnTextInput;
                _textKb = kb;
                kb.onTextInput -= OnTextInput;
                kb.onTextInput += OnTextInput;
            }
            catch (Exception e)
            {
                Log.LogWarning(Tag + " 订阅文本输入失败，中文可能打不进：" + e.GetType().Name);
            }
        }

        private void UnsubscribeTextInput()
        {
            try { if (_textKb != null) _textKb.onTextInput -= OnTextInput; } catch { }
            _textKb = null;
        }

        /// <summary>只收可见字符。退格/回车在 HandleKeyboard 里按按键处理，避免删两次。</summary>
        private void OnTextInput(char c)
        {
            if (!_open || !_typing) return;
            if (c < 32 || c == 127) return;
            if (_keyword.Length >= 40) return;
            _keyword += c;
        }

        // ───────────────────────────── 搜索 / 取地址 ─────────────────────────────

        private void StartSearch()
        {
            if (_busy) { _status = "上一个请求还没回来，等一下"; return; }
            string kw = _keyword.Trim();
            if (kw.Length == 0) { _status = "先输入歌名（点搜索框或按 T，也可点下面的预设词）"; return; }

            LibraryStore.AddHistory(kw);   // 记进搜索历史，下次点标签直接搜
            _libDirty = true;
            if (_view != 0) { _view = 0; _page = 0; }   // 从收藏/历史点搜，切回搜索结果视图

            _busy = true;
            _taskKind = 1;
            _taskError = null;
            _pendingSongs = null;
            _kw = kw;
            _offSearch = 0;
            _hasMore = true;
            _page = 0;                       // 换关键词一定回到第一页

            // r37：按平台各搜各的——选中哪家就只搜哪家，平台真实返回多少就显示多少
            //（r36 及之前是「聚合搜索」：两家都搜、同名去重合并，导致两个标签页结果一模一样）
            Platform plat = _platform;
            _status = "搜索中：" + Platforms.DisplayName(plat) + " · " + kw;
            Log.LogInfo(Tag + " 搜索[" + Platforms.DisplayName(plat) + "] " + kw);

            _task = Task.Run(() =>
            {
                try
                {
                    List<SongInfo> r = Client.SearchAsync(plat, kw, FetchLimit, 0).GetAwaiter().GetResult();
                    _pendingSongs = r;
                    _offSearch = r != null ? r.Count : 0;
                }
                catch (Exception e)
                {
                    _taskError = e.GetType().Name + ": " + e.Message;
                }
            });
        }

        /// <summary>本地列表翻到底后，向当前平台续拉一批拼进现有结果。</summary>
        private void StartLoadMore()
        {
            if (_busy) { _status = "正在加载更多…"; return; }
            if (!_hasMore) { _status = "没有更多了（共 " + _songs.Count + " 首）"; return; }

            _busy = true;
            _taskKind = 3;
            _taskError = null;
            _pendingSongs = null;
            _status = "加载更多中…（已 " + _songs.Count + " 首）";

            string kw = _kw;
            int off = _offSearch;
            Platform plat = _platform;
            Log.LogInfo(Tag + " 续拉[" + Platforms.DisplayName(plat) + "] " + kw + " off=" + off);

            _task = Task.Run(() =>
            {
                try
                {
                    List<SongInfo> r = Client.SearchAsync(plat, kw, MoreBatch, off).GetAwaiter().GetResult();
                    _pendingSongs = r;
                    _offSearch += r != null ? r.Count : 0;
                }
                catch (Exception e)
                {
                    _taskError = e.GetType().Name + ": " + e.Message;
                }
            });
        }

        private void PlaySong(SongInfo song)
        {
            if (_busy) { _status = "上一个请求还没回来，等一下"; return; }
            _busy = true;
            _taskKind = 2;
            _taskError = null;
            _pendingUrl = null;
            _pendingSong = song;
            _status = "取播放地址：" + song.Name;
            Log.LogInfo(Tag + " 取播放地址[" + Platforms.DisplayName(song.Platform) + "] " +
                        song.Key + " " + song.Name + " - " + song.Artists);

            // 先把上一首的歌词撤掉，否则取地址那一两秒里画面上还挂着旧词，看着像串了
            LyricOverlay ov = LyricOverlay.Instance;
            if (ov != null) ov.Clear();

            SongInfo target = song;
            _task = Task.Run(() =>
            {
                try
                {
                    // 按 song.Platform 分发，不看当前选中平台 —— 列表里可能留着切平台之前搜到的歌
                    _pendingUrl = Client.GetUrlAsync(target).GetAwaiter().GetResult();
                }
                catch (Exception e)
                {
                    _taskError = e.GetType().Name + ": " + e.Message;
                }
            });
        }

        /// <summary>
        /// 客机点歌：先用「点歌人自己」的账号取播放直链，再连歌+直链一起发给房主（r40）。
        /// 取到流 = 点歌人有权限（VIP 歌用点歌人的会员）；取不到也会发（无直链），
        /// 房主批准时回退用房主账号取流，两边都没权限才真正播不了。
        /// </summary>
        private void QueueSong(SongInfo song, string requester)
        {
            if (_busy) { _status = "上一个请求还没回来，等一下"; return; }
            _busy = true;
            _taskKind = 4;
            _taskError = null;
            _pendingUrl = null;
            _pendingSong = song;
            _pendingRequester = requester;
            _status = "点歌中（取直链）：" + song.Name;
            Log.LogInfo(Tag + " 客机点歌先取流[" + Platforms.DisplayName(song.Platform) + "] " +
                        song.Key + " " + song.Name + " - " + song.Artists);

            SongInfo target = song;
            _task = Task.Run(() =>
            {
                try
                {
                    _pendingUrl = Client.GetUrlAsync(target).GetAwaiter().GetResult();
                }
                catch (Exception e)
                {
                    _taskError = e.GetType().Name + ": " + e.Message;
                }
            });
        }

        /// <summary>
        /// 直接用「点歌人给的直链」播放 + 广播（房主批准队列 / 自动接播用，r40）。
        /// 不再走房主取流——谁点的歌用谁的权限，房主没会员也能放客机点的 VIP 歌。
        /// </summary>
        private void PlaySongWithUrl(SongInfo song, string url)
        {
            if (_player == null) return;
            _status = "开始播放：" + song.Name;
            Log.LogInfo(Tag + " 用点歌人直链播放：" + song.Name + " - " + song.Artists);
            _player.Play(song, url, 0f);
            StartLyricFetch(song);
            var sync = MusicSync.Instance;
            if (sync != null) sync.BroadcastPlay(song, url, 0f);
        }

        /// <summary>暂停/继续按钮：本地切换 + 房主广播给房间。</summary>
        private void TogglePause()
        {
            if (_player == null) return;
            _player.TogglePause();
            var sync = MusicSync.Instance;
            if (sync == null) return;
            // 切换后不在播 = 处于暂停态（有 clip 才算真暂停，否则是停止）
            if (_player.HasClip && !_player.IsPlaying) sync.BroadcastPause(true);
            else if (_player.IsPlaying) sync.BroadcastPause(false);
        }

        /// <summary>停止按钮：本地停止 + 房主广播。</summary>
        private void StopPlayback()
        {
            if (_player == null) return;
            _player.Stop();
            var sync = MusicSync.Instance;
            if (sync != null) sync.BroadcastStop();
        }

        /// <summary>取流失败的统一说法（r38/r41）：-110 按登录态给准确提示，别让已登录的人看到「没登录」。</summary>
        private string ExplainFetchFail(SongInfo song, SongUrlResult r)
        {
            if (r == null) return "没有返回";
            if (r.Code == -110)
            {
                bool loggedIn = song != null && song.Platform == Platform.QQ ? HasQqLogin() : HasNeteaseLogin();
                return loggedIn
                    ? "-110 无权限：已登录，但这首是会员曲/付费曲/无版权（当前账号没有这首歌的权限）"
                    : "-110 无权限：还没登录（扫码登录有权限的账号再试）";
            }
            return r.Explain();
        }

        private void PollTask()
        {
            if (_task == null || !_task.IsCompleted) return;

            Task t = _task;
            _task = null;
            try { t.GetAwaiter().GetResult(); }
            catch (Exception e) { if (_taskError == null) _taskError = e.GetType().Name + ": " + e.Message; }

            if (_taskError != null)
            {
                _status = "失败：" + _taskError;
                Log.LogError(Tag + " " + _status);
                _taskError = null;
                _busy = false;
                return;
            }

            if (_taskKind == 1)
            {
                _songs.Clear();
                if (_pendingSongs != null) _songs.AddRange(_pendingSongs);
                _page = 0;
                _hasMore = _pendingSongs != null && _pendingSongs.Count >= FetchLimit;
                _status = _songs.Count == 0
                    ? "没搜到（换个关键词，或者点预设词试试）"
                    : "搜到 " + _songs.Count + " 首 · 第 1/" + PageCount + " 页，点一行播放";
                Log.LogInfo(Tag + " 搜索结果 " + _songs.Count + " 首，共 " + PageCount + " 页");
            }
            else if (_taskKind == 3)
            {
                // 续拉一批：拼到列表尾部，同平台去重
                int added = 0;
                if (_pendingSongs != null)
                {
                    var seen = new HashSet<string>();
                    foreach (SongInfo s in _songs) seen.Add(s.Platform + ":" + s.Key);
                    foreach (SongInfo s in _pendingSongs)
                    {
                        if (seen.Add(s.Platform + ":" + s.Key)) { _songs.Add(s); added++; }
                    }
                }

                // 返回满一批就算还有更多；不满一批就到头了
                _hasMore = _pendingSongs != null && _pendingSongs.Count >= MoreBatch;

                if (added == 0 && !_hasMore)
                {
                    _status = "没有更多了（共 " + _songs.Count + " 首）";
                }
                else
                {
                    _status = "又加了 " + added + " 首 · 共 " + _songs.Count + " 首 · 第 " + PageNo + "/" + PageCount + " 页";
                    // 翻到新拼进来的那一页，让用户立刻看到后续内容
                    _page = Mathf.Min(_page + PageSize, MaxPageStart);
                }
                Log.LogInfo(Tag + " 续拉 +" + added + " 首，共 " + _songs.Count + " 首，hasMore=" + _hasMore);
            }
            else if (_taskKind == 2)
            {
                if (_pendingUrl != null && _pendingUrl.Ok && _player != null)
                {
                    _status = "开始播放：" + _pendingSong.Name;
                    _player.Play(_pendingSong, _pendingUrl.Url, 0f);
                    StartLyricFetch(_pendingSong);
                    // 房主广播「一起听」：让房间里其他人播同一首
                    var sync = MusicSync.Instance;
                    if (sync != null) sync.BroadcastPlay(_pendingSong, _pendingUrl.Url, 0f);
                }
                else
                {
                    // r38/r41：-110 按登录态给准确说法；队列回退自取时说清「两边都没权限」
                    string why = ExplainFetchFail(_pendingSong, _pendingUrl);
                    if (_queueFallback && _pendingUrl != null && _pendingUrl.Code == -110)
                        why = "这首歌是会员/付费曲：点歌人取不到直链，房主账号也没有权限（换一首，或让有会员的账号点歌）";
                    _status = "拿不到播放地址：" + why;
                    Log.LogWarning(Tag + " " + _status + " key=" + (_pendingSong != null ? _pendingSong.Key : "?"));
                }
            }
            else if (_taskKind == 4)
            {
                // 客机点歌：取到流就把「歌 + 点歌人直链」一起发给房主；取不到也发（无直链，房主回退自取）
                var sync = MusicSync.Instance;
                string url = (_pendingUrl != null && _pendingUrl.Ok) ? _pendingUrl.Url : "";
                if (sync != null && _pendingSong != null)
                {
                    sync.RequestQueue(_pendingSong, _pendingRequester ?? "我", url);
                }
                if (!string.IsNullOrEmpty(url))
                {
                    _status = "已点歌：" + _pendingSong.Name + "（等房主批准）";
                }
                else
                {
                    string why = ExplainFetchFail(_pendingSong, _pendingUrl);
                    _status = "已点歌：" + _pendingSong.Name + "（你这账号取不到流，" + why + "，房主会用他的账号再试）";
                    Log.LogWarning(Tag + " 客机点歌取流失败，发无直链入队：" + why + " key=" +
                                   (_pendingSong != null ? _pendingSong.Key : "?"));
                }
            }

            _taskKind = 0;
            _busy = false;
            _queueFallback = false;
        }

        // ───────────────────────────── 自动切歌 ─────────────────────────────

        /// <summary>
        /// 一首放完自动接列表下一首。检测"上一帧在播、这一帧停了"，
        /// 且播放进度接近结尾（排除手动暂停/停止导致的 isPlaying 变 false）。
        /// </summary>
        private void AutoAdvance()
        {
            MusicPlayer p = _player;
            if (p == null) { _wasPlaying = false; return; }

            // r35：客机不本地自动切歌——接播由房主广播驱动，否则一首放完两边各切各的直接失联
            var sync = MusicSync.Instance;
            if (sync != null && sync.InRoom && !sync.IsHost)
            {
                _wasPlaying = p.IsPlaying;
                return;
            }

            bool playing = p.IsPlaying;
            bool hasClip = p.HasClip;

            // 只在"确实在播"时记录标记，暂停/停止不算
            if (playing)
            {
                _wasPlaying = true;
                _playEndGrace = 0f;
                return;
            }

            if (!_wasPlaying)
            {
                // 没在播也不触发；但防抖计时照常递减
                if (_playEndGrace > 0f) _playEndGrace -= Time.unscaledDeltaTime;
                return;
            }

            // 从"在播"翻到"停"：判断是不是播到结尾
            bool reachedEnd = hasClip && p.Length > 1f && p.Time >= p.Length - 1.2f;
            if (!reachedEnd)
            {
                // 手动暂停/停止，不自动切
                _wasPlaying = false;
                _playEndGrace = 0f;
                return;
            }

            _wasPlaying = false;

            // 防抖：一次播完只切一次（流式下 length/time 可能有波动）
            if (_playEndGrace > 0f) return;
            _playEndGrace = 1.0f;

            PlayNextSong();
        }

        /// <summary>按当前列表顺序播下一首；到最后一首则回到第一首。</summary>
        private void PlayNextSong()
        {
            // r35：房主优先自动接播「一起听」队列——客机点的歌排上了就该轮到它
            var sync = MusicSync.Instance;
            if (sync != null && sync.IsHost && sync.Queue.Count > 0)
            {
                SongInfo queued = sync.Queue[0];
                string queuedUrl = sync.QueueUrlAt(0);   // 点歌人直链（r40）
                sync.RemoveQueueItem(0);
                sync.BroadcastQueueState();
                Log.LogInfo(Tag + " 自动接播队列：" + queued.Name + " - " + queued.Artists + "（剩余 " + sync.Queue.Count + " 首）" +
                    (string.IsNullOrEmpty(queuedUrl) ? "〔无点歌人直链〕" : "〔用点歌人直链〕"));
                if (!string.IsNullOrEmpty(queuedUrl))
                    PlaySongWithUrl(queued, queuedUrl);
                else
                {
                    _queueFallback = true;   // r41
                    PlaySong(queued);
                }
                return;
            }

            List<SongInfo> list = ActiveSongs;
            if (list.Count == 0) { _status = "列表空，无法自动切歌"; return; }
            if (_busy) { _playEndGrace = 1.5f; return; }   // 还在取地址就稍后重试

            MusicPlayer p = _player;
            SongInfo cur = p != null ? p.Current : null;
            if (cur == null) return;

            int idx = -1;
            for (int i = 0; i < list.Count; i++)
                if (list[i].Key == cur.Key) { idx = i; break; }

            int next = idx < 0 ? 0 : (idx + 1) % list.Count;
            SongInfo target = list[next];
            Log.LogInfo(Tag + " 自动切下一首：" + target.Name + " - " + target.Artists);
            PlaySong(target);
        }

        // ───────────────────────────── 收藏 / 历史 ─────────────────────────────

        /// <summary>队列视图的空表（MusicSync 未挂载时兜底，避免每次取数据都分配）。</summary>
        private static readonly List<SongInfo> _emptySongs = new List<SongInfo>();

        /// <summary>当前列表视图用哪一份数据。_view：0=搜索结果 1=收藏 2=一起听队列。</summary>
        private List<SongInfo> ActiveSongs
        {
            get
            {
                if (_view == 2)
                {
                    var sync = MusicSync.Instance;
                    return sync != null ? sync.Queue : _emptySongs;
                }
                return _view == 1 ? _favorites : _songs;
            }
        }

        private void SwitchView(int v)
        {
            if (_view == v) return;
            _view = v;
            _page = 0;
            RefreshLibrary();
            string name = v == 0 ? "搜索结果" : v == 2 ? "一起听队列" : "收藏";
            _status = "已切到「" + name + "」";
            Log.LogInfo(Tag + " 视图 → " + name);
        }

        private void RefreshLibrary()
        {
            if (!_libDirty) return;
            _libDirty = false;
            _favorites.Clear();
            _favorites.AddRange(LibraryStore.LoadFavorites());
            _history.Clear();
            _history.AddRange(LibraryStore.LoadHistory());
        }

        /// <summary>
        /// 预设词那行显示的标签：有搜索历史就显示历史关键词（最近 4 个，点击直接搜），
        /// 没有历史才退回默认预设词。返回 null 表示这一格没标签。
        /// </summary>
        private string TagLabel(int i)
        {
            if (i < 0 || i >= Presets.Length) return null;
            if (_history.Count > 0)
            {
                if (i >= _history.Count) return null;
                return _history[i];
            }
            return Presets[i];
        }

        private void ToggleFavorite(SongInfo s)
        {
            if (s == null) return;
            if (LibraryStore.IsFavorite(s.Key))
            {
                LibraryStore.RemoveFavorite(s.Key);
                _status = "已取消收藏：" + s.Name;
            }
            else
            {
                LibraryStore.AddFavorite(s);
                _status = "已收藏：" + s.Name;
            }
            _libDirty = true;
            RefreshLibrary();
            Log.LogInfo(Tag + " " + _status + "（收藏总数 " + _favorites.Count + "）");
        }


        // ───────────────────────────── 歌词（独立通道）─────────────────────────────

        private Task _lyricTask;
        private SongInfo _lyricSong;
        private LyricResult _lyricResult;

        /// <summary>
        /// 取歌词不占 <see cref="_busy"/>：它比取流慢、也不影响出声，
        /// 让它和"能不能点下一首"解耦，用户体感才不会卡。
        /// </summary>
        private void StartLyricFetch(SongInfo song)
        {
            if (song == null) return;
            if (_lyricTask != null) return;          // 上一个还在飞就先不排，歌词不怕晚一行

            _lyricSong = song;
            _lyricResult = null;

            var target = song;
            _lyricTask = Task.Run(() =>
            {
                try { _lyricResult = Client.GetLyricAsync(target).GetAwaiter().GetResult(); }
                catch (Exception e) { Log.LogWarning(Tag + " 取歌词失败：" + e.Message); }
            });
        }

        private void PollLyric()
        {
            if (_lyricTask == null || !_lyricTask.IsCompleted) return;

            Task t = _lyricTask;
            _lyricTask = null;

            SongInfo song = _lyricSong;
            LyricResult r = _lyricResult;
            _lyricSong = null;
            _lyricResult = null;

            if (song == null) return;

            LyricOverlay ov = LyricOverlay.Instance;
            if (ov == null) return;

            if (r == null || !r.HasLyric)
            {
                ov.SetLyrics(song, "");
                return;
            }

            ov.SetLyrics(song, r.Lrc, r.Translated);
        }

        // ───────────────────────────── 布局 ─────────────────────────────

        private Rect RowRect(int i)
        {
            Rect r = new Rect(_rList.x + 4f, _rList.y + 4f + i * RowH, _rList.width - 8f, RowH - 2f);
            _rFavBtn[i] = new Rect(r.xMax - 46f, r.y + (r.height - 32f) * 0.5f, 38f, 32f);
            return r;
        }

        /// <summary>
        /// 布局全用"面板本地坐标"：原点 (0,0) 是面板左上角，尺寸就是 PanelW × (GripH+PanelH)。
        /// 绘制时靠 GUI.matrix 一点平移一缩放，命中测试时用 _lm 反算 —— 改缩放不用动任何一个数字。
        /// 登录卡片是独立居中层，仍然是屏幕坐标，所以那几行照旧用 Screen。
        /// </summary>
        private void ComputeLayout()
        {
            float h = _collapsed ? GripH : GripH + PanelH;
            _rPanel = new Rect(0f, 0f, PanelW, h);

            // ── 标题栏：左边整条是拖动区，右边挂着 [−][+][▾][×] ──
            float bx = PanelW - 12f;
            bx -= 24f; _rClose = new Rect(bx, 4f, 24f, 18f);
            bx -= 28f; _rCollapse = new Rect(bx, 4f, 28f, 18f);
            bx -= 26f; _rZoomIn = new Rect(bx, 4f, 26f, 18f);
            bx -= 26f; _rZoomOut = new Rect(bx, 4f, 26f, 18f);
            _rGrip = new Rect(10f, 2f, bx - 18f, GripH - 4f);

            // 右下角缩放手柄（折叠时没有主体，也就不给柄）
            _rResize = _collapsed ? Rect.zero : new Rect(PanelW - 20f, h - 20f, 20f, 20f);

            // ── 登录层：屏幕居中卡片（画在 GUI.matrix 外面）──
            const float lw = 392f, lh = 520f;
            _rLoginCard = new Rect(Mathf.Max(12f, (Screen.width - lw) * 0.5f),
                                   Mathf.Max(12f, (Screen.height - lh) * 0.5f), lw, lh);
            _rLoginQr = new Rect(_rLoginCard.x + (lw - 216f) * 0.5f, _rLoginCard.y + 86f, 216f, 216f);

            float lbY = _rLoginCard.yMax - 92f;
            _rLoginRefresh = new Rect(_rLoginCard.x + 24f, lbY, 88f, 34f);
            _rLoginClip = new Rect(_rLoginRefresh.xMax + 8f, lbY, 156f, 34f);
            _rLoginClose = new Rect(_rLoginCard.xMax - 24f - 84f, lbY, 84f, 34f);
            // 退出登录：已登录时显示在按钮行下面一行（r26 放在卡片外了，用户根本看不见）
            _rLoginLogout = new Rect(_rLoginCard.x + 24f, lbY + 42f, 120f, 30f);
            // 扫码方式切换（r43）：QQ 音乐 App 扫码 ⇄ 手机 QQ 扫码，紧挨退出按钮
            _rLoginMode = new Rect(_rLoginCard.x + 152f, lbY + 42f, 168f, 30f);

            if (_collapsed) return;

            float top = GripH;   // 主体从标题栏下面开始

            // ── 左侧导航：音源列表（垂直），宽 NavW ──
            _rNav = new Rect(14f, top + 14f, NavW, PanelH - 28f);
            _rNavLine = new Rect(_rNav.xMax + 14f, top + 20f, 1f, PanelH - 40f);

            float ny = _rNav.y + 10f;
            for (int i = 0; i < Platforms.All.Length; i++)
            {
                _rNavItem[i] = new Rect(_rNav.x + 6f, ny, NavW - 12f, 46f);
                ny += 52f;
            }

            // 「收藏」视图入口
            _rFavNav = new Rect(_rNav.x + 6f, ny, NavW - 12f, 38f);
            // 「一起听」点歌队列入口（收藏正下方）
            _rQueueNav = new Rect(_rNav.x + 6f, ny + 44f, NavW - 12f, 38f);

            // 导航底部：歌词开关 + 字号 + 距离 + 左右 + 高度 + 配色（都是点一下换一个，改完立刻生效）
            _rLyricPos = new Rect(_rNav.x + 6f, _rNav.yMax - 52f, NavW - 12f, 42f);
            _rLyricSize = new Rect(_rNav.x + 6f, _rNav.yMax - 96f, NavW - 12f, 42f);
            _rLyricDist = new Rect(_rNav.x + 6f, _rNav.yMax - 140f, NavW - 12f, 42f);
            _rLyricEdge = new Rect(_rNav.x + 6f, _rNav.yMax - 184f, NavW - 12f, 42f);
            _rLyricHeight = new Rect(_rNav.x + 6f, _rNav.yMax - 228f, NavW - 12f, 42f);
            _rLyricColor = new Rect(_rNav.x + 6f, _rNav.yMax - 272f, NavW - 12f, 42f);

            // ── 主区从导航右侧开始 ──
            float px = _rNav.xMax + 28f;
            float inner = PanelW - 18f - px;

            // 头部 = 正在播放（大封面 + 曲名 + 歌手 + 徽标）
            float py = top + 14f;
            _rCover = new Rect(px, py, BigCover, BigCover);
            _rTitle = new Rect(px + 64f, py + 3f, inner - 64f - 210f, 24f);
            _rArtist = new Rect(px + 64f, py + 29f, inner - 64f - 210f, 18f);
            _rBadge = new Rect(PanelW - 18f - 64f, py + 5f, 64f, 22f);
            _rTheme = new Rect(_rBadge.x - 8f - 56f, py + 5f, 56f, 22f);
            py += 66f;

            _rHeaderLine = new Rect(px, py, inner, 1f);
            py += 12f;

            // ── 搜索 ──
            float btnW = 76f, clrW = 60f;
            _rSearch = new Rect(px, py, inner - btnW - clrW - 16f, 36f);
            _rSearchBtn = new Rect(_rSearch.xMax + 8f, py, btnW, 36f);
            _rClear = new Rect(_rSearchBtn.xMax + 8f, py, clrW, 36f);
            py += 48f;

            float cx = px;
            for (int i = 0; i < Presets.Length; i++)
            {
                string lbl = TagLabel(i);
                if (lbl == null) { _rPreset[i] = Rect.zero; continue; }
                float bw = 26f + lbl.Length * 15f;
                _rPreset[i] = new Rect(cx, py, bw, 26f);
                cx += bw + 8f;
            }
            py += 36f;

            _rStatus = new Rect(px, py, inner, 18f);
            py += 24f;

            // ── 结果列表 ──
            _rList = new Rect(px, py, inner, PageSize * RowH + 8f);
            py = _rList.yMax + 12f;

            _rPrev = new Rect(px, py, 76f, 28f);
            _rNext = new Rect(_rPrev.xMax + 8f, py, 76f, 28f);
            _rPageInfo = new Rect(_rNext.xMax + 12f, py, inner - 184f, 28f);
            py += 40f;

            // ── 底部播放条（手机 App 那种整条 nav） ──
            _rBar = new Rect(px, py, inner, 76f);
            _rProgress = new Rect(px + 16f, py + 14f, inner - 32f, 3f);
            _rTime = new Rect(px + 16f, py + 30f, 160f, 20f);

            float mid = px + inner * 0.5f;
            _rPause = new Rect(mid - 22f, py + 26f, 44f, 44f);
            _rStop = new Rect(mid - 22f - 12f - 72f, py + 33f, 72f, 30f);

            _rVolText = new Rect(px + inner - 16f - 56f, py + 33f, 56f, 20f);
            _rVolUp = new Rect(_rVolText.x - 8f - 32f, py + 33f, 32f, 30f);
            _rVolDown = new Rect(_rVolUp.x - 36f, py + 33f, 32f, 30f);
            py += 88f;

            _rHint = new Rect(px, py, inner, 16f);
        }

        // ───────────────────────────── 绘制 ─────────────────────────────

        // 贴图/样式一律运行时生成：不依赖游戏内置皮肤，也不往磁盘塞素材。
        // 圆角靠九宫格（GUIStyle.border + 圆角 SDF 贴图），这样任意尺寸都不糊。

        private GUIStyle RoundStyle(int radius, Color fill, Color border, float bw)
        {
            var s = new GUIStyle();
            s.normal.background = RoundTex(radius, fill, border, bw);
            int b = radius + 3;
            s.border = new RectOffset(b, b, b, b);
            return s;
        }

        private Texture2D RoundTex(int radius, Color fill, Color border, float bw)
        {
            int size = radius * 2 + 8;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            tex.hideFlags = HideFlags.DontSave;
            _texs.Add(tex);

            var px = new Color32[size * size];
            float half = size * 0.5f;
            float r = radius;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // 圆角矩形的有符号距离场：d<0 在内，|d| 越小越靠近边缘
                    float dx = Mathf.Abs(x + 0.5f - half) - (half - r);
                    float dy = Mathf.Abs(y + 0.5f - half) - (half - r);
                    float ox = Mathf.Max(dx, 0f), oy = Mathf.Max(dy, 0f);
                    float d = Mathf.Sqrt(ox * ox + oy * oy) + Mathf.Min(Mathf.Max(dx, dy), 0f) - r;

                    float cover = Mathf.Clamp01(0.5f - d);                      // 1px 抗锯齿
                    float t = bw <= 0.01f ? 1f : Mathf.Clamp01(-d / bw);        // 边缘渐变到填充色
                    Color c = Color.Lerp(border, fill, t);

                    px[y * size + x] = new Color32(
                        (byte)(Mathf.Clamp01(c.r) * 255f),
                        (byte)(Mathf.Clamp01(c.g) * 255f),
                        (byte)(Mathf.Clamp01(c.b) * 255f),
                        (byte)(Mathf.Clamp01(c.a) * cover * 255f));
                }
            }
            tex.SetPixels32(px);
            tex.Apply(false, false);
            return tex;
        }

        private Texture2D FlatTex(Color c)
        {
            var t = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            t.hideFlags = HideFlags.DontSave;
            t.SetPixel(0, 0, c);
            t.Apply(false, false);
            _texs.Add(t);
            return t;
        }

        private GUIStyle Text(int size, Color c, TextAnchor anchor, bool bold = false)
        {
            var s = new GUIStyle(GUI.skin.label);
            if (_font != null) s.font = _font;
            s.fontSize = size;
            s.alignment = anchor;
            s.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
            s.normal.textColor = c;
            s.richText = true;
            s.wordWrap = false;
            s.clipping = TextClipping.Clip;
            s.margin = new RectOffset(0, 0, 0, 0);
            s.padding = new RectOffset(0, 0, 0, 0);
            return s;
        }

        private void EnsureStyles()
        {
            if (_styleReady) return;
            _styleReady = true;
            try
            {
                // IMGUI 默认字体没有中文字形。优先 YaHei UI（游戏里 Prophecy 用的就是它），退到 YaHei / 黑体。
                if (_font == null)
                {
                    try
                    {
                        _font = Font.CreateDynamicFontFromOSFont(
                            new[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "Segoe UI" }, 16);
                    }
                    catch { }
                }

                Palette p = _pal;

                // 圆角底：语义角色直接映射到 GUI 元素，换主题不用改任何绘制代码
                _sShadow = RoundStyle(18, new Color(0f, 0f, 0f, 0.55f), new Color(0f, 0f, 0f, 0.55f), 0f);
                _sPanel = RoundStyle(16, Palette.A(p.Bg, 0.985f), Palette.A(p.Border, 0.85f), 1.2f);
                // 折叠态只剩一条 26px 的标题栏，16px 圆角的九宫格放不下（border 上下加起来 38），
                // 会被 Unity 压变形，所以细条单独用一个小圆角样式
                _sPanelThin = RoundStyle(8, Palette.A(p.Bg, 0.985f), Palette.A(p.Border, 0.85f), 1.2f);
                _sCard = RoundStyle(12, p.Input, Palette.A(p.Border, 0.55f), 1f);      // 结果列表容器
                _sBar = RoundStyle(14, p.Nav, Palette.A(p.Border, 0.55f), 1f);         // 底部播放条
                _sField = RoundStyle(10, p.Input, Palette.A(p.Border, 0.6f), 1f);
                _sFieldFocus = RoundStyle(10, p.Input, p.Accent, 1.6f);

                _sBtn = RoundStyle(9, p.Elem, Palette.A(p.Border, 0.7f), 1f);
                _sBtnHi = RoundStyle(9, p.ElemHover, p.Border, 1f);
                _sBtnOff = RoundStyle(9, Palette.Lerp(p.Bg, p.Elem, 0.55f), Palette.A(p.Border, 0.35f), 1f);
                _sPrimary = RoundStyle(9, p.Accent, Palette.A(Color.white, 0.20f), 1f);
                _sPrimaryHi = RoundStyle(9, p.AccentHover, Palette.A(Color.white, 0.30f), 1f);

                _sChip = RoundStyle(13, p.Elem, Palette.A(p.Border, 0.55f), 1f);
                _sChipHi = RoundStyle(13, p.ElemHover, p.Accent, 1f);

                _sRowHi = RoundStyle(9, p.ElemHover, new Color(0f, 0f, 0f, 0f), 0f);
                _sRowPlay = RoundStyle(9, Palette.Lerp(p.Elem, p.Accent, 0.20f), new Color(0f, 0f, 0f, 0f), 0f);
                _sBadge = RoundStyle(11, p.Elem, Palette.A(p.Border, 0.6f), 1f);
                _sCover = RoundStyle(10, p.ElemHover, Palette.A(p.Border, 0.7f), 1f);

                // 二维码必须贴在白底上：深色主题里直接放码会对比度不够，手机扫不出来
                _sLight = RoundStyle(12, new Color(1f, 1f, 1f, 1f), new Color(1f, 1f, 1f, 1f), 0f);

                _texDivider = FlatTex(Palette.A(p.Border, 0.55f));
                _texAccent = FlatTex(p.Accent);
                _texProgBg = FlatTex(Palette.A(p.Text, 0.10f));
                _texProgFill = FlatTex(p.Accent);
                _texCoverEmpty = FlatTex(Palette.A(p.Accent, 0.55f));

                // 音源导航前面的品牌色圆点：白色圆角贴图（半径=半宽），用 GUI.color 着色，
                // 这样一张贴图能画出所有平台的圆点，不用每个平台各生成一张。
                _texDot = RoundTex(5, Color.white, new Color(0f, 0f, 0f, 0f), 0f);

                Color dim = Palette.A(p.Text2, 0.95f);
                Color faint = Palette.A(p.Text2, 0.62f);

                _h1 = Text(16, p.Text, TextAnchor.MiddleLeft, true);
                _sub = Text(10, faint, TextAnchor.MiddleRight);
                _body = Text(14, p.Text, TextAnchor.MiddleLeft);
                _bodyHi = Text(14, p.Text, TextAnchor.MiddleLeft, true);
                _dim = Text(13, dim, TextAnchor.MiddleLeft);
                _placeholder = Text(13, faint, TextAnchor.MiddleLeft);
                _tiny = Text(11, faint, TextAnchor.MiddleLeft);
                _rowText = Text(14, p.Text, TextAnchor.MiddleLeft);
                _rowTextHi = Text(14, p.Text, TextAnchor.MiddleLeft, true);
                _artist = Text(12, dim, TextAnchor.MiddleRight);
                _dur = Text(12, faint, TextAnchor.MiddleRight);
                _statusOk = Text(12, dim, TextAnchor.MiddleLeft);
                _statusErr = Text(12, Palette.Lerp(p.Text, Palette.Hex(0xFF5A5A), 0.7f), TextAnchor.MiddleLeft);
                _stateRight = Text(12, dim, TextAnchor.MiddleRight);
                _timeText = Text(12, dim, TextAnchor.MiddleLeft);
                _barDim = Text(11, faint, TextAnchor.MiddleRight);
                _btnText = Text(13, p.Text, TextAnchor.MiddleCenter);
                _btnTextOff = Text(13, Palette.A(faint, 0.55f), TextAnchor.MiddleCenter);
                _btnTextPrimary = Text(13, Palette.Hex(0xFFFFFF), TextAnchor.MiddleCenter, true);
                _btnTextSmall = Text(11, p.Text, TextAnchor.MiddleCenter);
                _chipText = Text(12, Palette.Lerp(dim, p.Accent, 0.45f), TextAnchor.MiddleCenter);
                _badgeOk = Text(11, Palette.Hex(0x8FDCA8), TextAnchor.MiddleCenter);
                _badgeBusy = Text(11, Palette.Hex(0xF9C26A), TextAnchor.MiddleCenter);
                _hintCenter = Text(12, faint, TextAnchor.MiddleCenter);

                // 登录层
                _loginStatus = Text(12, dim, TextAnchor.UpperCenter);
                _loginStatus.wordWrap = true;
                _loginStatusHint = Text(11, faint, TextAnchor.MiddleCenter);
                _loginMask = FlatTex(new Color(0f, 0f, 0f, 0.66f));

                _accentHex = "#" + ColorUtility.ToHtmlStringRGB(p.Accent);
                _faintHex = "#" + ColorUtility.ToHtmlStringRGB(faint);
            }
            catch (Exception e)
            {
                Log.LogWarning(Tag + " 初始化界面样式失败：" + e.Message + "（会退回默认皮肤）");
            }
        }

        /// <summary>换主题：丢掉旧贴图（不丢就是显存泄漏），下次 OnGUI 重新生成。</summary>
        private void RebuildStyles()
        {
            for (int i = 0; i < _texs.Count; i++)
            {
                try { if (_texs[i] != null) Destroy(_texs[i]); } catch { }
            }
            _texs.Clear();
            _styleReady = false;
        }

        private void CycleTheme()
        {
            _themeIdx = (_themeIdx + 1) % Palettes.Count;
            _pal = Palettes.Get(_themeIdx);
            Plugin.Theme.Value = _themeIdx;
            RebuildStyles();
            _status = "配色切换：" + _pal.Name;
            Log.LogInfo(Tag + " 配色 → " + _pal.Name);
        }

        /// <summary>歌词开关：点一下开/关。改完立即生效，不用重启。</summary>
        private void ToggleLyric()
        {
            bool on = !Plugin.LyricEnabled.Value;
            Plugin.LyricEnabled.Value = on;
            _status = "歌词 " + (on ? "开" : "关");
            Log.LogInfo(Tag + " 歌词 → " + (on ? "开" : "关"));
        }

        /// <summary>取封面贴图；未就绪返回 null，同时会把下载排进队列。</summary>
        private Texture2D CoverOf(SongInfo s)
        {
            if (s == null || string.IsNullOrEmpty(s.AlbumPicUrl) || _covers == null) return null;
            return _covers.Get(s.AlbumPicUrl);
        }

        private void DrawCover(Rect r, SongInfo s)
        {
            Texture2D t = CoverOf(s);
            if (t != null)
            {
                GUI.DrawTexture(r, t, ScaleMode.ScaleAndCrop);
                return;
            }
            // 占位：和封面同样圆角的底块 + 一段强调色短线，看着不像"忘了画"
            GUI.Box(r, GUIContent.none, _sCover);
            GUI.DrawTexture(new Rect(r.x + r.width * 0.28f, r.yMax - 8f, r.width * 0.44f, 2f), _texCoverEmpty);
        }

        /// <summary>
        /// 左侧音源导航。每项 = 品牌色圆点 + 平台名，选中项整块高亮并加左侧竖条。
        /// 品牌色直接用 <see cref="Platforms.BrandRgb"/>，不在这里写死任何颜色 ——
        /// 加新平台时这段代码不用改。
        /// </summary>
        private void DrawNav()
        {
            GUI.Box(_rNav, GUIContent.none, _sCard);
            GUI.DrawTexture(_rNavLine, _texDivider);

            for (int i = 0; i < Platforms.All.Length; i++)
            {
                Platform pl = Platforms.All[i];
                Rect r = _rNavItem[i];
                bool on = pl == _platform;
                bool hover = _mouseValid && r.Contains(_lm);

                if (on) GUI.Box(r, GUIContent.none, _sRowPlay);
                else if (hover) GUI.Box(r, GUIContent.none, _sRowHi);

                if (on)
                {
                    Color old = GUI.color;
                    GUI.color = BrandColor(pl);
                    GUI.DrawTexture(new Rect(r.x + 2f, r.y + 8f, 2f, r.height - 16f), Texture2D.whiteTexture);
                    GUI.color = old;
                }

                // 品牌色圆点
                Color prev = GUI.color;
                GUI.color = BrandColor(pl);
                GUI.DrawTexture(new Rect(r.x + 14f, r.y + r.height * 0.5f - 5f, 10f, 10f), _texDot);
                GUI.color = prev;

                GUI.Label(new Rect(r.x + 32f, r.y + 5f, r.width - 38f, 20f),
                          Platforms.DisplayName(pl), on ? _rowTextHi : _rowText);

                string sub = pl == _platform ? "当前音源" : "点击切换";
                GUI.Label(new Rect(r.x + 32f, r.y + 24f, r.width - 38f, 16f), sub, _tiny);
            }

            // ── 「收藏」视图入口 ──
            DrawViewNav(_rFavNav, "♥ 收藏", _favorites.Count, _view == 1);
            var qsync = MusicSync.Instance;
            DrawViewNav(_rQueueNav, "♪ 一起听", qsync != null ? qsync.Queue.Count : 0, _view == 2);

            // ── 导航底部：歌词开关 / 字号 / 距离 / 左右 / 高度 / 配色。都是点一下换下一个，改完立刻生效 ──
            Rect lp = _rLyricPos;
            bool lpOn = Plugin.LyricEnabled.Value;
            if (_mouseValid && lp.Contains(_lm)) GUI.Box(lp, GUIContent.none, _sRowHi);

            GUI.Label(new Rect(lp.x + 8f, lp.y + 3f, lp.width - 10f, 16f),
                      "歌词 " + (lpOn ? "开" : "关"), lpOn ? _badgeOk : _dim);
            GUI.Label(new Rect(lp.x + 8f, lp.y + 21f, lp.width - 10f, 16f),
                      "世界空间 · 左右 3D", _tiny);

            Rect ls = _rLyricSize;
            if (_mouseValid && ls.Contains(_lm)) GUI.Box(ls, GUIContent.none, _sRowHi);

            GUI.Label(new Rect(ls.x + 8f, ls.y + 3f, ls.width - 10f, 16f),
                      "字号 " + Plugin.LyricSize.Value, _rowText);
            GUI.Label(new Rect(ls.x + 8f, ls.y + 21f, ls.width - 10f, 16f), "点击调整", _tiny);

            // 距离：文字平面离镜头的远近
            Rect ld = _rLyricDist;
            if (_mouseValid && ld.Contains(_lm)) GUI.Box(ld, GUIContent.none, _sRowHi);
            GUI.Label(new Rect(ld.x + 8f, ld.y + 3f, ld.width - 10f, 16f),
                      "距离 " + Plugin.LyricDistance.Value.ToString("0.#"), _rowText);
            GUI.Label(new Rect(ld.x + 8f, ld.y + 21f, ld.width - 10f, 16f), "点击调整", _tiny);

            // 左右：句子中心离屏幕边的远近（Edge）
            Rect le = _rLyricEdge;
            if (_mouseValid && le.Contains(_lm)) GUI.Box(le, GUIContent.none, _sRowHi);
            GUI.Label(new Rect(le.x + 8f, le.y + 3f, le.width - 10f, 16f),
                      "左右 " + (Plugin.LyricEdge.Value * 100f).ToString("0") + "%", _rowText);
            GUI.Label(new Rect(le.x + 8f, le.y + 21f, le.width - 10f, 16f),
                      "两侧 " + Plugin.SideName(Plugin.LyricSide.Value), _tiny);

            // 配色：一个实心色点当预览（用的是真正的歌词色），点一下切下一套
            Rect lc = _rLyricColor;
            if (_mouseValid && lc.Contains(_lm)) GUI.Box(lc, GUIContent.none, _sRowHi);

            Color dot = LyricColors.Lit;
            dot.a = 1f;
            Color keep = GUI.color;
            GUI.color = dot;
            GUI.DrawTexture(new Rect(lc.x + 9f, lc.y + 8f, 10f, 10f), _texDot);
            GUI.color = keep;

            GUI.Label(new Rect(lc.x + 25f, lc.y + 3f, lc.width - 28f, 16f),
                      LyricColors.CurrentName(), _rowText);
            GUI.Label(new Rect(lc.x + 9f, lc.y + 21f, lc.width - 10f, 16f), "点击换配色", _tiny);

            // 高度：句子中心在屏幕上的上下位置
            Rect lh = _rLyricHeight;
            if (_mouseValid && lh.Contains(_lm)) GUI.Box(lh, GUIContent.none, _sRowHi);
            GUI.Label(new Rect(lh.x + 8f, lh.y + 3f, lh.width - 10f, 16f),
                      "高度 " + (Plugin.LyricY.Value * 100f).ToString("0"), _rowText);
            GUI.Label(new Rect(lh.x + 8f, lh.y + 21f, lh.width - 10f, 16f), "点击调整", _tiny);
        }

        /// <summary>导航栏的「收藏 / 历史」入口：选中态高亮，右侧显示数量。</summary>
        private void DrawViewNav(Rect r, string label, int count, bool on)
        {
            bool hover = _mouseValid && r.Contains(_lm);
            if (on) GUI.Box(r, GUIContent.none, _sRowPlay);
            else if (hover) GUI.Box(r, GUIContent.none, _sRowHi);

            if (on)
            {
                Color old = GUI.color;
                GUI.color = _pal.Accent;
                GUI.DrawTexture(new Rect(r.x + 2f, r.y + 6f, 2f, r.height - 12f), Texture2D.whiteTexture);
                GUI.color = old;
            }

            GUI.Label(new Rect(r.x + 10f, r.y + 3f, r.width - 40f, 16f),
                      label, on ? _rowTextHi : _rowText);
            if (count > 0)
                GUI.Label(new Rect(r.xMax - 30f, r.y + 3f, 26f, 16f),
                          count.ToString(), _dim);
            else
                GUI.Label(new Rect(r.x + 10f, r.y + 21f, r.width - 40f, 14f),
                          on ? "当前视图" : "点击查看", _tiny);
        }

        /// <summary>歌词高度循环：40 → 44 → 50 → 56 → 62（屏幕百分比，越大越靠下）。</summary>
        private void CycleLyricHeight()
        {
            float[] steps = { 0.40f, 0.44f, 0.50f, 0.56f, 0.62f };
            float cur = Plugin.LyricY.Value;
            float next = steps[0];
            for (int i = 0; i < steps.Length; i++)
            {
                if (steps[i] > cur + 0.001f) { next = steps[i]; break; }
            }
            Plugin.LyricY.Value = next;
            _status = "歌词高度 → 屏幕 " + (next * 100f).ToString("0") + "% 处";
            Log.LogInfo(Tag + " 歌词高度 → " + (next * 100f).ToString("0") + "%");
        }

        /// <summary>歌词距离循环：3 → 4 → 6 → 8 → 10 → 12（文字平面离镜头的远近，越小越贴脸）。</summary>
        private void CycleLyricDistance()
        {
            float[] steps = { 3f, 4f, 6f, 8f, 10f, 12f };
            float cur = Plugin.LyricDistance.Value;
            float next = steps[0];
            for (int i = 0; i < steps.Length; i++)
            {
                if (steps[i] > cur + 0.001f) { next = steps[i]; break; }
            }
            Plugin.LyricDistance.Value = next;
            _status = "歌词距离 → " + next.ToString("0") + " 格（越小越贴脸）";
            Log.LogInfo(Tag + " 歌词距离 → " + next.ToString("0"));
        }

        /// <summary>歌词左右位置循环：18% → 25% → 30% → 35% → 40%（句子中心离屏幕边缘的比例，越小越靠边）。</summary>
        private void CycleLyricEdge()
        {
            float[] steps = { 0.18f, 0.25f, 0.30f, 0.35f, 0.40f };
            float cur = Plugin.LyricEdge.Value;
            float next = steps[0];
            for (int i = 0; i < steps.Length; i++)
            {
                if (steps[i] > cur + 0.001f) { next = steps[i]; break; }
            }
            Plugin.LyricEdge.Value = next;
            _status = "歌词左右 → 屏幕 " + (next * 100f).ToString("0") + "% 处（两侧对称）";
            Log.LogInfo(Tag + " 歌词左右 → " + (next * 100f).ToString("0") + "%");
        }

        /// <summary>歌词字号循环：40 → 52 → 62 → 74 → 86 → 40。改 cfg 立即生效，不用重启。</summary>
        private void CycleLyricSize()
        {
            int[] steps = { 40, 48, 56, 64, 72, 84 };
            int cur = Plugin.LyricSize.Value;
            int next = steps[0];
            for (int i = 0; i < steps.Length; i++)
            {
                if (steps[i] > cur) { next = steps[i]; break; }
            }
            Plugin.LyricSize.Value = next;
            _status = "歌词字号 → " + next + "px（也可以直接改 cfg 的 [歌词] Size）";
            Log.LogInfo(Tag + " 歌词字号 → " + next + "px");
        }


        /// <summary>歌词配色循环。写的是 cfg 里的三个十六进制项，歌词层下一帧就换色，不用重启。</summary>
        private void CycleLyricColor()
        {
            int idx = LyricColors.Cycle();
            _status = "歌词颜色 → " + LyricColors.Presets[idx].Name + "（想自定义就改 cfg 的 [歌词] ColorLit）";
            Log.LogInfo(Tag + " 歌词配色 → " + LyricColors.Presets[idx].Name);
        }

        private static Color BrandColor(Platform p)
        {
            float r, g, b;
            Platforms.BrandColor(p, out r, out g, out b);
            return new Color(r, g, b, 1f);
        }

        private void SwitchPlatform(Platform p)
        {
            // 点当前平台 = 打开登录卡片（里面能退出登录 / 重扫，r26 前已登录时完全没有入口）
            if (_platform == p)
            {
                StartLogin();
                return;
            }

            _platform = p;
            Plugin.SourcePlatform.Value = Platforms.ToInt(p);
            RefreshLoginLine();

            // 切平台清掉上一家的结果：混着显示会让人误点，也不知道点了会去哪个源取流
            _songs.Clear();
            _page = 0;
            _view = 0;   // 回到搜索结果视图

            _status = "已切到 " + Platforms.DisplayName(p) + " · " + _loginLine;
            Log.LogInfo(Tag + " 切换音源 → " + Platforms.DisplayName(p));

            // 切到没登录的平台，顺手弹登录卡片（用户主动切平台 = 想用这个源，别让他卡在"点不动"）
            bool needLogin = p == Platform.Netease ? !HasNeteaseLogin() : !HasQqLogin();
            if (needLogin) StartLogin();
        }

        private void OnGUI()
        {
            if (!_open) return;
            ComputeLayout();
            EnsureStyles();

            MusicPlayer p = _player;
            bool has = p != null && p.HasClip;
            SongInfo cur = has ? p.Current : null;

            // 整块面板走一次矩阵变换：平移到 _panelPos，再按 _scale 缩放。
            // 子控件全按"本地坐标"画，所以拖动/缩放一个布局数字都不用改。
            Matrix4x4 prevMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(new Vector3(_panelPos.x, _panelPos.y, 0f),
                                       Quaternion.identity,
                                       new Vector3(_scale, _scale, 1f));

            // 外投影让面板从游戏画面里"浮"出来（折叠成一条时不画，18px 圆角的影子套在 26px 条上会变形）
            if (!_collapsed)
            {
                GUI.Box(new Rect(_rPanel.x - 8f, _rPanel.y - 8f, _rPanel.width + 16f, _rPanel.height + 16f),
                        GUIContent.none, _sShadow);
            }
            GUI.Box(_rPanel, GUIContent.none, _collapsed ? _sPanelThin : _sPanel);

            DrawChrome(cur);

            if (!_collapsed)
            {
            // ── 左侧音源导航 ──
            DrawNav();

            // ── 头部：正在播放 ──
            DrawCover(_rCover, cur);
            GUI.Label(_rTitle, cur != null ? cur.Name : "未播放", cur != null ? _h1 : _dim);

            string artistLine;
            if (cur == null) artistLine = "搜一首歌，点一行开始放";
            else artistLine = cur.Artists + (string.IsNullOrEmpty(cur.Album) ? "" : "　·　" + cur.Album);
            GUI.Label(_rArtist, artistLine, _tiny);

            GUI.Box(_rBadge, GUIContent.none, _sBadge);
            GUI.Label(_rBadge, _busy ? "处理中" : "就绪", _busy ? _badgeBusy : _badgeOk);
            Button(_rTheme, _pal.Name, true, false, _btnTextSmall);

            GUI.DrawTexture(_rHeaderLine, _texDivider);

            // ── 搜索 ──
            GUI.Box(_rSearch, GUIContent.none, _typing ? _sFieldFocus : _sField);
            if (string.IsNullOrEmpty(_keyword))
            {
                GUI.Label(new Rect(_rSearch.x + 13f, _rSearch.y, _rSearch.width - 26f, _rSearch.height),
                    _typing ? "" : "搜歌名 / 歌手　　点这里或按 T 输入", _placeholder);
            }
            else
            {
                GUI.Label(new Rect(_rSearch.x + 13f, _rSearch.y, _rSearch.width - 26f, _rSearch.height),
                    _keyword, _body);
            }
            if (_typing && ((int)(UnityEngine.Time.unscaledTime * 2f) % 2 == 0))
            {
                float w = MeasureKeyword(_keyword);
                GUI.DrawTexture(new Rect(_rSearch.x + 13f + w + 1f, _rSearch.y + 10f, 1.5f, 16f), _texAccent);
            }

            Button(_rSearchBtn, "搜索", true, true);
            Button(_rClear, "清空");

            // r39：必须用 TagLabel（和点击同一份数据）——之前这里画的是固定 Presets[i]，
            // 点击走的却是 _history[i]，显示「江南」搜出来的是历史里的「365里路」，记忆全乱。
            for (int i = 0; i < Presets.Length; i++)
            {
                string chipLabel = TagLabel(i);
                if (chipLabel == null) continue;
                Chip(_rPreset[i], chipLabel);
            }

            bool err = _status.StartsWith("失败") || _status.StartsWith("拿不到") || _status.StartsWith("加载失败");
            GUI.Label(_rStatus, _status, err ? _statusErr : _statusOk);

            // ── 结果列表 ──
            GUI.Box(_rList, GUIContent.none, _sCard);
            List<SongInfo> viewList = ActiveSongs;
            if (viewList.Count == 0)
            {
                string empty;
                if (_view == 1) empty = "还没有收藏　·　点列表行右侧的 ♥ 收藏喜欢的歌";
                else if (_view == 2) empty = "队列空　·　客机点歌后会排到这里，点一行批准播放";
                else empty = _busy ? "正在搜索…" : "还没有结果　·　上行输入歌名，或点一个预设词";
                GUI.Label(new Rect(_rList.x, _rList.y + _rList.height * 0.5f - 10f, _rList.width, 20f),
                    empty, _hintCenter);
            }
            else
            {
                for (int i = 0; i < PageSize; i++)
                {
                    int idx = _page + i;
                    if (idx >= viewList.Count) break;

                    SongInfo s = viewList[idx];
                    Rect r = RowRect(i);
                    bool hover = _mouseValid && r.Contains(_lm);
                    bool playing = has && cur != null && cur.Key == s.Key;   // 按 Key 比，跨平台 ID 会撞
                    bool fav = LibraryStore.IsFavorite(s.Key);

                    if (playing) GUI.Box(r, GUIContent.none, _sRowPlay);
                    else if (hover) GUI.Box(r, GUIContent.none, _sRowHi);

                    if (playing || hover)
                        GUI.DrawTexture(new Rect(r.x + 2f, r.y + 10f, 2f, r.height - 20f), _texAccent);

                    DrawCover(new Rect(r.x + 10f, r.y + (r.height - CoverSize) * 0.5f, CoverSize, CoverSize), s);

                    // 来源平台小点（收藏视图里混着两家，用品牌色区分；r37 起搜索列表只有一家）
                    Color keepDot = GUI.color;
                    GUI.color = BrandColor(s.Platform);
                    GUI.DrawTexture(new Rect(r.x + 46f, r.y + r.height * 0.5f - 3f, 6f, 6f), _texDot);
                    GUI.color = keepDot;

                    GUI.Label(new Rect(r.x + 58f, r.y, r.width - 58f - 226f, r.height),
                        s.Name, (playing || hover) ? _rowTextHi : _rowText);

                    // 队列视图把歌手列让给点歌人（谁点的比谁唱的更需要一眼看到）
                    string artistText = s.Artists;
                    if (_view == 2)
                    {
                        var qy = MusicSync.Instance;
                        string who = qy != null ? qy.RequesterAt(idx) : "";
                        if (!string.IsNullOrEmpty(who)) artistText = "点歌：" + who;
                    }
                    GUI.Label(new Rect(r.xMax - 214f, r.y, 108f, r.height), artistText, _artist);

                    string tail = "<color=" + _faintHex + ">" + s.DurationText + "</color>" + FeeTag(s);
                    GUI.Label(new Rect(r.xMax - 104f, r.y, 72f, r.height), tail, _dur);

                    // 行尾按钮：普通视图收藏（♥，已收藏实心红色）；队列视图移除（✕）
                    bool favHover = _mouseValid && _rFavBtn[i].Contains(_lm);
                    if (favHover || fav)
                        GUI.Box(_rFavBtn[i], GUIContent.none, _sRowHi);
                    if (_view == 2)
                    {
                        Color keep2 = GUI.color;
                        GUI.color = favHover ? _pal.Text : _pal.Text2;
                        GUI.Label(_rFavBtn[i], "✕", _tiny);
                        GUI.color = keep2;
                    }
                    else
                    {
                        GUIStyle favStyle = fav ? _chipText : _tiny;
                        Color keep = GUI.color;
                        GUI.color = fav ? new Color(0.93f, 0.25f, 0.25f, 1f)
                                        : (favHover ? _pal.Text : _pal.Text2);
                        GUI.Label(_rFavBtn[i], fav ? "♥" : "♡", favStyle);
                        GUI.color = keep;
                    }
                }
            }

            // ── 翻页 ──
            Button(_rPrev, "上一页", CanPrev);
            Button(_rNext, "下一页", CanNext);
            if (viewList.Count > 0)
            {
                GUI.Label(new Rect(_rPageInfo.x, _rPageInfo.y, _rPageInfo.width, _rPageInfo.height),
                    "第 " + PageNo + " / " + PageCount + " 页　·　共 " + viewList.Count + " 首　·　列表内滚轮翻页", _dim);
            }

            // ── 底部播放条 ──
            GUI.Box(_rBar, GUIContent.none, _sBar);

            GUI.DrawTexture(_rProgress, _texProgBg);
            if (has && p.Length > 0.01f)
            {
                float prog = Mathf.Clamp01(p.Time / p.Length);
                if (prog > 0.002f)
                    GUI.DrawTexture(new Rect(_rProgress.x, _rProgress.y, _rProgress.width * prog, _rProgress.height),
                        _texProgFill);
            }

            GUI.Label(_rTime, has ? Mmss(p.Time) + "　/　" + Mmss(p.Length) : "0:00　/　0:00", _timeText);

            string state;
            if (p == null) state = "空闲";
            else if (p.Loading) state = "缓冲中";
            else if (has && p.IsPlaying) state = "播放中";
            else if (has) state = "已暂停";
            else state = "空闲";
            GUI.Label(new Rect(_rTime.xMax, _rTime.y, 58f, _rTime.height),
                "<color=" + _accentHex + ">●</color> " + state, _timeText);

            Button(_rPause, p != null && p.IsPlaying ? "暂停" : "播放", true, true);
            Button(_rStop, "停止");

            GUI.Label(_rVolText, Mathf.RoundToInt((p != null ? p.Volume : 0f) * 100f) + "%", _barDim);
            Button(_rVolDown, "−");
            Button(_rVolUp, "+");

            // ── 页脚 ──
            string syncInfo = "";
            var sync = MusicSync.Instance;
            if (sync != null && sync.InRoom)
            {
                syncInfo = sync.IsHost
                    ? "　·　一起听：房主 · 听众 " + sync.ListenerCount + " 人"
                    : "　·　一起听：跟随房主";
                if (sync.Queue.Count > 0) syncInfo += " · 队列 " + sync.Queue.Count + " 首";
            }
            GUI.Label(_rHint, _loginLine + "　·　" + PanelKeyName + " 面板　·　歌词自动显示" +
                              (Plugin.LyricEnabled.Value ? "(开)" : "(关)") +
                              "　·　T 输入　·　回车搜索　·　拖标题栏移动" + syncInfo, _tiny);

            }   // ← !_collapsed

            GUI.matrix = prevMatrix;

            // 登录层是屏幕居中的独立层：必须在矩阵外画，否则会被面板的平移/缩放拖着走
            if (_loginOpen)
            {
                Vector2 saveLm = _lm;
                _lm = _mousePos;          // 登录层用屏幕坐标，hover 也就跟着用屏幕坐标
                DrawLogin();
                _lm = saveLm;
            }
        }

        /// <summary>标题栏：拖动区 + 右侧 [−][+][▾][×]。折叠状态下也照画，只是主体没了。</summary>
        private void DrawChrome(SongInfo cur)
        {
            // 提示文字：拖动 / 缩放比例
            string tip = _collapsed
                ? "PeakMusic　" + (cur != null ? cur.Name : "未播放")
                : "PeakMusic　·　按住这里拖动　·　Ctrl+滚轮缩放　" + Mathf.RoundToInt(_scale * 100f) + "%";
            GUI.Label(new Rect(_rGrip.x + 6f, _rGrip.y, _rGrip.width - 8f, _rGrip.height), tip, _tiny);

            GUI.DrawTexture(new Rect(0f, GripH - 1f, _rPanel.width, 1f), _texDivider);

            Button(_rZoomOut, "−", _scale > ScaleMin + 0.001f, false, _btnTextSmall);
            Button(_rZoomIn, "+", _scale < ScaleMax - 0.001f, false, _btnTextSmall);
            Button(_rCollapse, _collapsed ? "▸" : "▾", true, false, _btnTextSmall);
            Button(_rClose, "×", true, false, _btnTextSmall);

            // 右下角缩放手柄：三条小折线暗示"可拖"
            if (!_collapsed)
            {
                for (int i = 0; i < 3; i++)
                {
                    float o = 4f + i * 5f;
                    GUI.DrawTexture(new Rect(_rResize.xMax - 2f - o, _rResize.yMax - 3f, o, 1f), _texDivider);
                    GUI.DrawTexture(new Rect(_rResize.xMax - 3f, _rResize.yMax - 2f - o, 1f, o), _texDivider);
                }
            }
        }

        /// <summary>
        /// 扫码登录层：遮罩 + 居中卡片 + 白底二维码 + 一行状态 + 三个按钮。
        /// 二维码必须衬白底、留 quiet zone，深色主题下直接把码画在深色面板上手机是扫不出来的。
        /// </summary>
        private void DrawLogin()
        {
            GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), _loginMask);

            Rect c = _rLoginCard;
            GUI.Box(new Rect(c.x - 8f, c.y - 8f, c.width + 16f, c.height + 16f), GUIContent.none, _sShadow);
            GUI.Box(c, GUIContent.none, _sPanel);

            GUI.Label(new Rect(c.x + 24f, c.y + 16f, c.width - 48f, 26f),
                _isQqLogin ? "QQ音乐扫码登录" : "网易云扫码登录", _h1);
            GUI.Label(new Rect(c.x + 24f, c.y + 46f, c.width - 48f, 18f),
                _isQqLogin ? "手机 QQ 扫一扫 → 手机上点确认登录" : "手机网易云 App → 右上角「扫一扫」→ 手机上点确认", _tiny);

            GUI.Box(_rLoginQr, GUIContent.none, _sLight);
            if (_qrTex != null)
            {
                GUI.DrawTexture(new Rect(_rLoginQr.x + 10f, _rLoginQr.y + 10f,
                                         _rLoginQr.width - 20f, _rLoginQr.height - 20f),
                                _qrTex, ScaleMode.ScaleToFit);
            }
            else
            {
                GUI.Label(_rLoginQr, _loginState == 1 ? "生成中…" : "二维码没生成出来", _hintCenter);
            }

            string shown = _loginMsg ?? "";
            if (_loginState == 6 || _loginState == 7) shown = "<color=#FF7A7A>" + shown + "</color>";
            else if (_loginState == 4) shown = "<color=#8FDCA8>" + shown + "</color>";
            GUI.Label(new Rect(c.x + 24f, _rLoginQr.yMax + 12f, c.width - 48f, 44f), shown, _loginStatus);

            GUI.Label(new Rect(c.x + 24f, _rLoginQr.yMax + 60f, c.width - 48f, 34f),
                _isQqLogin
                    ? "扫不出来：浏览器登录 y.qq.com → F12 → Application → Cookies，\n复制 qqmusic_key 后点下面「从剪贴板导入」"
                    : "扫不出来 / 8821 风控：浏览器登录 music.163.com → F12 → Application → Cookies，\n复制 MUSIC_U 后点下面「从剪贴板导入」", _loginStatusHint);

            Button(_rLoginRefresh, "换一张", _loginState != 1);
            Button(_rLoginClip, "从剪贴板导入");
            Button(_rLoginClose, "关闭");

            // 已登录时给个退出入口（否则网易云一直显示已登录、永远弹不出二维码）
            if (IsLoggedIn)
            {
                Button(_rLoginLogout, "退出登录");
            }

            // 扫码方式切换（只在 QQ 卡片给）：点一下换二维码生成方式
            if (_isQqLogin)
            {
                Button(_rLoginMode, _qqAppScan ? "改用手机QQ扫码" : "改用QQ音乐App扫码");
            }
        }

        /// <summary>估算已输入文本宽度，用来放光标。中文按 14px、ASCII 按 7.5px，够用。</summary>
        private static float MeasureKeyword(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0f;
            float w = 0f;
            for (int i = 0; i < s.Length; i++)
                w += s[i] < 128 ? 7.5f : 14f;
            return w;
        }

        private void Button(Rect r, string text, bool enabled = true, bool primary = false, GUIStyle labelStyle = null)
        {
            bool hover = enabled && _mouseValid && r.Contains(_lm);
            GUIStyle bg = !enabled ? _sBtnOff
                         : primary ? (hover ? _sPrimaryHi : _sPrimary)
                                   : (hover ? _sBtnHi : _sBtn);
            GUI.Box(r, GUIContent.none, bg);
            GUIStyle tx = labelStyle != null ? labelStyle
                         : !enabled ? _btnTextOff
                         : primary ? _btnTextPrimary
                                   : _btnText;
            GUI.Label(r, text, tx);
        }

        private void Chip(Rect r, string text)
        {
            bool hover = _mouseValid && r.Contains(_lm);
            GUI.Box(r, GUIContent.none, hover ? _sChipHi : _sChip);
            GUI.Label(r, text, _chipText);
        }

        private static string Mmss(float sec)
        {
            if (sec < 0f) sec = 0f;
            int t = Mathf.RoundToInt(sec);
            return (t / 60) + ":" + (t % 60).ToString("00");
        }

        private static string FeeTag(SongInfo s)
        {
            if (s.PrivilegeSt < 0) return " <color=#4B5163>无版权</color>";
            if (s.Fee == 1)
            {
                // 网易叫 VIP，QQ 叫「会员」——同一件事，用各自用户熟悉的说法
                return s.Platform == Platform.QQ
                    ? " <color=#31C27C>会员</color>"
                    : " <color=#EC4141>VIP</color>";
            }
            if (s.Fee == 4) return " <color=#F9C26A>付费</color>";
            return "";
        }

        // ───────────────────────────── 清理 ─────────────────────────────

        private void OnDestroy()
        {
            UnsubscribeTextInput();
            // 打字中途插件被卸载时，把 IME 还给游戏，别让它卡在组合模式
            try { Input.imeCompositionMode = IMECompositionMode.Auto; } catch { }
            try { if (_client != null) _client.Dispose(); } catch { }
            _client = null;

            // 「一起听」解绑房主指令
            var sync = MusicSync.Instance;
            if (sync != null)
            {
                sync.OnRemotePlay -= HandleRemotePlay;
                sync.OnRemotePause -= HandleRemotePause;
                sync.OnRemoteStop -= HandleRemoteStop;
                sync.OnRemoteSeek -= HandleRemoteSeek;
                sync.OnQueueAdd -= HandleQueueAdd;
                sync.OnQueueState -= HandleQueueState;
            }

            // 运行时生成的贴图必须自己回收，否则每次开关插件都漏一点显存
            for (int i = 0; i < _texs.Count; i++)
            {
                try { if (_texs[i] != null) Destroy(_texs[i]); } catch { }
            }
            _texs.Clear();

            Log.LogInfo(Tag + " 面板已销毁。");
        }
    }
}
