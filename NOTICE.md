# NOTICE · 第三方代码来源与许可

本项目的**大部分代码为原创**，但歌词渲染与逐字动画部分移植自第三方开源项目。
按 GPL-3.0 的要求，这里保留来源与许可声明——**不要删除本文件**。

## 1. 移植部分（GPL-3.0）

| 本项目文件 | 来源 |
|---|---|
| `PeakMusic/PeakMusic/WorldLyricRenderer.cs` | WorldLyrics / LyricPlacement / LyricMaterial / TextHalo |
| `PeakMusic/PeakMusic/LyricOverlay.cs` | 歌词宿主（世界空间歌词挂载与生命周期） |
| `PeakMusic/PeakMusic.Core/Lyrics/LyricAnimation.cs` | LyricAnimation（逐字显现 / 抖动） |

上述代码来自开源项目 **PeakDangoMusic**（作者：布洛芬 / ZHIZHU4410），
其歌词实现又源自 **Sorihiko 的 DangoMusic** 项目。原项目以 **GPL-3.0** 发布。

本项目的改动：把歌词几何/材质/动画接到 Unity 6 + TextMeshPro 上，并接入本项目自己的
播放器、接口层与样式系统；同时新增逐字双色点亮、六种整句运动、四种入场/退场等逻辑。

## 2. 许可

因为包含 GPL-3.0 代码，**本插件整体以 GPL-3.0 分发**（见同目录 `LICENSE`）。
再分发编译后的 DLL 时，必须同时提供：本 NOTICE、`LICENSE` 全文、以及对应源码。

## 3. 原创部分

样式预设系统（`LyricColors.cs` 的十套样式）、面板 UI（含样式选择页）、
网易云 / QQ 音乐接口与登录、一起听联机同步、跟随播放采集器、构建与部署脚本，
均为本项目自行实现。

## 4. 与同目录其他模组的关系

安装目录里可能同时存在 `PeakDangoMusic.dll` 与 `PeakDangoMusic/` 文件夹——
那是上述第三方模组**自己的本体与运行时**，不属于本项目，本项目不修改、也不重新分发它的文件。
两者是独立模组，可各自开关。

## 5. vendored 上游源码（AGPL-3.0，仅参考，不参与构建）

`vendor/folia-major/` 存放上游 **folia-major** 的完整源码快照（commit `b7423b4`，AGPL-3.0）。
它**不参与本项目的 C# 编译、不进入安装包**，仅用于对照其歌词排版/动画实现。

若将来把它的代码或构建产物纳入分发，本项目的许可必须整体切换为 AGPL-3.0 并公开源码。
详见 `vendor/folia-major/INTEGRATION.md`。

## 6. 随包分发的第三方二进制

| 文件 | 来源 | 用途 | 许可 |
|---|---|---|---|
| `FlacBox.dll` | NuGet `flacbox` 1.0.1（作者 notmasteryet，上游 flacbox.codeplex.com） | FLAC 无损音频解码（纯 C#，无原生依赖） | 该 NuGet 包**未标注 license 字段**，上游为 CodePlex 时代项目。再分发前建议向上游确认（MS-PL / 公有领域两种说法都有）。若不接受此不确定性，可删除该 DLL 并关闭无损功能（`[音质] QualityLevel = 3`），其余功能不受影响。 |

FlacBox 只在"会员 + 无损/高解析档"时被调用；没有它时插件仍能正常加载，
取流会自动退回 320k（`FlacLoader` 会记录一条"无损不可用"日志）。
