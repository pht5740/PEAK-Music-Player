# PeakMusic — PEAK 音乐 Mod

按 `D:\QQ\PEAK音乐Mod-施工流程.md` 施工。当前进度：**阶段 1 四条闸门全过；阶段 2 协议层已跑通（搜索 / 歌词 / 免费曲直链 / 扫码登录）**。

## 工程结构

```
PeakMusic.sln
├─ PeakMusic.Core/      类库 net472 —— 纯 C#，零 Unity 依赖（节流、加密、API、模型、歌词）
├─ PeakMusic.Sandbox/   控制台 net472 —— 协议层在这里跑，2 秒出结果，不进游戏
└─ PeakMusic/           BepInEx 5 插件 —— 只做 Unity 侧；编译后自动部署到游戏 plugins
```

## 构建

```powershell
cd C:\Users\Admin\WorkBuddy\2026-11-07-20-32-32\PeakMusic
C:\Users\Admin\.dotnet\dotnet.exe build -c Release
```

- SDK：`C:\Users\Admin\.dotnet`（8.0.427，由 `global.json` 锁定；机器 PATH 上的 `dotnet` 只有运行时，不能用）
- 目标框架 `net472`，引用全部来自 `D:\Steam\steamapps\common\PEAK`，`<Private>false</Private>` 一个都不复制
- `AfterTargets="Build"` 自动把 `PeakMusic.dll` 复制到 `BepInEx\plugins\PeakMusic\`

## 沙盒

```powershell
$exe = ".\PeakMusic.Sandbox\bin\Release\net472\PeakMusic.Sandbox.exe"

# 阶段 1（网络音频链路）
& $exe env                              # 运行时 + 游戏依赖 + 节流器自检
& $exe url   https://xxx.mp3            # 拉前 1KB，判断是不是真音频直链（几毫秒）
& $exe speed https://xxx.mp3 4194304    # 实拉 4MB 量带宽，并算出"整首要多久"
& $exe netease 186016                   # 网易外链探针（匿名已失效，见 docs）

# 阶段 2（协议层，全部不需要 weapi 加密）
& $exe search 雨爱 30                   # 搜 30 首，打印 fee / 无版权标记
& $exe song   3443155291 exhigh         # 取播放地址 + 自动验直链 + 打印前几行歌词
& $exe lyric  316100                    # 只看歌词
& $exe login                            # 扫码登录（打印链接 → 浏览器打开 → 手机扫码）
& $exe me                               # 当前账号
& $exe logout                           # 清掉本地登录态
```

登录态存在 `%APPDATA%\PeakMusic\cookie.txt`，只保留 `MUSIC_U` / `__csrf` / `NMTID` 等 5 项，不存账号密码。

**免费曲目（`fee=0`）匿名就能拿到直链**，登录只影响会员曲库——所以不登录也能跑通"搜索→播放"。

**换音源前先 `speed` 测一下。** 实测本机到 soundhelix.com 只有 27 KB/s，8.7 MB 要 323 秒，
这种源拿来当测试素材只会浪费时间。

## 本机测试音频服务

```powershell
& "C:\Users\Admin\.workbuddy\binaries\python\versions\3.13.12\python.exe" `
  "C:\Users\Admin\WorkBuddy\2026-11-07-20-32-32\PeakMusic\testmedia\serve.py" 8765
```

- 素材：`testmedia\test-song.mp3`（3.69 MB，3.6 分钟，够测 seek）
- `serve.py` **支持 Range**（`python -m http.server` 不支持），行为和真实 CDN 一致
- 走本机是为了绕开公网带宽：Unity 的请求/解码/播放/seek 代码路径完全一样，只是快


## 阶段 1 怎么验收（已过，2026-11-07）

结果在 `notes.md`：起播 46 ms、seek 60s 落位、20 次切歌 660→660、退出干净。

1. 启动 PEAK，主菜单；
2. `F7` 起播 → `F8` seek 到 60s → `F9` 暂停恢复 → `F10` 压测 20 次 → `F11` 停止；
3. 可选：`F5` 走 512 KB/s 限速链路，验证"边下边播"是否真生效（见下）；
4. 看 `D:\Steam\steamapps\common\PEAK\BepInEx\LogOutput.log` 里 `[探针]` 开头的行；

限速链路（F5 用，端口 8766，512 KB/s）：

```powershell
& "C:\Users\Admin\.workbuddy\binaries\python\versions\3.13.12\python.exe" `
  "C:\Users\Admin\WorkBuddy\2026-11-07-20-32-32\PeakMusic\testmedia\serve.py" 8766 512
```

配置在 `BepInEx\config\com.peakmusic.probe.cfg`（UrlA / UrlB / UrlSlow / SeekSeconds / Volume / TimeoutSeconds）。

## 阶段 3 面板（游戏内点歌）

启动 PEAK 后按 **F1**（备用 **F2**）打开面板，屏幕居中：

1. 点搜索框或按 **T** 进入输入态，打字 → **回车**搜索（中文打不进去就点预设词：雨爱/下雨天/江南/晴天）
2. 结果列表里**点一行** → 取播放地址 → 出声
3. 底部按钮：暂停/继续 / 停止 / 音量 −/+；`Esc` 退出输入态（面板不关，留给游戏自己的 Esc 菜单）

日志前缀：`[面板]`（搜索、取地址、失败原因）、`[播放器]`（起播、缓冲、清理）。

面板逻辑在 `PeakMusic/MusicPanel.cs`，播放器在 `PeakMusic/MusicPlayer.cs`。
提醒：点歌时游戏仍在收键盘，**输入最好在主菜单/暂停时做**，否则 WASD 会同时控制角色。

> **范围声明（2026-11-07 决定）**：本插件是**纯手动点歌机**，不做「下雨时自动切歌」。
> 不 patch 天气状态机、不订阅任何游戏事件，播放完全由玩家操作触发。

## 已知修正

见 `docs/施工流程-修订.md`。最要紧的一条：**网易 `outer/url` 外链匿名请求已失效**，
阶段 1 不要用它当素材。
