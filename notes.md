# 阶段 1 实测记录（生死闸门）

> 这张表的每一格都要有实测数字。空着的格子就是没过闸门。
> 记录时间：2026-11-07　机器：Windows 11　游戏：`D:\Steam\steamapps\common\PEAK`

## 环境基线（已确认）

| 项目 | 值 |
|---|---|
| PEAK 版本形态 | Mono（`PEAK_Data\Managed\Assembly-CSharp.dll` 存在，无 `GameAssembly.dll`） |
| Unity 版本 | **6000.3.15**（日志：`Running under Unity v6000.3.15.12692100`） |
| BepInEx | 5.4.23.3，`winhttp.dll` + `doorstop_config.ini` 就位 |
| 已装插件 | 59 个（PEAKLib 多版本共存；`PEAKERRpcInfo` 每次启动对 DLSS 原生 dll 报 BadImageFormatException，属噪音） |
| 游戏自带 AudioClip | 插件 `Start()` 时 **379 个**；进主菜单按 F7 时 **660 个**（判"涨没涨"必须用同一时刻前后比） |

## 起服务（每次测试前都要开着）

```powershell
& "C:\Users\Admin\.workbuddy\binaries\python\versions\3.13.12\python.exe" `
  "C:\Users\Admin\WorkBuddy\2026-11-07-20-32-32\PeakMusic\testmedia\serve.py" 8765
```

`serve.py` 支持 Range（`python -m http.server` 不支持，测出来的行为是假的）。
素材是 `testmedia\test-song.mp3`（3.69 MB，ID3 正常；从 `D:\QQ\5_6294056628467543235.mp3` 复制的副本，可随时删）。

## 探针操作

启动游戏 → 主菜单按 **F7** 起播 → 依次 F8 / F9 / F10 / F11 → 退出游戏。
全程看 `D:\Steam\steamapps\common\PEAK\BepInEx\LogOutput.log` 里 `[探针]` 开头的行。

| 键 | 动作 |
|---|---|
| F7 | 播放 UrlA |
| F8 | seek 到 60 秒，并在 +500ms / +1000ms 各读一次 `AudioSource.time` |
| F9 | 暂停 / 恢复 |
| F10 | 换歌压测 20 次，记录前后 `AudioClip` 数量 |
| F11 | 停止并清理 |
| F5 | 慢链验证：512 KB/s 链路起播，3 秒后往缓冲区间外 seek（证明流式是否真生效） |
| F6 | 隐藏屏幕面板 |

## 第一次实测（2026-11-07 20:45，失败）

```
[探针] AudioSource 就绪。spatialBlend=0 loop=true volume=0.8
[探针] 当前 AudioClip 计数（含游戏自带）= 379
[探针] 还没播任何东西。                     ← F9 先按了，说明热键是通的
[探针] 起播失败 [A] code=200 err=Request timeout 耗时=25006ms
[探针] AudioProbe 已销毁，资源已释放。        ← 退出干净
```

**不是代码问题。** `code=200` 说明 TCP/TLS/HTTP 全通，是 body 下载超时。随后实测带宽：

| 目标 | 结果 |
|---|---|
| `soundhelix.com` | **27.1 KB/s**，8.7 MB 整曲需 **323 秒** |
| `127.0.0.1:8765`（本机服务） | 125 MB/s，3.7 MB 用时 0.03 s |
| `music.163.com` 外链 | 302 → /404，返回 HTML，不是音频 |

三条结论：
1. **国外测试站对你这边不可用**（27 KB/s），拿它当闸门素材永远过不了；
2. `timeout` 写死 25 秒是错的，已改成配置项、默认 0（不限），并且下载期间每帧刷新缓冲百分比、每 10% 打一行日志；
3. 本机 HTTP 完全够用——除了"公网带宽"，Unity 的请求 / 解码 / 播放 / seek 每一行代码都走同一路径。

## 第二次实测（2026-11-07 21:05，四条全过）

原始日志（`LogOutput.log` 第 1031–1083、1147–1149 行）：

```
[探针] 热键实际走的是： Input System —— 这个结论要写进 notes.md
[探针] 缓冲 0% 0KB [A]
[探针] 起播成功 [A] 网络=38ms 出声=46ms len=237.11s freq=44100 ch=2 loadState=Loaded isPlaying=True clips=660
[探针] seek 测试 目标=60.0s 立即=60.00s +500ms=62.77s +1000ms=63.75s => PASS
[探针] 已暂停，time=66.98
[探针] 换歌压测开始：20 次，每首整曲下载，起始 clips=660
[探针] 压测 1/20 ok clips=660   …（20 次全是 660）
[探针] 换歌压测结束：clips 660 → 660 （正常）
[PeakMusic] 退出中——
[探针] AudioProbe 已销毁，资源已释放。
```

| 指标 | 目标 | 实测 | 判定 |
|---|---|---|---|
| 起播延迟（F7 → 出声） | < 1.5 s | **46 ms**（网络 38 ms + 解码 8 ms） | ✅ |
| `clip.length` 是否可信 | > 0 | **237.11 s**，44100 Hz / 2ch，`loadState=Loaded`，真实（3.69 MB @128k ≈ 236 s） | ✅ |
| seek 到 60 s 是否落位 | 落位且推进 | 立即 60.00 s → +500 ms 62.77 s → +1000 ms 63.75 s | ✅ |
| 切歌 20 次后 AudioClip 计数 | 不涨 | 660 → 660（20 次全等，`UnloadUnusedAssets` 生效） | ✅ |
| 暂停/恢复 | 顺 | 暂停时 `time=66.98`，与 seek 后继续播放的进度吻合 | ✅ |
| 与游戏原声是否冲突 | 不冲突 | 日志无法证明，需人耳确认（见下方待确认） | ⚠️ |
| 退出是否干净 | 不报错 | `AudioProbe 已销毁，资源已释放`，之后只有别的 mod 的 Piggyback NRE | ✅ |
| 热键走哪套输入 | — | **Input System**（`Keyboard.current` 拿到了 F7/F8/F9/F10，旧版 `Input` 全程未触发） | ✅ |

### 四条闸门 → 全过，可以进阶段 2

- [x] 起播 < 1.5 s（46 ms）
- [x] seek 成立（60 s 落位并持续推进）
- [x] 压测不涨 clip（660 → 660）
- [x] 退出干净

### 这次实测新发现的三个坑（原文都没有）

**1. `streamAudio = true` 时，`downloadProgress` / `downloadedBytes` 恒为 0。**
日志里每个请求只有一条 `缓冲 0% 0KB`，之后直接 `起播成功`。原因是流式模式下请求在"流已接上"时就算完成，
进度回调拿不到数据。**结论：阶段 3 想显示缓冲进度条，不能靠 `req.downloadProgress`**，要么自己写
`DownloadHandlerScript` 数字节，要么放弃流式改整曲下载。现在面板上的百分比在流式下没有意义。

**2. `AudioSource.time` 在流式模式下的读数不等于墙钟。**
seek 之后 0.5 s 的窗口里 `time` 跳了 2.77 s，再 0.5 s 跳了 0.98 s（合计 1 s 墙钟走了 3.75 s）。
落位判定用它是够的（都 ≥ 目标值），但**拿它做进度条 / 歌词同步会漂**。
阶段 3 若要精确进度，用 `AudioSettings.dspTime` 自己算差值。

**3. 压测基线不是 379，是 660。**
379 是插件 `Start()` 那一刻的数（主菜单还没加载完），按 F7 时游戏已经加载到 660。
所以"涨没涨"必须用同一时刻前后比，不能跨阶段比。文档里那条"≤ 379+3"的判据是错的。

### 待你确认的一件事

`spatialBlend=0`、`volume=0.8`，日志证明 `isPlaying=True` 且没有报错——但**出没出声只有耳朵知道**。
按 F7 时听到音乐了吗？如果听到了，阶段 1 才算真正闭环。如果没听到，那是音频输出路径的问题
（`AudioListener.volume` / 输出设备 / 游戏把主音量压到 0），跟这次的链路问题无关，我另开一条线查。

## 还要不要补一个"限速链路"验证（可选，F5）

本机 125 MB/s 有个副作用：**"秒开"和"整曲下完再播"在这条链路上表现完全一样**，
所以上面 46 ms 这个数字**不能证明 `streamAudio` 真的在边下边播**，只能证明"本机能播网络 mp3"。

为此加了 `F5` 慢链验证：`serve.py 8766 512` 把带宽限到 512 KB/s（整曲约 7.2 s），
F5 会起播 → 3 秒后往"已缓冲区间之外"（约 222 s）seek，然后报告落不落位。

这条验证的价值在阶段 3：如果流式下远距 seek 不落位，那"从下雨开始从第 60 秒播"就不能只靠
`AudioSource.time`，必须用 Range 重请求或整曲下载。**不跑也不挡阶段 2**。

```powershell
& "C:\Users\Admin\.workbuddy\binaries\python\versions\3.13.12\python.exe" `
  "C:\Users\Admin\WorkBuddy\2026-11-07-20-32-32\PeakMusic\testmedia\serve.py" 8766 512
```

## 已验证的离线结论

1. 网易 `song/media/outer/url` 匿名请求已失效（三个 id 全部 302 → /404）。
2. 本机 HTTP 服务的 Range 正常（`206` + `Content-Range: bytes 0-1023/3782791`）。
3. 节流器自检通过：3 次串行 1340 ms（≥ 3×(200+50)）。
4. 沙盒 `speed` 能直接量出"这首拉完要多久"——以后换源先测速再进游戏。

## 沙盒命令

```
PeakMusic.Sandbox.exe env
PeakMusic.Sandbox.exe url   <任意音频直链>
PeakMusic.Sandbox.exe speed <直链> [字节数]
PeakMusic.Sandbox.exe netease <songId>
set NETEASE_COOKIE=MUSIC_U=xxx; __csrf=yyy   &  PeakMusic.Sandbox.exe netease 186016
```

## 闸门判定（2026-11-07 21:05 结论）

四条全过，见上方「第二次实测」。**阶段 2 可以开工。**

唯一没有机器判据的是"与游戏原声是否冲突"，需要你在游戏里用耳朵确认一次。

---

# 阶段 2 实测记录（协议层）

## 结论先行：weapi 加密不需要做

原文档 2.1 那套 `AES-128-CBC ×2 + RSA(1024)` 可以整段跳过。明文 `/api/` 接口匿名就能调：

| 接口 | 结果 |
|---|---|
| `POST /api/cloudsearch/pc`（搜索） | ✅ `s=雨爱` → 8 条，带 `fee` / `privilege.st` |
| `GET /api/song/lyric/v1`（歌词） | ✅ 316100 → 56 行 LRC |
| `POST /api/login/qrcode/unikey`（二维码） | ✅ `{"code":200,"unikey":"5c5964ba-…"}` |
| `POST /api/song/enhance/player/url/v1`（播放地址） | ⚠️ 免费曲 ✅ / 会员曲 ❌ |

## 播放地址：不是"必须登录"，是"看这首歌要不要钱"

| 曲目 | fee | 匿名结果 |
|---|---|---|
| 3443155291（《雨爱》DJ 版） | 0 | **320kbps 直链**，`m801.music.126.net`，`206` + `audio/mpeg` + `ID3`，5,409,480 字节，69 ms 拿到首字节 |
| 316100（杨丞琳《雨爱》） | 1 | `url=null`，`code=-110`（需会员） |

→ **不登录也能跑通"搜索 → 取直链 → 播放"**，只是曲库限于免费歌。登录是解锁会员曲的增强项，不是前置条件。

## 直链是临时的，不许缓存

`http://m801.music.126.net/20261107214325/…/xxx.mp3?vuutv=<签名>&cdntag=…`

路径带日期、查询串带签名。**每次点播放现取一次，绝不落盘复用**（这也正好符合"不预取"那条防封规则）。
单曲循环缓存的是 AudioClip，不是 URL。

## 沙盒命令实测输出

```
> PeakMusic.Sandbox.exe search 雨爱 8
 1. 雨爱 - 杨丞琳  [雨爱] 4:20 (id=316100, fee=1)  $付费
 4. 雨爱 (DJ版) - 暴躁小猫有脾气  [] 2:15 (id=3443155291, fee=0)     ← 免费，可匿名播

> PeakMusic.Sandbox.exe song 3443155291
结果   : OK 320kbps 5282KB
直链   : http://m801.music.126.net/20261107214325/…mp3?vuutv=…
判定   : MP3 音频流，可以丢给 UnityWebRequestMultimedia

> PeakMusic.Sandbox.exe lyric 316100
行数   : 56（解析后，已丢掉时间戳）
  00:16.28  窗外的天气
  00:20.70  就像是 你多变的表情
```

## 顺手记的两个编译坑

- net472 的 SDK 风格工程**不会自动带** `System.Net.Http`，要显式 `<Reference Include="System.Net.Http" />`，否则 `HttpClient` 报 CS0234/CS0246。
- 不引 Newtonsoft：Unity 里第二份 Newtonsoft 会和游戏自带版本打架（MissingMethodException）。用自带的 `MiniJson`。

---

# 阶段 2 登录记录（2026-11-07 21:40，走 cookie 落地）

## 扫码登录被风控挡死

两次扫码，日志都走到这一步：

```
[21:29:41] code=801 等待手机扫码
[21:31:10] code=802 已扫码，等手机上点确认     ← 扫码本身成功
[21:31:14] code=8821 安全环境风险，网易拒绝这次登录
```

`8821` 出现在**手机点确认之后**，不是二维码/轮询的问题。换码重扫无效。
对照实验：带 `NMTID` cookie 的会话 poll 正常回 801；说明会话 cookie 是必要条件，但**不是 8821 的原因**——8821 是账号/环境侧判定。

## 最终解法：浏览器搬 cookie

浏览器登录 music.163.com → F12 → 网络面板 → 右键请求 →「以 cURL 格式复制」→ 从 `-b '...'` 里取 cookie。

```
PeakMusic.Sandbox.exe setcookie "MUSIC_U=...; __csrf=...; NMTID=...; _ntes_nuid=..."
```

结果：

```
已写入: C:\Users\Admin\AppData\Roaming\PeakMusic\cookie.txt
账号: 托风寄思念_9uz7（uid=5133885738）
=> 登录态有效
```

## 登录之后：会员曲仍然 -110，这是权限不是 bug

| 曲目 | 登录前 | 登录后 |
|---|---|---|
| 316100《雨爱》杨丞琳（fee=1） | -110 | **仍 -110**（`standard` / `exhigh` / `higher` 全试过） |
| 3443155291《雨爱》DJ 版（fee=0） | OK 320k | OK 320k |

原因：该账号**没有黑胶VIP**（网页上那张「黑胶VIP / 优惠购买」卡片就是证据）。
结论：`-110` 在**已登录**状态下应当解释为「会员曲/需购买/地区无版权」，而不是「没登录」。
沙盒 `song` 命令的提示文案已按此改写。

→ 游戏内点歌时，会员曲要给出一条人话提示（"需要黑胶VIP"），不能静默失败，也不能退化成播试听片段。

## 新增命令

- `setcookie "<cookie>"`：写入后立刻调 `/api/nuser/account/get` 验证，不做"存了就算成功"
- `tools/make_login_qr.py`：本地渲染登录二维码（白底黑块 + border=3，深色主题配色扫不出来；参数顺序任意，.png 结尾的当输出路径）

# 阶段 3（游戏内面板）

## 第一件事：F1 之前根本没绑定

日志里只有 F5–F11 的探针热键注册记录，**没有任何 F1 的代码**——阶段 1 的探针只做到"播放/seek/压测"，
面板是阶段 3 的内容，当时还没写。所以按 F1 没反应是预期行为，不是按键冲突、也不是热键被吞。

现在已绑定：**F1 打开/关闭面板，F2 作为备用键**（怕 F1 被别的 mod 占了）。
插件版本升到 0.3.0，启动日志里会有两行 `阶段3 面板已挂载`。

## IMGUI 只当画笔，不当输入源

PEAK 只开了新输入系统（阶段 1 实测热键走 `Keyboard.current`）。在这种配置下
`GUI.Button` / `GUI.TextField` 依赖的 `Event.current` 键盘鼠标事件不可靠，所以：

- 画：只用 `GUI.Box` / `GUI.Label`（纯绘制，任何事件类型下都能画）
- 点：自己读 `Mouse.current.position` + `leftButton.wasPressedThisFrame`，坐标翻转成 GUI 坐标（左上原点）
- 打字：自己订阅 `Keyboard.current.onTextInput`，退格/回车按 `Key.Backspace` / `Key.Enter` 处理

这样整块面板不依赖旧输入系统，也不会踩 `Input.mousePosition` 抛异常的坑。

## 中文输入是这套方案唯一的软肋

`onTextInput` 拿的是 IME 合成后的字符，Win 上一般能打进中文，但不保证。
所以面板上放了四个**预设词**（雨爱 / 下雨天 / 江南 / 晴天），点一下就搜——中文搜不到时不至于卡死。
预设词在 `MusicPanel.Presets` 里改。

## 起播策略：起点 > 0 就不许流式

阶段 1 的 F5 实测（512KB/s 限速链路）：

```
慢链远距 seek 目标=222s 立即=0.00s +500ms=0.00s +1000ms=0.00s => FAIL
```

所以 `MusicPlayer.Play(song, url, startAt)` 里：`startAt > 0.5s` 时强制
`streamAudio = false`（整曲下完再 seek），否则 `AudioSource.time` 设了也白设。
正常点歌（startAt = 0）仍走流式，秒开。

## 依赖变化

插件现在引用 `PeakMusic.Core.dll`，部署目标已改成同时拷这两个 DLL 到
`BepInEx\plugins\PeakMusic\`。少拷 Core 会在游戏里报"找不到类型"而不是编译失败。

## 范围约定：不做「下雨自动切歌」（2026-11-07 21:54 用户明确要求）
原流程文档阶段 3 最后一条是"下雨时自动触发某个 BGM"。**明确不做。**

- 不监听天气 / 不 patch 天气状态机、不订阅任何游戏事件
- 不保留 `startAt > 0` 的"从第 N 秒起播"调用方（`MusicPlayer.Play` 的这个参数留着，
  但游戏内没有任何自动路径会传非 0 值，只在你手动操作时用）
- 面板里那四个预设词（雨爱 / 下雨天 / 江南 / 晴天）只是搜索关键词，跟天气无关，保留

插件最终形态是**纯手动点歌机**：F1 开面板 → 搜 → 点 → 播。行为完全由玩家操作决定。

# 阶段 3 实机（22:00 首屏正常，22:06 修翻页）

用户截图确认：**面板能开、中文正常、搜索出结果、列表/按钮都画对了**。
→ `System.Net.Http` 在 Unity Mono 下没有炸（这是沙盒验证不了、我唯一担心的点）。

## 翻页点了没反应：不是坐标问题，是判据写错

```csharp
if (_page + PageStep + PageSize <= _total) _page += PageStep;  // 20+8 <= 8  不成立
else if (_page + PageSize < _total) _page = ...;               // 0+8 < 8    不成立
```

`_total` 被算成 `_songs.Count + _page` = 8，`PageStep` 却是 20。两个分支永远不进。
更深的病根：**`_page` 同时当"远程 offset"和"本地列表下标"用**，就算判据改对，第 2 页会直接空白。

## 改法：一次取 40 首，本地翻页

- 搜索固定 `SearchAsync(kw, FetchLimit=40, 0)`，`_page` 只表示本地起始下标（0/8/16/24/32）
- `TurnPage(dir)`：下一页 `min(_page+8, MaxPageStart)`，上一页 `max(0, _page-8)`
- 到边界不再静默：状态行说"已经是最后一页（第 3/3 页）"，按钮变灰
- 新增页码显示「第 x/y 页 · 共 n 首」+ **滚轮翻页**（仅鼠标在列表内响应，不抢游戏镜头缩放）
- 顺手补了 `T` 进输入态——底部一直写着"按 T 输入"，代码里从来没绑过

为什么不做服务端翻页：每翻一页重发一次搜索，撞 `-460` 风控的概率白白变高，
而 40 首对点歌机够用。真想看第 41 首以后，换关键词更实际。



# 阶段 3 真凶：Unity 6 禁止 http 明文连接（22:10 修）

日志（22:07 那次运行）里点歌不出声，全挂在同一条上：

```
[Warning: Unity Log] Non-secure network connections disabled in Player Settings
[Error  : Unity Log] InvalidOperationException: Insecure connection not allowed
  at UnityEngine.Networking.UnityWebRequest.BeginWebRequest ()
  at PeakMusic.MusicPlayer+<LoadAndPlay>d__31.MoveNext ()
```

## 结论

网易 CDN 回的直链是 `http://m804.music.126.net/...`，而 Unity 6 的 Player Settings
默认把 "Allow downloads over HTTP" 关了。`SendWebRequest()` 在**创建连接这一步就同步抛异常**，
协程当场被终止 → `Loading` 永远停在 true → 面板一直"加载中"，看着就是"点了没反应"。

注意这个异常发生在 `SendWebRequest()` 调用处，不是 `req.result`——所以 `result != Success`
那条错误分支根本走不到，日志里也就没有"加载失败"。**任何 try/catch 必须包住 SendWebRequest 本身。**

## 验证：CDN 同时开了 443

同一条带签名的直链，把 scheme 换成 https 后：

```
HTTP/1.1 200 OK            Server: Tengine
Content-Type: audio/mpeg    Content-Length: 8444909
-range 0-1023 → 206 Partial Content
```

→ 升协议即可，不用改游戏设置，也不用退回 weapi。

## 改法（两处，双保险）

1. `NeteaseClient.ForceHttps()`：`GetSongUrlAsync` 出参统一 `http→https`；
   **localhost / 127.0.0.1 不升级**（Unity 对本地地址放行，且本机测试服务没证书，
   阶段 1 的探针正是靠这条豁免跑通的）。
2. `MusicPlayer.LoadAndPlay`：请求前再 ForceHttps 一次并打日志；`SendWebRequest()` 包 try/catch，
   失败就把 `Loading=false` 复位、Dispose 请求、`yield break`，绝不让面板卡在"加载中"。

编译 0 警告 0 错误，Core 35328 / 插件 41472 字节，22:10 已部署。
沙盒复测：直链 `https://m804...` → 206 audio/mpeg。

## 顺带确认

同一次日志里 `-110` 的那几首（Moonlight / X / Everybody Dies / 松本梨香之外的 XXXTENTACION）
是账号没会员或没版权，属于预期，不是 bug。

# 22:14 UI 重做：从默认灰盒子到自绘深色面板

用户原话「这个 ui 太丑了」。原版全是 `GUI.Box(rect, GUIContent.none)` 的 Unity 默认皮，
灰底黑字圆角都没有，跟 PEAK 的画面完全不搭。

## 做法：IMGUI 当画布，皮肤全部运行时生成

不换渲染方案（换 UI Toolkit / uGUI 要重写输入层，且新输入系统下 Event.current 依旧不可靠），
只把所有视觉元素自己画：

- **圆角靠九宫格**：`RoundTex()` 用圆角矩形 SDF 生成 2r+8 见方贴图（1px 抗锯齿），
  `GUIStyle.border = r+3` 做九宫格拉伸 → 任意尺寸都不糊，不需要美术资源
- **投影**：面板外扩 7px 画一张低透明度大圆角，把面板从画面里"抬"起来
- **唯一强调色**：网易云红 #EC4141，只用在标题竖条、主按钮、播放中行、进度条、VIP 标
- **字体**：`Microsoft YaHei UI`（游戏里 Prophecy 用的就是这个）→ YaHei → SimHei 兜底
- **富文本**：`GUIStyle.richText` 让行内混色（时长灰、VIP 红、无版权更灰），不用叠好几个 Label

## 布局（700×640，全部绝对定位，Update 与 OnGUI 共用同一套 Rect）

| 区域 | y | 高度 |
|---|---|---|
| 标题 + 状态徽标 | 16 | 28 |
| 搜索行（输入框 + 搜索 + 清空） | 63 | 34 |
| 预设词胶囊 | 109 | 26 |
| 状态行 | 147 | 18 |
| 结果列表 8×30 | 175 | 248 |
| 翻页 | 433 | 28 |
| 正在播放卡片（含进度条） | 473 | 58 |
| 控制条（暂停/停止/音量） | 543 | 32 |
| 页脚两行 | 594 / 614 | 16 |

## 交互细节（都是"看着像死的"变"有反应"）

- 每行都有 hover 高亮 + 左侧 2px 竖条；正在播的那行常亮红条
- 按钮 hover 变亮，主按钮 hover 变浅红；不可用时整块压暗到 `#454A56`
- 搜索框聚焦时描边变红，光标按中英文宽度估算位置后自己画（不用 TextField）
- 空列表时居中提示"正在搜索…/还没有结果"，不再是左上角一行小字
- 播放卡片用圆点 + 文字表达状态：缓冲中（琥珀）/播放中（红）/已暂停（灰）/空闲
- 贴图在 OnDestroy 里逐个 Destroy，避免反复开关插件漏显存

## 验证

`PeakMusic/docs/ui-mock.html` 是按同样像素坐标做的静态稿（可离线开），
用来在重启游戏前先看效果。编译 0 警告 0 错误，22:14 部署 PeakMusic.dll 48128 字节。

# 22:23 照 MuoniumPlayer 的观感重做面板（v2）

用户丢来 `MuoniumPlayer-multiversion-main.7z`（Minecraft 的网易云播放器 mod，126MB，
3043 个条目，7 棵版本树 + `muonium-common` 共享层 1365 个 Java 文件），选了「照它的观感重做」。

## 从它那里抄了什么（抄设计，不抄实现）

它用 **Skija**（Skia 的 Java 绑定）做矢量渲染并直接画进 GL，跟我们的 IMGUI 是两套栈，
所以照搬渲染代码没有意义。真正可搬的是**设计系统**：

1. **11 个语义色角色**（`NCMTheme.ThemePreset`）：
   GENERIC_BACKGROUND / ELEMENT_BACKGROUND / ELEMENT_HOVER / PRIMARY_TEXT / SECONDARY_TEXT /
   ACCENT / ACCENT_HOVER / INPUT_BACKGROUND / NAVIGATION_BACKGROUND / BORDER。
   按角色命名后，换主题不用改任何绘制代码 —— 这就是它 15 套主题能共存的原因。
2. **背景带色相**，不是中性灰：丝绒玫瑰 `#211821`（玫瑰紫）、极光海 `#0D1720`（青黑）。
3. **BORDER 比背景亮得多**（`#604252` vs `#211821`）：面板边缘能看见，才有"物质感"。
4. **ACCENT / ACCENT_HOVER 两级**，且都不是纯饱和红 —— 丝绒玫瑰用 `#E48AAB` 柔和粉。
5. **封面是主角**：手机 App 的骨架是「封面 + 曲名 + 歌手」在上，整条播放控制在下。

## v2 布局（720×696）

- 头部 = 正在播放：52px 圆角封面 + 曲名(16 bold) + 歌手·专辑(11) + 状态徽标 + 配色按钮
- 搜索行 36 高 + 预设胶囊 + 状态行
- 结果列表：8 行 × 40 高，**每行带 30px 封面**，悬停/播放行左侧 2px 强调竖条
- 翻页行
- 底部播放条（nav 底色，圆角 14）：顶部进度线 + 时间/状态 + 居中「停止 + 圆形播放键」+ 右侧音量 −/+/%
- 页脚一行：登录态 + 快捷键

## 新增两个文件

- `PeakMusic/Palette.cs`：4 套配色（丝绒玫瑰 / 午夜石墨 / 极光海 / 曜石红），hex 直接抄它的预设
- `PeakMusic/CoverCache.cs`：封面缓存
  - 拉 `?param=96y96` 小图（几 KB），`ForceHttps` 后请求（封面地址同样是 `http://p2.music.126.net/...`）
  - 缩到 64×64 后**把圆角烤进 alpha** —— IMGUI 的 `GUI.DrawTexture` 不支持遮罩，只能预先烤
  - 并发上限 3、LRU 64 张、失败 URL 进 `_failed` 不再重试（否则会每帧重拉）
  - **坑**：`yield` 不能写在带 catch 的 try 里（CS1626），`SendWebRequest()` 必须单独 try 只取返回值

## 实测依据

- 封面 https 可用：`https://p2.music.126.net/...jpg?param=96y96` → `200 image/jpg`
- 搜索接口返回的 `al.picUrl` 就是 `http://`，不升级协议 Unity 会直接拒绝（与播放直链同一个坑）

## 验证

`docs/ui-mock.html` 重做为 v2 静态稿，右上角四个按钮可实时切换 4 套配色。
编译 0 警告 0 错误，22:23 部署 PeakMusic.dll 52224 字节。

# 22:35 多音源 + 漂浮歌词 + 菜单式 UI（v3，照 MuoniumPlayer 的完整形态）

用户看完 B 站那个视频后明确了想要的不是"配色"，而是它的三件事：
**多音源可选 / 游戏正中央漂浮歌词 / 内置菜单**。

## 一、多音源：技术探路结果（都是实测，不是推测）

MuoniumPlayer 有 13 个平台（网易云/QQ/酷狗/酷我/咪咕/YouTube/B站/汽水/GD/SMTC/Spotify/Apple/本地）。
逐个验完，**只有两家的匿名链路真正能出 mp3**：

| 平台 | 搜索 | 取流 | 歌词 | 结论 |
|---|---|---|---|---|
| 网易云 | ✅ 明文 | ✅ `song/enhance/player/url/v1` | ✅ | 已在用 |
| QQ音乐 | ✅ `client_search_cp` | ✅ 匿名 mp3 | ✅ base64 LRC | **本次新增** |
| 其余 | 需各自逆向 + 多数要登录 cookie | — | — | 暂不做 |

### QQ 音乐的三条接口（全部匿名可用）

1. **搜索** `https://c.y.qq.com/soso/fcgi-bin/client_search_cp?format=json&t=0&p=1&n=40&w=<kw>`
   - 必须带 `Referer: https://y.qq.com/`
   - **坑**：绝对不能加 `new_json=1`。那个参数会把 `song.list` 的字段整体改名
     （`songmid`→`mid`、`singer` 嵌套进 `album`），实测加完之后一首歌都解析不出来，
     而且接口照样返回 `code:0`，非常难查。老接口只吃 `format/t/p/n/w` 五个参数。
   - 关键字段：`songmid`（14 位 base62 字符串）、`songname`、`singer[].name`、
     `albumname`、`albummid`、`interval`（秒）、`pay.payplay`

2. **取流** `POST https://u.y.qq.com/cgi-bin/musicu.fcg`，两段式：
   ```
   先 CDN.SrfCdnDispatchServer/GetCdnDispatch 拿 sip 列表
   再 vkey.GetVkeyServer/CgiGetVkey 拿 purl
   ```
   - **核心技巧**：`param` 里带 `filename` 数组可以指定音质前缀，
     给 `M500{mid}.mp3` 才返回 mp3。
   - **不给 filename 的默认结果是 `C400{mid}.m4a`（AAC）**，
     而 Unity 的 `UnityWebRequestMultimedia` 解不了 AAC —— 等于"能下载但不能播"，
     这是最容易踩的坑：直链 206 正常、content-type `audio/mp4` 也正常，就是没声音。
   - 音质前缀对照：`M500`=128k mp3（匿名可用）/ `M800`=320k mp3（**匿名 purl 为空**）/ `C400`=m4a
   - 返回的 `sip` 是 `http://`，**必须升 https**（Unity 6 禁止明文，与网易同一个坑）
   - 付费曲 `payplay=1` → `purl` 为空串 → 我们映射成 `code=-110`，
     与网易语义一致，面板复用同一条提示

3. **歌词** `https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg?songmid=<mid>&format=json&nobase64=0`
   - `lyric` / `trans` 字段是 **base64(UTF-8)**，解码后是标准 LRC
   - 实测杨丞琳《雨爱》48 行，时间戳正常

4. **封面** `https://y.gtimg.cn/music/photo_new/T002R300x300M000{albummid}.jpg`
   - 300×300 可用（`T002R96x96M000` 是 404）

### 代码结构（新增 4 个文件）

- `Core/Sources/Platform.cs` —— 平台枚举 + 显示名 + 品牌色（`0xE94747` / `0x31C27C`），
  **只能往后追加**（配置存序号）
- `Core/Sources/QqClient.cs` —— 上面三条接口
- `Core/Sources/MusicSources.cs` —— 统一入口，面板只认它；
  **所有请求共用同一个 Throttle**，跨平台也串行，不出现"同时打两家"
- `SongInfo` 加 `Platform` + `SourceId` + `Key`（`QQ:000FKMIu2SVEZE` 这种跨平台唯一键）

`GetUrlAsync(song)` 按 `song.Platform` 分发而不是按"当前选中平台"——
列表里可能留着切平台前搜到的歌，点它必须回它自己的源取流。

### 沙盒验证命令

```
qq search <kw>            # 搜索
qq song <songmid>         # 取流 + 魔数探测（ID3 / MPEG 帧头）
qq lyric <songmid>        # 歌词
src search <netease|qq> <kw>   # 走统一入口，连取流带歌词
```

实测：
```
qq search 雨爱 → 8 首，付费/免费标注正确
qq song 000FKMIu2SVEZE → code=200, https, 206 audio/mpeg, 魔数=ID3 (mp3)
qq song 000rh0dE2TyUic → code=-110（付费曲，purl 空）
qq lyric 000rh0dE2TyUic → 48 行
```

## 二、漂浮歌词 `PeakMusic/LyricOverlay.cs`

照 MuoniumPlayer 的 HUD 歌词：屏幕中央偏上一行大字，逐字点亮，上下邻行压暗。

### 逐字点亮怎么在没有逐字时间的情况下做

LRC 只有**行级**时间戳，真正的逐字要走网易 yrc / QQ qrc（另一套格式 + 另一个接口）。
这里用「行内按字数线性插值」：
```
progress = (t - 本行start) / (下一行start - 本行start)
已唱宽度 = progress * 整行宽度
```
正常语速几乎看不出误差，成本为零。

### 为什么用裁剪而不是富文本分色

把一行拆成「已唱」+「未唱」两段拼富文本，需要精确字符宽度，中英混排一定错位。
改成：**同一条文字画两遍，第二遍用 `GUI.BeginClip` 裁到「已唱宽度」**。
两遍的字体度量完全一致，边界永远对得齐。

字符宽度用 `CalcSize(前缀)` 逐字累加，行不变就缓存（`_prefix` / `_prefixFor`）。

### 其它视觉细节

- 切行：当前行从下方 22px 滑入（ease-out），旧行上浮 14px 淡出（0.25s）
- 描边：IMGUI 没有原生 stroke，在 4 个方向偏移 1.2px 画黑色再画本色
  —— 这是"浮在亮场景上"的必要成本，4 次额外 Label 可忽略
- 超宽时按比例缩字号，不截断
- 前奏（`_index < 0`）显示第一行压暗当预告
- 没歌词时显示 `♪ 歌名 — 歌手`，不留空白
- 翻译行按**时间戳最近匹配**（不按下标），差 >1.5s 就认为对不上、宁可不显示
- 配置：`LyricEnabled` / `LyricSize`(34) / `LyricY`(0.46) / `LyricTranslation`

### 歌词通道独立于面板

`StartLyricFetch` / `PollLyric` 用**独立的 Task + 独立的字段**，不占 `_busy`：
- 歌词比取流慢，也不影响出声，不该阻塞"能不能点下一首"
- `PollLyric()` 放在 `Update()` 里 `if (!_open) return` **之前**，
  这样面板关着也照样把歌词交给歌词层

## 三、菜单式 UI

`PanelW` 720 → **832**，左侧让出 96px 给音源导航：

- 导航项 = 品牌色圆点 + 平台名 + 「当前音源 / 点击切换」
- 选中项：`_sRowPlay` 铺底 + 左侧 2px 竖条（竖条用 `GUI.color` 染品牌色）
- 圆点：**一张白色圆角贴图 + `GUI.color` 着色**，不是每个平台生成一张
- 导航底部显示歌词开关状态
- 切平台清空列表：混着显示会让人误点，也不知道点了去哪个源取流
- VIP 标按平台区分文案：网易「VIP」/ QQ「会员」（同一个 Fee 字段）

## 验证

- 编译 **0 警告 0 错误**，22:34 部署（Core 47104 / PeakMusic.dll 62464 字节）
- 沙盒四条命令全部实测通过
- `docs/ui-mock.html` 重写为 v3：模拟游戏画面 + 漂浮歌词 + 带左侧导航的面板，
  可切 4 套配色、可「只看歌词」

# 22:53 三处修正：歌词被擦掉 / 探针抢声 / 歌词改贴边多行

用户截图反馈三件事：①歌词只有一行「♪ 雨爱 — 戴羽彤」，没有实时歌词；②按 F7 探针也会播，
和面板同时出声，画面还乱；③歌词要在画面**一侧**像 MC 模组那样多行动态出现，不是压在正中。

## 一、「没有实时歌词」的真因：歌词被自己的 Update 擦掉了

日志时间线（同一个 id=2693371705 戴羽彤）：

```
[歌词] 歌词就绪：56 行 · 雨爱        ← 22:44:xx
[播放器] 缓冲 15% / 41% / 66% / 91%
[播放器] 起播 雨爱 len=213.62s       ← 约 1 秒之后
```

歌词**比起播早到**。而旧 `Update()` 里写的是：

```csharp
if (_player == null || !_player.HasClip) { if (...) Clear(); return; }
```

缓冲那 1 秒里 `HasClip` 还是 false → **每帧 Clear 一次** → 56 行当场清零，
之后只剩 `_lines.Count == 0` 的兜底文字「♪ 歌名 — 歌手」。截图里那一行就是兜底。

修法：清空条件改成「**歌真的换了**」——只比 `_player.Current.Key != _song.Key`。
缓冲期、暂停、停止都只是「不画」，绝不清数据。

教训：缓存/状态的失效条件要用**业务主键**（哪首歌），不要用**瞬时状态**（有没有 clip）。

## 二、F7 双声源：探针默认下线

`AudioProbe` 是阶段 1 的验证探针，自带一个 AudioSource，跟面板播放器完全独立。
面板还在播《雨爱》时按 F7，两个 AudioSource 各放各的 —— 这就是"两个人同时播放"。
它左上角那排调试文字（PeakMusic 阶段1 探针 / clips=661 / 延迟 496ms）也是画面"乱"的一大来源。

- 新增配置 `探针 / EnableProbe`，**默认 false**，不再挂载 AudioProbe
- 代码保留，要做回归测试时改成 true 即可
- 顺手加互斥：探针真正开始播之前先 `MusicPlayer.Active.Stop()`，开着也不会叠音

## 三、歌词改成贴边多行（照 MC 模组那个 HUD 挂件）

翻它的 `MusicLyricsWidget`：那是个可拖动的挂件，`width=450 height=120`，
`alignMode = Left / Center / Right`，块内多行排列、**块边缘把行淡到 0**（`calculateEdgeAlpha`），
行级时间 + graceScroll 平滑滚动。不是我们原来那种"屏幕正中一行大字"。

新实现（`LyricOverlay.cs` 重写 OnGUI）：

| 项 | 值 |
|---|---|
| 块宽 | 左侧/右侧 36% 屏宽（上限 620px）；正中 78% |
| 贴边留白 | max(36px, 3.5% 屏宽) |
| 同屏行数 | 默认 5（`歌词/Lines`，3–7，强制奇数） |
| 行距 | 当前行字号 × 1.55 |
| 邻行字号 | 当前行 × 0.74 |
| 分层不透明度 | 1.00 / 0.52 / 0.26 / 0.12（再远不画） |
| 对齐 | 左→左对齐，右→右对齐，中→居中 |

- **连续滚动**：`_vScroll` 用指数平滑（k=13）逼近当前行下标，
  y 按 `(idx - _vScroll) * 行距` 算 → 观感是整块滑过去，不是一行一行跳
- 当前行进场：`_lineIn` 0→1，同时轻微上浮 8px + 亮度 0.72→1
- 跳太远（seek / 换歌，>4 行）直接落位，不让中间几十行一路滚
- 逐字点亮仍是「画两遍 + BeginClip 裁到已唱宽度」，与之前一致
- 缩字号逻辑改成按行缓存（`_fitFor/_fitFont`），否则每帧要算两遍 `CalcSize`
- 面板左侧导航底部「歌词 开 / 左侧 · 点这里换边」可点，循环 左→中→右，立刻生效

## 验证

- 编译 **0 警告 0 错误**，22:53 部署 `PeakMusic.dll` 63488 字节
- 静态预览 `docs/lyric-side.html`：模拟泳池馆画面 + 7 行歌词连续滚动 + 逐字填充，
  右上角可切 左/中/右
