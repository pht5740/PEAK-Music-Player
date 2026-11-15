using System;
using System.Text;
using System.Collections.Generic;
using System.Globalization;
using PeakMusic.Core;
using PeakMusic.Core.Lyrics;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TextCore.LowLevel;

namespace PeakMusic
{
    /// <summary>
    /// 世界空间 3D 歌词（TextMeshPro SDF）。
    /// 移植自 PeakDangoMusic 的 WorldLyrics + LyricPlacement + LyricMaterial + TextHalo（GPL-3.0）。
    ///
    /// <para>跟旧 IMGUI 版的核心区别：歌词不是画在屏幕上，而是在<b>相机左右两侧</b>、
    /// 按 FOV 算出的散布角、距离相机一定距离处，放置真正的 3D 文字网格。它正对相机、
    /// 会随视角转动，近处岩壁会挡住它。逐字弹出/抖动是直接改写 TMP 的网格顶点实现的。</para>
    ///
    /// <para>数据源仍是 <see cref="MusicPlayer"/>（UnityWebRequest 播放），这里只读
    /// <c>Time</c> 和 <see cref="LyricOverlay"/> 喂进来的 <c>LrcLine</c> 列表。</para>
    /// </summary>
    internal sealed class WorldLyricRenderer : IDisposable
    {
        private const string Tag = "[歌词3D]";

        // 字面颜色直接用面板配色（LyricColors.Lit），这样「歌词颜色」那栏对 3D 歌词也生效

        private readonly List<Entry> _entries = new List<Entry>();
        private readonly List<UnityEngine.Object> _resources = new List<UnityEngine.Object>();

        private TMP_FontAsset _font;
        private string _mainFontFile = "";
        private readonly List<UnityEngine.Object> _fontAssets = new List<UnityEngine.Object>();
        private int _fontRev = -1;

        /// <summary>字体配置的版本号。面板换了字体就 +1，渲染层下一帧自动重载。</summary>
        internal static int FontRevision;
        private GameObject _root;
        private Entry _current;
        private int _serial;
        private bool _fontFailed;
        private int _renderIndex = -1;
        private LyricFx _fx;
        private int _styleSig;
        private float _retryAt;

        private List<LrcLine> _lines = new List<LrcLine>();
        private string _songKey = "";

        internal string RenderStatus =>
            _fontFailed ? "3D歌词：字体不可用（已回退屏幕歌词）"
            : _font != null ? "3D歌词：世界空间 SDF（TMP）"
            : "3D歌词：等待进入地图";

        // ───────────────────────────── 对外 ─────────────────────────────

        internal void SetLyrics(string songKey, List<LrcLine> lines)
        {
            _songKey = songKey ?? "";
            _lines = lines ?? new List<LrcLine>();
            _renderIndex = -1;
            ClearEntries();
        }

        internal void Hide()
        {
            ClearEntries();
            _renderIndex = -1;
        }

        /// <summary>
        /// 每帧驱动。camera 为 null（菜单/加载）时调用方应改走屏幕歌词，这里直接清空。
        /// </summary>
        internal void Tick(MusicPlayer player, Camera camera, float nowSeconds)
        {
            Tick(player != null && player.Current != null ? player.Current.Key : null, camera, nowSeconds);
        }

        /// <summary>
        /// 外部时间轴版本：本地模式（跟着网易云/QQ 客户端）没有 AudioSource，
        /// 歌名与播放位置都由 LocalBridge 提供，所以这里只认 key 和秒数。
        /// </summary>
        /// <summary>上一帧真正画出来的那句（同步探针用，排查"时间对不上"时看它）。</summary>
        internal static string LastLineText = "";

        internal void Tick(string songKey, Camera camera, float nowSeconds)
        {
            if (camera == null || _lines.Count == 0)
            {
                Hide();
                return;
            }
            if (!EnsureFont())
            {
                ClearEntries();
                return;
            }

            // 换歌清空
            if (!string.IsNullOrEmpty(songKey) && _songKey != songKey)
            {
                _songKey = songKey;
                ClearEntries();
                _renderIndex = -1;
            }

            double positionMs = nowSeconds * 1000.0;
            int index = FindIndex(nowSeconds);
            if (index < 0)
            {
                Hide();
                return;
            }

            LrcLine line = _lines[index];
            double startMs = line.Time.TotalMilliseconds;
            double endMs = index + 1 < _lines.Count
                ? _lines[index + 1].Time.TotalMilliseconds
                : startMs + 5000.0;
            if (endMs <= startMs) endMs = startMs + 2500.0;

            if (positionMs >= endMs || string.IsNullOrWhiteSpace(line.Text))
            {
                Retire();
                Animate(camera, positionMs);
                return;
            }

            int styleSig = StyleSignature();
            if (styleSig != _styleSig)
            {
                // 换了样式：**清掉所有旧句**再重建。
                // 只重置 _renderKey 的话，屏上那几句会保留旧材质/旧颜色，
                // 看起来就是「点了样式没反应」，要等下一句才变。
                _styleSig = styleSig;
                ClearEntries();
                _renderIndex = -1;
            }

            LastLineText = line.Text;
            // key 以前是字符串拼接 "_songKey/index" —— 每帧都 new 一个字符串，
            // 60 帧就是每秒 60 次小对象分配，GC 一到就是掉帧尖峰。
            // 现在只比一句的整数下标：换歌时 SetLyrics() 会 ClearEntries()，_current 变 null，
            // 下面的 _current == null 分支照样会重生，语义完全一致。
            if (index != _renderIndex || (_current == null && Time.unscaledTime >= _retryAt))
            {
                Retire();
                _renderIndex = index;
                _retryAt = Time.unscaledTime + .8f;
                _current = Spawn(line, endMs, camera);
            }

            // 粒子特效已下线：面板里「歌词特效」那一排按用户要求删掉了（效果不好看、也没人用）。
            // 代码和 cfg 都还在，把 FxMode 改回读配置就能恢复。
            if (FxMode > 0)
            {
                if (_fx == null && _root != null) _fx = LyricFx.Create(_root.transform);
                if (_fx != null)
                {
                    float fxDist = (_current != null && _current.Dist > .01f) ? _current.Dist : Distance;
                    _fx.Tick(camera, fxDist, FxMode, index, positionMs, startMs, endMs, line.Text.Length);
                }
            }

            Animate(camera, positionMs);
        }

        // ───────────────────────────── 字体 ─────────────────────────────

        private bool EnsureFont()
        {
            // 面板换了字体：旧图集必须丢掉重建，否则字形还是旧的
            int rev = FontRevision;
            if (rev != _fontRev)
            {
                if (_fontRev >= 0)
                {
                    ReleaseFonts();
                    ClearEntries();
                    _renderIndex = -1;
                }
                _fontRev = rev;
            }

            if (_font != null) return true;
            if (_fontFailed) return false;

            try
            {
                _root = new GameObject("PeakMusic world lyrics");
                UnityEngine.Object.DontDestroyOnLoad(_root);

                // ① 用户指定的字体（cfg 的 [歌词] Font，可以是系统里任何已安装字体）。
                //    以前这里写死 msyh/simhei/simsun 四个文件名 —— 英文版 Windows 上一个都不存在，
                //    3D 歌词就整块失效；玩家自己装了好看的中文字体也用不上。
                string want = Plugin.LyricFont != null ? (Plugin.LyricFont.Value ?? "").Trim() : "";
                if (want.Length > 0)
                {
                    string path = FontLibrary.ResolveFile(want);
                    if (path == null)
                        Plugin.Log?.LogWarning(Tag + " 指定的字体「" + want + "」没装在系统里，改用自动选择");
                    else
                    {
                        var asset = FileFont(path);
                        if (asset != null && asset.material != null && asset.material.shader.isSupported)
                        {
                            _font = asset;
                            _mainFontFile = System.IO.Path.GetFileName(path);
                        }
                        else
                        {
                            if (asset != null) ReleaseAsset(asset);
                            Plugin.Log?.LogWarning(Tag + " 指定的字体「" + want + "」生成图集失败，改用自动选择");
                        }
                    }
                }

                // ② 自动：中 → 繁 → 日 → 韩，逐个试系统里真实存在的字体
                if (_font == null)
                {
                    string[] auto = FontLibrary.AutoFiles();
                    for (int i = 0; i < auto.Length && _font == null; i++)
                    {
                        var asset = FileFont(auto[i]);
                        if (asset == null) continue;
                        if (asset.material != null && asset.material.shader.isSupported)
                        {
                            _font = asset;
                            _mainFontFile = System.IO.Path.GetFileName(auto[i]);
                        }
                        else ReleaseAsset(asset);
                    }
                }

                if (_font == null) throw new InvalidOperationException("系统里没有能生成 SDF 图集的字体");

                _fontAssets.Add(_font);
                _fontAssets.Add(_font.material);

                // 主字体只有一个就是「韩文变方块」的病根：msyh.ttc 里没有任何韩文字形
                // （2026-11-12 实测 29,905 个字形，谚文一个都没有）。TMP 找不到字形就画白框，
                // 所以凡是雅黑没覆盖的语言都会这样。实测覆盖情况：
                //   雅黑：中 ✓ 英 ✓ 日假名 ✓ 俄 ✓ 希腊 ✓ ｜ 韩 ✗ 泰 ✗ 越南 ✗ 阿拉伯 ✗
                //   malgun.ttf：韩 ✓ 日 ✓ 俄 ✓（无中文）
                //   tahoma.ttf：泰 ✓ 越南 ✓ 阿拉伯 ✓ 俄 ✓ 英 ✓（无中日韩）
                BuildFallbacks();

                Plugin.Log?.LogInfo(Tag + " SDF 字体就绪（主 " + _mainFontFile + "）· 系统字体 "
                                    + FontLibrary.Count + " 个");
                return true;
            }
            catch (Exception e)
            {
                _fontFailed = true;
                _font = null;
                Plugin.Log?.LogWarning(Tag + " SDF 字体不可用，回退屏幕歌词：" + e.Message);
                return false;
            }
        }

        // 回退字体表在 FontLibrary.Fallbacks() 里（顺序 = 覆盖优先级）。
        // TMP 只有主字体缺字形时才往这里找，所以顺序不影响中文观感，只影响「哪种语言先命中」。

        /// <summary>把系统里的其它语言字体挂成 TMP 的回退链，缺字形时自动降级过去。</summary>
        private void BuildFallbacks()
        {
            var list = new List<TMP_FontAsset>();
            var got = new List<string>();
            try
            {
                string[] files = FontLibrary.Fallbacks();
                for (int i = 0; i < files.Length; i++)
                {
                    string file = files[i];
                    if (string.Equals(System.IO.Path.GetFileName(file), _mainFontFile,
                                      StringComparison.OrdinalIgnoreCase)) continue;
                    var asset = FileFont(file);
                    if (asset == null) continue;
                    if (asset.material == null || !asset.material.shader.isSupported)
                    {
                        ReleaseAsset(asset);
                        continue;
                    }

                    // 必须是动态图集：韩文一万多个字不可能预烘，用哪个补哪个
                    try { asset.atlasPopulationMode = AtlasPopulationMode.Dynamic; } catch { }
                    try { asset.isMultiAtlasTexturesEnabled = true; } catch { }

                    list.Add(asset);
                    _fontAssets.Add(asset);
                    if (asset.material != null) _fontAssets.Add(asset.material);
                    got.Add(System.IO.Path.GetFileName(file));
                }

                if (list.Count == 0)
                {
                    Plugin.Log?.LogWarning(Tag + " 没找到任何回退字体：日/韩/泰文可能显示成方块");
                    return;
                }

                _font.fallbackFontAssetTable = list;
                try { _font.isMultiAtlasTexturesEnabled = true; } catch { }
                Plugin.Log?.LogInfo(Tag + " 字体回退链：" + string.Join(" → ", got.ToArray())
                                    + "（韩文 / 泰文 / 越南文不会再显示成方块）");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning(Tag + " 设置字体回退链失败：" + e.Message);
            }
        }

        private static TMP_FontAsset FileFont(string nameOrPath)
        {
            // 注册表里的值可能是绝对路径（Win10 起「仅为当前用户安装」的字体在 LocalAppData）
            string path = System.IO.Path.IsPathRooted(nameOrPath)
                ? nameOrPath
                : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), nameOrPath);
            if (!System.IO.File.Exists(path)) return null;
            try
            {
                return TMP_FontAsset.CreateFontAsset(path, 0, 64, 8, GlyphRenderMode.SDFAA, 1024, 1024);
            }
            catch
            {
                return null;
            }
        }

        private static void ReleaseAsset(TMP_FontAsset asset)
        {
            foreach (var tex in asset.atlasTextures) if (tex != null) UnityEngine.Object.Destroy(tex);
            if (asset.material != null) UnityEngine.Object.Destroy(asset.material);
            UnityEngine.Object.Destroy(asset);
        }

        // ───────────────────────────── 生成 / 摆放 ─────────────────────────────

        private sealed class Entry
        {
            internal GameObject Root;
            internal TextMeshPro Main;
            internal TextMeshPro Translation;
            internal LyricAnimation Animation;
            internal TMP_MeshInfo[] Original;
            internal int[] CharacterToGrapheme = Array.Empty<int>();
            internal string LineText;
            internal Vector3 Anchor;
            internal float Dist;              // 平滑后的实际距离（0 = 还没算过）
            internal Material Mat;            // 主行材质（每句独立实例，能单独开关光晕）
            internal Material TransMat;       // 副行材质
            internal bool GlowOn;             // 当前是否开着光晕
            internal TextMeshPro Glow;        // 只画「唱到的字」的光晕层
            internal float[] LitT;            // 每个字符的演唱进度 0..1，发光层复用
            internal float[] JitterX;         // 散聚样式的稳定偏移（生成时算一次）
            internal float[] JitterY;
            internal float TargetDist;        // 遮挡探测缓存（10Hz 刷新）
            internal float NextProbe;         // 下次探测时间
            internal Vector3 Viewport;
            internal Quaternion Orientation;
            internal float Born;
            internal float SlideDir = 1f;   // 侧滑入的方向（左右交替）
            internal float Retired = -1;
            internal double LastPositionMs;
        }

        private Entry Spawn(LrcLine line, double endMs, Camera camera)
        {
            if (_root == null || _font == null) return null;

            string text = WrapText(StripPunct(Limit(line.Text, 160)));

            var e = new Entry
            {
                Root = new GameObject("PeakMusic lyric"),
                LineText = text,
                Animation = new LyricAnimation(text, line.Time.TotalMilliseconds, endMs - line.Time.TotalMilliseconds, false, true),
                Born = Time.unscaledTime,
                SlideDir = (_serial % 2 == 0) ? 1f : -1f,
            };
            e.Root.transform.SetParent(_root.transform, false);

            e.Main = MakeText(e.Root, "Original", text, 36f);
            e.Translation = MakeText(e.Root, "Translation", "", 36f * .72f);
            // 每句都是 new Material(...)，所以光晕可以按句单独开关
            e.Mat = e.Main.fontSharedMaterial;
            e.TransMat = e.Translation.fontSharedMaterial;

            // 主层不要整句光晕：光晕改由下面这层「逐字层」负责，
            // 否则没唱到的字也会被整句材质光晕糊亮（用户要的是"唱到才发光"）。
            if (e.Mat != null && e.Mat.HasProperty("_GlowColor"))
            {
                Color mc = e.Mat.GetColor("_GlowColor");
                mc.a = 0f;
                e.Mat.SetColor("_GlowColor", mc);
            }

            e.Glow = MakeText(e.Root, "Glow", text, 36f);
            var gm = e.Glow.fontSharedMaterial;
            if (gm != null && gm.HasProperty("_GlowColor"))
            {
                Color lc = LyricColors.Lit;
                gm.SetColor("_GlowColor", new Color(lc.r, lc.g, lc.b, 1f));
                if (gm.HasProperty("_GlowPower")) gm.SetFloat("_GlowPower", .62f);
                if (gm.HasProperty("_GlowOuter")) gm.SetFloat("_GlowOuter", .33f);
                // 发光层是同一批字的副本：再叠一层倒角/投影只会更脏，关掉
                gm.DisableKeyword("UNDERLAY_ON");
                if (gm.HasProperty("_GlowInner")) gm.SetFloat("_GlowInner", .08f);
                if (gm.HasProperty("_GlowOffset")) gm.SetFloat("_GlowOffset", .02f);
            }
            float ww = WorldWidth(camera);
            e.Main.rectTransform.sizeDelta = new Vector2(ww, 20f);
            e.Translation.rectTransform.sizeDelta = new Vector2(ww, 20f);
            float px = (Plugin.LyricSize != null ? Plugin.LyricSize.Value : 56f) * SizeScale;
            float worldH = CalibrateFont(camera, e.Main, e.Translation, px);
            e.Translation.transform.localPosition = new Vector3(0f, -worldH * .6f - .1f, 0f);

            // 发光层必须和主行同一字号、同一矩形、同一位置，之后每帧直接抄主行顶点
            e.Glow.fontSize = e.Main.fontSize;
            e.Glow.characterSpacing = e.Main.characterSpacing;
            e.Glow.rectTransform.sizeDelta = e.Main.rectTransform.sizeDelta;
            // CalibrateFont 会按 textBounds 再乘一个缩放系数（main.transform.localScale *= k），
            // 发光层必须跟着乘同一个系数，否则它比主行大一圈，两层叠在一起就是"两个字重影"。
            e.Glow.transform.localScale = e.Main.transform.localScale;
            e.Glow.transform.localPosition = new Vector3(0f, 0f, -.003f);   // 往镜头方向挪 3mm，避免 z-fighting
            e.Glow.color = Color.white;
            e.Glow.ForceMeshUpdate();

            // 基准色只设一次：主行留白（逐字色写在顶点上），副行用样式里的翻译色
            e.Main.color = Color.white;
            e.Translation.color = LyricColors.Sub;

            if (!Place(e, camera))
            {
                DestroyEntry(e);
                return null;
            }

            e.Original = e.Main.textInfo.CopyMeshInfoVertexData();
            int charCount = e.Main.textInfo.characterCount;
            e.CharacterToGrapheme = new int[charCount];
            e.LitT = new float[charCount];
            // 「散聚」样式每个字要两份稳定伪随机。字符数是固定的，生成时算一次就够 ——
            // 以前每帧每字算两次整数 hash（50 字 × 60 帧 = 每秒 6000 次）。
            e.JitterX = new float[charCount];
            e.JitterY = new float[charCount];
            for (int i = 0; i < charCount; i++)
            {
                e.JitterX[i] = Hash01(i) - .5f;
                e.JitterY[i] = Hash01(i + 977) - .5f;
            }
            for (int i = 0; i < e.CharacterToGrapheme.Length; i++)
            {
                int source = e.Main.textInfo.characterInfo[i].index;
                int grapheme = 0;
                while (grapheme + 1 < e.Animation.Graphemes.Length && e.Animation.Graphemes[grapheme + 1].SourceStart <= source) grapheme++;
                e.CharacterToGrapheme[i] = grapheme;
            }

            SetGlow(e, true);          // 新句 = 当前句，马上发光
            _entries.Add(e);
            while (_entries.Count > 4)
            {
                var old = _entries[0];
                _entries.RemoveAt(0);
                DestroyEntry(old);
            }
            return e;
        }

        private TextMeshPro MakeText(GameObject parent, string label, string value, float size)
        {
            var go = new GameObject(label);
            go.transform.SetParent(parent.transform, false);
            var text = go.AddComponent<TextMeshPro>();
            text.font = _font;
            text.fontSize = size;
            text.extraPadding = true;
            text.text = value;
            text.richText = false;
            text.textWrappingMode = TextWrappingModes.Normal;
            text.maxVisibleLines = 3;
            text.overflowMode = TextOverflowModes.Ellipsis;
            text.alignment = TextAlignmentOptions.Center;
            text.characterSpacing = CharSpacing;   // 字距：样式维度之一，决定「紧凑/疏朗」的呼吸感
            text.color = Color.white;
            text.raycastTarget = false;

            var material = new Material(text.fontSharedMaterial);
            text.fontSharedMaterial = material;
            TryOverlayShader(material);          // 必须在 ApplyMaterial 之前：换 shader 会清掉材质属性
            ApplyMaterial(material, isDark: false);

            text.UpdateMeshPadding();
            text.renderer.shadowCastingMode = ShadowCastingMode.Off;
            text.renderer.receiveShadows = false;
            return text;
        }

        /// <summary>
        /// 出生时只定「屏幕位置」（viewport）：世界锚点由 <see cref="Animate"/> 每帧跟随
        /// 相机重算（带避墙拉近），所以不再做射线挑点。旧逻辑每句歌词扫一轮射线，
        /// 贴墙时还刷「候选点全被岩壁挡住」日志，而那个世界锚点下一句就被人甩掉了。
        /// </summary>
        private bool Place(Entry e, Camera camera)
        {
            foreach (var point in Candidates(camera, _serial++))
            {
                SetAt(e, camera.ViewportToWorldPoint(point), camera, point);
                return true;
            }
            return false;
        }

        private static void SetAt(Entry e, Vector3 p, Camera camera, Vector3 viewport)
        {
            e.Root.transform.position = p;
            e.Orientation = Face(p, camera);
            e.Root.transform.rotation = e.Orientation;
            e.Anchor = p;
            e.Viewport = viewport;
        }

        /// <summary>
        /// 相机左右两侧生成候选点。位置由 cfg 的 [歌词] 段控制：
        /// <c>Side</c>（0=两侧交替 1=只左 2=只右）、<c>Edge</c>（句子中心离屏幕横向的比例）、
        /// <c>Y</c>（垂直位置）。原版是按 FOV 散布角算，这里改成按屏幕比例，位置可精确调。
        /// </summary>
        private IEnumerable<Vector3> Candidates(Camera camera, int serial)
        {
            // 居中滚动模式：只有一个落点（屏幕中央），不左右分侧
            if (LyricLayout == 1)
            {
                var pc = camera.ViewportToWorldPoint(new Vector3(.5f, Mathf.Clamp(LyricY, .2f, .8f), Distance));
                yield return camera.WorldToViewportPoint(pc);
                yield break;
            }

            float edge = Mathf.Clamp(LyricEdge, 0.12f, 0.40f);
            float y = Mathf.Clamp(LyricY, 0.10f, 0.90f);

            int sideMode = LyricSide;           // 0 两侧交替 / 1 只左 / 2 只右
            float side = 1f;
            if (sideMode == 0) side = (serial % 2 == 0) ? -1f : 1f;
            else if (sideMode == 1) side = -1f;
            else side = 1f;

            // 水平：句子中心在屏幕 edge 比例处（左=1-edge，右=edge 对称）。给两个候选：主位 + 略靠内
            float leftX = 1f - edge;
            float rightX = edge;
            float[] xs = side < 0
                ? new[] { leftX, leftX - 0.08f }
                : new[] { rightX, rightX + 0.08f };

            foreach (float x in xs)
            {
                float vx = Mathf.Clamp(x, 0.05f, 0.95f);
                var point = camera.ViewportToWorldPoint(new Vector3(vx, y, Distance));
                yield return camera.WorldToViewportPoint(point);
            }
        }

        private static Quaternion Face(Vector3 point, Camera camera)
        {
            var ray = point - camera.transform.position;
            return ray.sqrMagnitude > .000001f
                ? Quaternion.LookRotation(ray, camera.transform.up)
                : camera.transform.rotation;
        }

        // ───────────────────────────── 材质 ─────────────────────────────

        /// <summary>字面颜色（面板配色）+ SDF 描边。</summary>
        private static bool _overlayTried, _overlayOk;
        private static Shader _overlayShader;

        /// <summary>是否开了「穿透地形」。</summary>
        private static bool ThroughWallOn
        {
            get { try { return Plugin.LyricThroughWall != null && Plugin.LyricThroughWall.Value; } catch { return false; } }
        }

        /// <summary>
        /// 穿透地形：TMP 的 Mobile 变体没有 `_ZTest`，必须换成完整的 Distance Field 变体，
        /// 把深度测试设成 Always（=8）并压到最后渲染，字幕就永远不会被山体/墙挡住。
        /// 参考的是琳玲那套悬浮字幕的做法；换不到 shader 就静默回退到「贴墙拉近」。
        /// </summary>
        private static void TryOverlayShader(Material m)
        {
            if (!ThroughWallOn || m == null) return;

            if (!_overlayTried)
            {
                _overlayTried = true;
                try
                {
                    _overlayShader = Shader.Find("TextMeshPro/Distance Field");
                    _overlayOk = _overlayShader != null && _overlayShader.isSupported;
                }
                catch { _overlayOk = false; }
                Plugin.Log?.LogInfo("[歌词3D] 穿透地形：" + (_overlayOk
                    ? "已切到 TextMeshPro/Distance Field（_ZTest=Always）"
                    : "shader 不可用，回退「贴墙自动拉近」"));
            }
            if (!_overlayOk) return;

            try
            {
                if (m.shader != _overlayShader) m.shader = _overlayShader;
                if (m.HasProperty("_ZTest")) m.SetFloat("_ZTest", 8f);   // CompareFunction.Always
                m.renderQueue = 5000;
            }
            catch { }
        }

        private static void ApplyMaterial(Material material, bool isDark)
        {
            // 亮度过 1 时颜色会往白里走（分量夹到 1），视觉上就是"更亮更抓眼"。
            float brightness = Brightness;
            var white = new Color(1f, 1f, 1f, 1f);

            Color face = LyricColors.Lit;
            Color bright = new Color(Mathf.Clamp01(face.r * brightness),
                                     Mathf.Clamp01(face.g * brightness),
                                     Mathf.Clamp01(face.b * brightness), 1f);
            // 字色现在由「逐字顶点色」驱动（见 Animate），材质 _FaceColor 必须留白：
            // 否则顶点色会被材质再乘一遍，逐字渐变看不出来、整体还会发灰。
            // bright 保留，只用于给光晕染色。
            if (material.HasProperty("_FaceColor")) material.SetColor("_FaceColor", white);
            if (material.HasProperty("_Color")) material.SetColor("_Color", white);
            // 字太细是"看着发暗"的一大原因：远处笔画只有一两像素，肉眼读到的全是衰减过的光晕。
            // 加粗笔画（SDF dilate）+ 下面亮度联动未唱字透明度，才接近参考模组那种"整句都亮"。
            if (material.HasProperty("_FaceDilate"))
                material.SetFloat("_FaceDilate", LyricWeight >= 1 ? .42f : .24f);

            if (material.HasProperty("_OutlineWidth"))
            {
                material.SetFloat("_OutlineWidth", .16f);
                material.EnableKeyword("OUTLINE_ON");
            }
            if (material.HasProperty("_OutlineColor"))
                material.SetColor("_OutlineColor", isDark ? new Color(1f, 1f, 1f, .8f) : new Color(.01f, .015f, .025f, .95f));

            // 白色光晕：参考模组（Dango/Folia）里歌词"亮"的一大半来自这层 glow，
            // 不只是字面颜色。TMP SDF 支持 GLOW_ON，没有该属性就静默跳过。
            try
            {
                bool glowOn = Plugin.LyricGlow != null && Plugin.LyricGlow.Value;
                float gs = Plugin.LyricGlowStrength != null ? Plugin.LyricGlowStrength.Value : 0.3f;
                if (material.HasProperty("_GlowColor"))
                {
                    if (glowOn && gs > 0.01f)
                    {
                        // TMP 里 _GlowColor.a 就是光晕强度
                        material.EnableKeyword("GLOW_ON");
                        material.SetColor("_GlowColor", new Color(bright.r, bright.g, bright.b, Mathf.Clamp01(gs)));
                        // 半径拉到 0.5、衰减压到 0.28：光晕才会「散出去」形成发光感。
                        // 之前 0.35 / 0.55 太紧，只在字边糊一圈，看着还是暗。
                        if (material.HasProperty("_GlowPower")) material.SetFloat("_GlowPower", 0.28f);
                        if (material.HasProperty("_GlowInner")) material.SetFloat("_GlowInner", 0.02f);
                        if (material.HasProperty("_GlowOuter")) material.SetFloat("_GlowOuter", 0.50f);
                        if (material.HasProperty("_GlowOffset")) material.SetFloat("_GlowOffset", 0.05f);
                    }
                    else material.DisableKeyword("GLOW_ON");
                }
            }
            catch { }

            // r35：贴脸岩壁虽然会把歌词拉近，但拉近点仍可能在墙里；shader 支持 _ZTest 就
            // 永远置前，保证歌词不被地形吞掉。不支持就靠避墙拉近兜底（HasProperty 护栏）。
            if (material.HasProperty("_ZTest"))
                material.SetFloat("_ZTest", (float)CompareFunction.Always);

            // ── 立体感 ──
            // 「像纸片贴在屏幕上」有两个原因：正对相机（没有透视）+ 字形完全平。
            // 这里做字形这一半：SDF 自带 bevel（斜切倒角）+ underlay（投影），
            // 不增加任何网格、不增加 draw call，纯改材质参数。
            int depth = DepthLevel;
            if (depth > 0)
            {
                if (material.HasProperty("_Bevel"))
                {
                    material.EnableKeyword("BEVEL_ON");
                    material.SetFloat("_Bevel", depth == 2 ? .90f : .45f);
                    if (material.HasProperty("_BevelType")) material.SetFloat("_BevelType", depth == 2 ? 2f : 1f);
                    if (material.HasProperty("_BevelWidth")) material.SetFloat("_BevelWidth", .18f);
                    if (material.HasProperty("_BevelOffset")) material.SetFloat("_BevelOffset", .03f);
                    if (material.HasProperty("_BevelClamp")) material.SetFloat("_BevelClamp", .65f);
                    if (material.HasProperty("_BevelRoundness")) material.SetFloat("_BevelRoundness", .35f);
                }

                if (material.HasProperty("_UnderlayColor"))
                {
                    material.EnableKeyword("UNDERLAY_ON");
                    material.SetColor("_UnderlayColor", new Color(0f, 0f, 0f, depth == 2 ? .55f : .35f));
                    if (material.HasProperty("_UnderlayOffsetX")) material.SetFloat("_UnderlayOffsetX", .22f);
                    if (material.HasProperty("_UnderlayOffsetY")) material.SetFloat("_UnderlayOffsetY", -.22f);
                    if (material.HasProperty("_UnderlayDilate")) material.SetFloat("_UnderlayDilate", .12f);
                    if (material.HasProperty("_UnderlaySoftness")) material.SetFloat("_UnderlaySoftness", .18f);
                }
            }
        }

        // ───────────────────────────── 动画 ─────────────────────────────

        private void Retire()
        {
            if (_current == null) return;
            _current.Retired = Time.unscaledTime;
            SetGlow(_current, false);   // 发光必须跟着"当前句"走，上一句立刻熄掉
            _current = null;
        }

        /// <summary>
        /// 光晕开关。TMP 里 `_GlowColor.a` 就是光晕强度，每句一个材质实例，所以能单独控制。
        /// 规则：同一时刻只有「当前句」发光；其余句子一律普通白字、不加任何光晕。
        /// </summary>
        private static void SetGlow(Entry e, bool on)
        {
            if (e == null || e.Glow == null) return;
            bool want = on && (Plugin.LyricGlow == null || Plugin.LyricGlow.Value);
            e.GlowOn = want;
            if (e.Glow.gameObject.activeSelf != want) e.Glow.gameObject.SetActive(want);
        }

        private static void SetGlowAlpha(Material m, float a)
        {
            if (m == null || !m.HasProperty("_GlowColor")) return;
            Color c = m.GetColor("_GlowColor");
            if (Mathf.Abs(c.a - a) < .001f) return;
            c.a = a;
            m.SetColor("_GlowColor", c);
        }

        /// <summary>
        /// 样式指纹：颜色/字距/运动方式任何一项变了就换一个值。
        /// 用它强制重生当前这句 —— 不然点了新样式要等下一句才看得出来。
        /// </summary>
        private static int StyleSignature()
        {
            int h = 17;
            try
            {
                h = h * 31 + (Plugin.LyricColorLit != null ? Plugin.LyricColorLit.Value.GetHashCode() : 0);
                h = h * 31 + (Plugin.LyricColorRest != null ? Plugin.LyricColorRest.Value.GetHashCode() : 0);
                h = h * 31 + (Plugin.LyricColorSub != null ? Plugin.LyricColorSub.Value.GetHashCode() : 0);
                h = h * 31 + MotionMode;
                h = h * 31 + WarpMode;
                h = h * 31 + Mathf.RoundToInt(CharSpacing * 10f);
                h = h * 31 + Mathf.RoundToInt(SizeScale * 100f);
                // 字号必须进指纹：否则点了「字号」只写 cfg，屏幕上的这句要等下一句才变，
                // 用户连点几次都看不到反应，会以为按钮坏了。
                h = h * 31 + (Plugin.LyricSize != null ? Plugin.LyricSize.Value : 0);
                h = h * 31 + LyricWeight;
                h = h * 31 + Mathf.RoundToInt(RestAlpha * 100f);
                h = h * 31 + DepthLevel;
                h = h * 31 + (Plugin.LyricFont != null && Plugin.LyricFont.Value != null
                              ? Plugin.LyricFont.Value.GetHashCode() : 0);
            }
            catch { }
            return h;
        }

        /// <summary>上一帧歌词层（Animate）耗时，毫秒。性能日志用。</summary>
        internal static float LastTickMs;

        private void Animate(Camera camera, double positionMs)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                var e = _entries[i];
                float time = Time.unscaledTime;
                float age = time - e.Born;
                float leaving = e.Retired < 0 ? 0f : (time - e.Retired) / .55f;

                if (e.Retired >= 0 && leaving >= 1f)
                {
                    _entries.RemoveAt(i);
                    DestroyEntry(e);
                    continue;
                }

                // 入场：0=淡入（位置不动）1=上浮（原默认）2=弹入（带回弹过冲）
                float enterT = Mathf.Clamp01(age / .26f);
                float enterEase = 1f - (1f - enterT) * (1f - enterT);   // ease-out

                // 退场：0=淡出 1=上飘+后退（原默认）2=缩小
                float exitT = e.Retired >= 0 ? Mathf.Clamp01(leaving / .55f) : 0f;
                float exitEase = exitT * exitT;                          // ease-in，先慢后快

                int enterKind = EnterAnim;
                int exitKind = ExitAnim;

                // 四种入场：0 淡入（不动）1 上浮 2 弹入（过冲回落）3 侧滑入
                float enterAlpha = enterKind == 0 ? enterT : enterEase;
                float enterScale;
                if (enterKind == 2)
                    enterScale = 0.70f + 0.30f * enterEase + 0.07f * Mathf.Sin(enterT * Mathf.PI);
                else
                    enterScale = 0.94f + 0.06f * enterEase;
                float enterUp = enterKind == 1 ? 0.22f * (1f - enterEase)
                              : (enterKind == 2 ? 0.08f * (1f - enterEase) : 0f);
                float enterSide = enterKind == 3 ? 1.10f * (1f - enterEase) * e.SlideDir : 0f;

                // 四种退场：0 淡出 1 上飘+后退 2 缩小 3 下坠
                float exitAlpha = 1f - exitEase;
                float exitScale = exitKind == 2 ? 1f - 0.30f * exitEase : 1f + 0.07f * exitEase;
                float exitUp = exitKind == 1 ? 0.75f * exitEase
                             : (exitKind == 3 ? -0.85f * exitEase : 0f);
                float exitBack = exitKind == 1 ? 0.55f * exitEase : 0.12f * exitEase;

                // 段落级微动：呼吸（整句缩放起伏）+ 漂移（整句横向游走）。
                // 相位按出生时间错开，否则前后两句会像做广播体操一样同频起伏。
                float phase = (time - e.Born) * 2f * Mathf.PI * .22f;
                float breath = Breath;
                float drift = Drift;

                // 整句运动方式：这是「每套样式不只是换颜色」的核心 ——
                // 同一句词，换样式时它是呼吸、漂浮、摇摆、心跳还是潮汐，全都不一样。
                float bobUp = 0f, bobSide = 0f, roll = 0f;
                switch (MotionMode)
                {
                    case 1:   // 呼吸：缓慢缩放
                        if (breath > .001f) enterScale *= 1f + breath * Mathf.Sin(phase);
                        break;
                    case 2:   // 漂浮：上下缓慢起伏
                        bobUp = drift * 8f * Mathf.Sin(phase * .8f);
                        break;
                    case 3:   // 摇摆：左右游走 + 轻微倾斜
                        bobSide = drift * 6f * Mathf.Sin(phase * .6f);
                        roll = drift * 55f * Mathf.Sin(phase * .6f + .4f);
                        break;
                    case 4:   // 心跳：双拍脉冲（快起慢落）
                        if (breath > .001f)
                            enterScale *= 1f + breath * 1.6f *
                                (Mathf.Sin(phase * 1.7f) + .5f * Mathf.Sin(phase * 3.4f));
                        break;
                    case 5:   // 潮汐：慢速上下 + 极缓倾斜
                        bobUp = drift * 5f * Mathf.Sin(phase * .45f);
                        roll = drift * 30f * Mathf.Sin(phase * .45f);
                        break;
                }

                float alpha = enterAlpha * exitAlpha;
                float scale = enterScale * exitScale;

                // 同一时刻只允许一句发光：新句上来后，旧句在淡出期间也必须熄掉光晕
                bool isCurrent = ReferenceEquals(e, _current);
                SetGlow(e, isCurrent);      // 只有当前句的发光层可见

                // 居中滚动：每句随时间缓慢上移，新句在中央出现、老句被顶上去并压暗。
                // 这就是「随播放进度滚动 + 当前句高亮」的最小实现，不需要额外的行槽位管理。
                if (LyricLayout == 1)
                {
                    float slot = Mathf.Clamp((time - e.Born) * 1.5f, 0f, 3f);
                    float baseY = Mathf.Clamp(LyricY, .2f, .8f);
                    e.Viewport = new Vector3(.5f, baseY + slot * .085f, e.Viewport.z);
                    if (!ReferenceEquals(e, _current))
                    {
                        scale *= .90f;
                        alpha *= .55f;
                    }
                }

                // r35：每帧把锚点按屏幕配置重算到「当前相机」前方（跟随视角和位移）。
                // 之前锚点出生时定死在世界坐标上，PEAK 里人一直在爬/走，歌词很快就落到
                // 身后（客机反馈「歌词老是出现在身后、跟不上」就是这个）。现在等价于
                // 固定屏幕位置的 3D 字幕，遇岩壁自动拉近 + 等比缩放，屏幕字号恒定。
                float dist = Distance;
                Vector3 origin = camera.transform.position;
                Vector3 dir = camera.ViewportToWorldPoint(e.Viewport) - origin;
                if (dir.sqrMagnitude > 0.0001f)
                {
                    dir.Normalize();

                    // ── 贴墙避让：把歌词压到相机与岩壁之间 ──
                    // 参考做法就一句话「距离拉近、字号拉大」：距离按射线命中点收，
                    // 再把整体缩放乘 dist/Distance —— 屏幕上的字号不变，只是离眼睛更近。
                    float target = Distance;
                    if (Occlusion && !ThroughWallOn)
                    {
                        // 每句每秒只探 10 次：物理查询不用每帧做，10Hz 足够跟得上走路/攀爬，
                        // 而 4 句同时在场时这一项从「每帧 8 次查询」降到「每帧不到 1 次」。
                        if (time >= e.NextProbe)
                        {
                            e.NextProbe = time + .10f;

                            // 1) 用 SphereCast 而不是 Raycast：PEAK 的岩壁是碎面，
                            //    一条细射线经常从两块石头之间穿过去，于是判定"没挡"。
                            const float probe = .16f;
                            RaycastHit hit;
                            if (Physics.SphereCast(origin, probe, dir, out hit, Distance + probe,
                                    WallMask, QueryTriggerInteraction.Ignore))
                            {
                                target = hit.distance - probe - .22f;
                                if (target < MinDist)
                                    // 墙就贴在脸上，没有 0.6m 的空间了：继续往近处压，但别越过近裁剪面
                                    target = Mathf.Max(.32f, hit.distance - .18f);
                                target = Mathf.Min(target, Distance);
                            }

                            // 2) 第三人称镜头被地形顶进墙里时，SphereCast 起点已在碰撞体内部，
                            //    会一路穿到底。用一个极小的球探一下：贴脸就直接压到鼻尖前。
                            if (Physics.CheckSphere(origin, .30f, WallMask, QueryTriggerInteraction.Ignore))
                                target = Mathf.Min(target, .85f);

                            e.TargetDist = target;
                        }
                        else if (e.TargetDist > .01f)
                            target = e.TargetDist;      // 两次探测之间用缓存值
                    }

                    // 3) 平滑：往里收要快（穿墙一帧都难看），往外放慢（爬墙时不会忽远忽近）
                    if (e.Dist <= .01f) e.Dist = target;
                    else
                    {
                        float k = target < e.Dist ? 20f : 4f;
                        e.Dist = Mathf.Lerp(e.Dist, target,
                                            1f - Mathf.Exp(-k * UnityEngine.Time.unscaledDeltaTime));
                        if (Mathf.Abs(e.Dist - target) < .01f) e.Dist = target;
                    }

                    dist = e.Dist;
                    e.Anchor = origin + dir * dist;
                }
                e.Root.transform.rotation = Face(e.Anchor, camera);
                int depthLv = DepthLevel;
                if (depthLv > 0)
                {
                    // 只正对相机 = 无论怎么转头都保持屏幕平行 → 大脑判定"贴在屏幕上"。
                    // 给一点倾角：文字平面像立在场景里的牌子，转视角时出现透视与视差。
                    // 幅度很小（轻 6° / 强 13°），不会影响读字。
                    float t = depthLv == 2 ? 13f : 6f;
                    float side = e.Viewport.x < .5f ? -1f : 1f;   // 左侧字往中心仰，右侧反之
                    e.Root.transform.rotation = e.Root.transform.rotation
                        * Quaternion.Euler(-t * .35f, side * t * .75f, 0f);
                }
                if (Mathf.Abs(roll) > .05f)
                    e.Root.transform.rotation = e.Root.transform.rotation * Quaternion.AngleAxis(roll, Vector3.forward);

                // 位移轨迹：按入场/退场类型组合（上浮/弹入/侧滑 有位移，淡入/淡出 不动），
                // 再叠一层缓慢漂移，让静止的那几秒也不至于像贴纸
                Vector3 motion = camera.transform.up * (exitUp - enterUp)
                               + camera.transform.right * enterSide
                               + camera.transform.forward * exitBack;
                if (drift > 0.001f)
                    motion += camera.transform.right * (drift * Mathf.Sin(phase * .55f));
                if (Mathf.Abs(bobUp) > .0001f) motion += camera.transform.up * bobUp;
                if (Mathf.Abs(bobSide) > .0001f) motion += camera.transform.right * bobSide;
                e.Root.transform.position = e.Anchor + motion;
                e.Root.transform.localScale = Vector3.one * (scale * dist / Distance);

                if (e.Retired < 0) e.LastPositionMs = positionMs;

                // 逐字双色基准：未唱 = Rest（压暗 + 半透），唱到哪个字往 Lit 过渡。
                // 参考实现是「没唱到就不画」，这里整句常显、逐字点亮 —— 观感更接近卡拉OK，
                // 画面里也不会出现半句空白。smoothstep 让过渡不发跳。
                // 透明度已经接近 0：这一帧不用再算逐字颜色/顶点（TMP 的 UpdateVertexData 最贵）。
                // 副行的 alpha 仍要跟着走，否则它会停在 1，主行还在淡入时翻译已经实心了。
                if (alpha <= .012f)
                {
                    if (e.Translation.text.Length > 0) e.Translation.alpha = alpha;
                    if (e.GlowOn) SetGlow(e, false);
                    continue;
                }

                float brightNow = Brightness;
                // 关键：**不能**直接乘 RGB 再 Clamp01。
                // FFB8D8 × 1.6 = (255,255,255) —— 三个通道一起顶死，粉色就变成纯白；
                // 这就是「换什么样式都像纯白」「重启后觉得回到纯白」的真正原因。
                Color litRgb = Boost(LyricColors.Lit, brightNow);
                Color restRgb = Boost(LyricColors.Rest, brightNow);
                float restAlpha = RestAlpha;

                var info = e.Main.textInfo;
                for (int m = 0; m < info.meshInfo.Length && m < e.Original.Length; m++)
                    Array.Copy(e.Original[m].vertices, info.meshInfo[m].vertices, Math.Min(e.Original[m].vertices.Length, info.meshInfo[m].vertices.Length));

                for (int c = 0; c < info.characterCount; c++)
                {
                    var ch = info.characterInfo[c];
                    if (!ch.isVisible || c >= e.CharacterToGrapheme.Length) continue;
                    int mesh = ch.materialReferenceIndex;
                    int vi = ch.vertexIndex;
                    if (mesh >= e.Original.Length) continue;

                    var frame = e.Animation.Sample(
                        e.CharacterToGrapheme[c],
                        e.Retired >= 0 ? e.LastPositionMs : positionMs,
                        time,
                        wordPop: true,
                        wiggle: Wiggle,
                        wiggleFrequencyHz: WiggleFrequency,
                        wiggleAmount: WiggleAmount);

                    float litT = (float)frame.Alpha;
                    litT = litT * litT * (3f - 2f * litT);                      // smoothstep
                    // 只有当前句的发光层会用到 LitT；淡出中的旧句写它纯属浪费（每帧几十次写）
                    if (isCurrent && e.LitT != null && c < e.LitT.Length) e.LitT[c] = litT;
                    Color rgb = Color.Lerp(restRgb, litRgb, litT);
                    // 不是当前句 → 普通白字，不参与配色/脉冲
                    if (!isCurrent) rgb = Color.white;

                    // 逐字光晕脉冲：对照「每个字自己冲高一次再回落」的做法 ——
                    // 字刚唱到的那 ~160ms 亮度抬高，之后回常态。这是它那种「字在发光」的关键。
                    // Pop 直接由缩放曲线反推（1.55→1 的回弹），不去读 Core 的新字段 ——
                    // 部署的 Core 是另一份构建，加字段会让插件在运行时抛 MissingFieldException。
                    float pulse = GlowPulse;
                    float pop = Mathf.Clamp01(((float)frame.Scale - 1f) / .55f);
                    if (isCurrent && pulse > .001f && pop > .001f)
                    {
                        float k = 1f + pulse * pop * .8f;
                        rgb = new Color(Mathf.Clamp01(rgb.r * k), Mathf.Clamp01(rgb.g * k),
                                        Mathf.Clamp01(rgb.b * k), 1f);
                    }
                    // 亮度不只调颜色，还要抬"未唱字"的不透明度：
                    // 否则亮度调到 1.9，颜色早就顶到纯白了，看着还是灰 —— 因为暗的是 alpha。
                    // 1.0 时完全按样式设定；1.9 时约 +0.5，整句基本都实心，就是参考模组那种观感。
                    float floorA = Mathf.Clamp01(restAlpha + (brightNow - 1f) * .55f);
                    float visible = alpha * Mathf.Lerp(floorA, 1f, litT);

                    // ── 逐字排布（参考开源播放器的文字 PV 样式，用顶点位移复刻）──
                    // 单位是「字号像素」，下面会乘 pixel 换成世界单位。
                    int warp = WarpMode;
                    float wx = 0f, wy = 0f, wr = 0f;
                    if (warp != 0 && info.characterCount > 1)
                    {
                        float tn = (float)c / (info.characterCount - 1);
                        float ctr = tn * 2f - 1f;                 // -1 左 … +1 右
                        switch (warp)
                        {
                            case 1:   // 弧排：中间高两边低，字顺着弧线偏角度
                                wy = -22f * (1f - ctr * ctr);
                                wr = ctr * 9f;
                                break;
                            case 2:   // 阶梯：每两个字一组上下错开
                                wy = ((c / 2) % 2 == 0) ? 9f : -9f;
                                break;
                            case 3:   // 散聚：未唱时散布，唱到哪个字哪个字归位
                            {
                                float j1 = (e.JitterX != null && c < e.JitterX.Length) ? e.JitterX[c] : 0f;
                                float j2 = (e.JitterY != null && c < e.JitterY.Length) ? e.JitterY[c] : 0f;
                                float spread = (1f - litT) * 30f;
                                wx = j1 * spread;
                                wy = j2 * spread;
                                wr = j1 * spread * .55f;
                                break;
                            }
                            case 4:   // 斜行：整行向右下倾斜
                                wy = ctr * 16f;
                                wr = -6f;
                                break;
                            case 5:   // 波浪：正弦起伏，相位随时间推进
                                wy = Mathf.Sin(c * .85f + time * 2.2f) * 7f;
                                break;
                        }
                    }
                    var original = e.Original[mesh].vertices;
                    var vertices = info.meshInfo[mesh].vertices;
                    var colors = info.meshInfo[mesh].colors32;

                    Vector3 centre = (original[vi] + original[vi + 2]) * .5f;
                    var rotation = Quaternion.Euler(0f, 0f, (float)(frame.Rotation * 180 / Math.PI) + wr);
                    float pixel = .03f * 1f * (Distance / 6.5f);

                    for (int k = 0; k < 4; k++)
                    {
                        vertices[vi + k] = centre + rotation * ((original[vi + k] - centre) * (float)frame.Scale)
                            + new Vector3(((float)frame.XOffset + wx) * pixel,
                                          ((float)frame.YOffset + wy) * pixel, 0f);
                        colors[vi + k] = new Color(rgb.r, rgb.g, rgb.b, Mathf.Clamp01(visible));
                    }
                }
                e.Main.UpdateVertexData(TMP_VertexDataUpdateFlags.Vertices | TMP_VertexDataUpdateFlags.Colors32);
                UpdateGlowLayer(e, alpha, isCurrent);

                // 注意：原来这里每帧 e.Main.color = Color.white 会把上面写好的逐字色洗掉，
                // 所以主行/副行的基准色改成出生时设一次，这里只驱动副行透明度。
                if (e.Translation.text.Length > 0) e.Translation.alpha = alpha;
            }

            LastTickMs = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - t0)
                                 * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
        }

        /// <summary>
        /// 逐字发光：直接抄主行的顶点（保证位置/缩放/排布完全一致），
        /// 只把「已经唱到」的字的 alpha 打开 —— 没唱到的字在这一层是全透明的，
        /// 所以画面里只有唱过的那几个字带光晕，和参考效果一致。
        /// </summary>
        private static void UpdateGlowLayer(Entry e, float alpha, bool on)
        {
            if (e == null || e.Glow == null) return;
            if (!on || alpha <= .012f)
            {
                if (e.Glow.gameObject.activeSelf) e.Glow.gameObject.SetActive(false);
                return;
            }
            if (!e.Glow.gameObject.activeSelf) e.Glow.gameObject.SetActive(true);

            var mi = e.Main.textInfo;
            var gi = e.Glow.textInfo;
            int n = Mathf.Min(mi.meshInfo.Length, gi.meshInfo.Length);
            for (int m = 0; m < n; m++)
            {
                var src = mi.meshInfo[m].vertices;
                var dst = gi.meshInfo[m].vertices;
                Array.Copy(src, dst, Math.Min(src.Length, dst.Length));
            }

            // 颜色只换算一次：以前每个字都 new Color32(r*255, g*255, b*255, a)，
            // 一帧几十个字就是几十次浮点乘 + 两次转换。
            Color32 baseCol = LyricColors.Lit;
            for (int c = 0; c < gi.characterCount; c++)
            {
                var ch = gi.characterInfo[c];
                if (!ch.isVisible) continue;
                int mesh = ch.materialReferenceIndex;
                if (mesh < 0 || mesh >= gi.meshInfo.Length) continue;
                var cols = gi.meshInfo[mesh].colors32;
                int vi = ch.vertexIndex;
                if (vi < 0 || vi + 3 >= cols.Length) continue;

                float k = (e.LitT != null && c < e.LitT.Length) ? e.LitT[c] : 0f;
                int ai = (int)(k * alpha * 255f + .5f);
                if (ai < 0) ai = 0; else if (ai > 255) ai = 255;
                var cc = baseCol;
                cc.a = (byte)ai;
                for (int q = 0; q < 4; q++) cols[vi + q] = cc;
            }
            e.Glow.UpdateVertexData(TMP_VertexDataUpdateFlags.Vertices | TMP_VertexDataUpdateFlags.Colors32);
        }

        /// <summary>单行超过这个字数就考虑拆两行。</summary>
        private const int WrapThreshold = 14;

        private static bool IsBreakChar(char c)
        {
            switch (c)
            {
                case '，': case '。': case '、': case '；': case '：':
                case ',': case '.': case ';': case ':':
                case '!': case '?': case '！': case '？':
                case ' ': case '\t': case '　':
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// 长句拆两行。为什么要自己拆：我们的文字矩形是按「整句撑开」算的，
        /// TMP 的自动换行永远不触发，长句就会横向拉得很长（19 套样式里弧排/斜行更明显）。
        /// 只在句子中点 ±5 个字符内找标点或空格断开；找不到就原样返回，绝不硬切。
        /// 换行符在 TMP 里没有可见字形，逐字映射会自然跳过它，不影响逐字点亮。
        /// </summary>
        private static string WrapText(string text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            if (Plugin.LyricWrap == null || !Plugin.LyricWrap.Value) return text;

            int[] starts = StringInfo.ParseCombiningCharacters(text);
            if (starts.Length <= WrapThreshold) return text;

            int mid = starts.Length / 2;
            int best = -1;
            for (int d = 0; d <= 5 && best < 0; d++)
            {
                int a = mid - d, b = mid + d;
                if (a > 0 && a < starts.Length - 1 && IsBreakChar(text[starts[a]])) best = a;
                else if (d > 0 && b > 0 && b < starts.Length - 1 && IsBreakChar(text[starts[b]])) best = b;
            }
            if (best <= 0) return text;

            int cut = starts[best];
            string head, tail;
            if (IsBreakChar(text[cut]) && text[cut] != ' ' && text[cut] != '　' && text[cut] != '\t')
            {
                head = text.Substring(0, cut + 1);      // 标点留在第一行末尾
                tail = text.Substring(cut + 1);
            }
            else
            {
                head = text.Substring(0, cut);          // 空格断开，空格不要
                tail = text.Substring(cut);
            }
            head = head.Trim();
            tail = tail.Trim();
            if (head.Length == 0 || tail.Length == 0) return text;
            return head + "\n" + tail;
        }

        /// <summary>可选：去掉标点。默认关，歌词保持原样。</summary>
        private static string StripPunct(string text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            if (Plugin.LyricStripPunct == null || !Plugin.LyricStripPunct.Value) return text;

            const string drop = "，。、；：！？,.;:!?“”‘’\"'（）()【】[]《》〈〉<>—-…·";
            var sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
                if (drop.IndexOf(text[i]) < 0) sb.Append(text[i]);
            return sb.ToString();
        }

        private static string Limit(string text, int elements)
        {
            int[] starts = StringInfo.ParseCombiningCharacters(text);
            return starts.Length <= elements ? text : text.Substring(0, starts[elements]) + "…";
        }

        private void ClearEntries()
        {
            foreach (var e in _entries) DestroyEntry(e);
            _entries.Clear();
            _current = null;
        }

        private static void DestroyEntry(Entry e)
        {
            if (e.Root != null) UnityEngine.Object.Destroy(e.Root);
        }

        public void Dispose()
        {
            ClearEntries();
            if (_fx != null) { _fx.Dispose(); _fx = null; }
            if (_root != null) UnityEngine.Object.Destroy(_root);
            _root = null;
            foreach (var r in _resources) if (r != null) UnityEngine.Object.Destroy(r);
            _resources.Clear();
            ReleaseFonts();
        }

        /// <summary>只释放字体图集（换字体时用）。</summary>
        private void ReleaseFonts()
        {
            for (int i = 0; i < _fontAssets.Count; i++)
                if (_fontAssets[i] != null) UnityEngine.Object.Destroy(_fontAssets[i]);
            _fontAssets.Clear();
            _font = null;
            _mainFontFile = "";
            _fontFailed = false;
        }

        // ───────────────────────────── 参数（对应 cfg，未配则用默认） ─────────────────────────────

        /// <summary>贴墙时最近能压到多少米（再近会切进相机近裁剪面）。</summary>
        private const float MinDist = .60f;

        private static int _wallMask;
        private static bool _wallMaskTried;

        /// <summary>挡住歌词的图层。名字被改过时退化成「除角色/水以外的一切」。</summary>
        private static int WallMask
        {
            get
            {
                if (!_wallMaskTried)
                {
                    _wallMaskTried = true;
                    // 不猜图层名：PEAK 的岩壁有 Map / Terrain 两套，还可能混着 Default 的碎石。
                    // 直接「除角色/水/UI/忽略射线以外的一切」——凡是能挡住视线的东西都算障碍。
                    _wallMask = ~LayerMask.GetMask("Character", "Water", "UI", "Ignore Raycast");
                }
                return _wallMask;
            }
        }

        private static bool Occlusion
        {
            get { try { return Plugin.LyricOcclusion == null || Plugin.LyricOcclusion.Value; } catch { return true; } }
        }

        /// <summary>立体感档位：0 关 1 轻 2 强（对应 [歌词] Depth）。</summary>
        private static int DepthLevel
        {
            get { try { return Plugin.LyricDepth != null ? Mathf.Clamp(Plugin.LyricDepth.Value, 0, 2) : 1; } catch { return 1; } }
        }

        private static float Distance
        {
            get { return Plugin.LyricDistance != null ? Plugin.LyricDistance.Value : 14f; }
        }

        /// <summary>句子中心离屏幕横向的比例（0.12–0.40）。面板「左右」行也能调。</summary>
        private static float LyricEdge
        {
            get { return Plugin.LyricEdge != null ? Plugin.LyricEdge.Value : 0.25f; }
        }

        /// <summary>
        /// 特效档 0–9（0 = 关）。**当前恒为 0**：面板里的「歌词特效」一排已按要求删除，
        /// 粒子层因此完全不创建。想恢复就把这里改回读 Plugin.LyricFxMode。
        /// </summary>
        private static int FxMode
        {
            get { return 0; }
        }

        /// <summary>0 两侧悬浮 / 1 居中滚动。</summary>
        private static int LyricLayout
        {
            get { try { return Plugin.LyricLayout != null ? Plugin.LyricLayout.Value : 0; } catch { return 0; } }
        }

        private static float LyricY
        {
            get { return Plugin.LyricY != null ? Plugin.LyricY.Value : 0.50f; }
        }

        /// <summary>3D 歌词往哪边散开：0=两侧交替 1=只左侧 2=只右侧。</summary>
        private static int LyricSide
        {
            get { return Plugin.LyricSide != null ? Plugin.LyricSide.Value : 0; }
        }

        /// <summary>
        /// 字号标定（r18 修「字小得看不见」）：r15 的换算系数错了，56px 实际只渲染出 ~23px。
        /// 正确公式：想要屏幕字高 = Size px，则世界字高 = Size/1080 × (2·D·tan(FOV/2))。
        /// 先用 TMP「1pt ≈ 0.1 世界单位」估初值，网格生成后实测 textBounds 再校正 localScale，
        /// 保证任何字体/FOV/距离下屏幕字高都精确等于 Size。
        /// </summary>
        private float CalibrateFont(Camera camera, TextMeshPro main, TextMeshPro translation, float px)
        {
            float dist = Distance;
            float screenWorldH = 2f * dist * Mathf.Tan(camera.fieldOfView * Mathf.Deg2Rad * .5f);
            float worldH = Mathf.Clamp(px, 16f, 200f) / 1080f * screenWorldH;

            main.fontSize = worldH * 10f;           // TMP 世界空间：1pt ≈ 0.1 世界单位
            main.ForceMeshUpdate(true, true);

            float actual = main.textBounds.size.y;
            if (actual > 0.0001f)
            {
                float k = worldH / actual;
                main.transform.localScale *= k;
                translation.transform.localScale *= k;
                main.ForceMeshUpdate(true, true);
            }
            return worldH;
        }

        /// <summary>换行宽度：句子在屏幕 25% / 75% 处，各给约 46% 屏宽的世界单位。</summary>
        private float WorldWidth(Camera camera)
        {
            float screenWorldH = 2f * Distance * Mathf.Tan(camera.fieldOfView * Mathf.Deg2Rad * .5f);
            return Mathf.Max(1f, screenWorldH * camera.aspect * 0.46f);
        }

        /// <summary>
        /// 歌词亮度。以前这里写死 1f，配置里的「发光强度」也是空转的 —— 这就是玩家反馈
        /// 「歌词不够亮」却怎么调都没用的原因。现在接 [歌词] Brightness。
        /// </summary>
        private static float Brightness
        {
            get
            {
                try { return Plugin.LyricBrightness != null ? Plugin.LyricBrightness.Value : 1.3f; }
                catch { return 1.3f; }
            }
        }

        private static bool Wiggle
        {
            get { try { return Plugin.LyricWiggle == null || Plugin.LyricWiggle.Value; } catch { return true; } }
        }

        private static int WiggleFrequency
        {
            get { try { return Plugin.LyricWiggleFps != null ? Plugin.LyricWiggleFps.Value : 8; } catch { return 8; } }
        }

        private static float WiggleAmount
        {
            get { try { return Plugin.LyricWiggleAmount != null ? Plugin.LyricWiggleAmount.Value : .8f; } catch { return .8f; } }
        }

        /// <summary>入场动画：0=淡入 1=上浮 2=弹入。</summary>
        private static int EnterAnim
        {
            get { try { return Plugin.LyricEnterAnim != null ? Plugin.LyricEnterAnim.Value : 1; } catch { return 1; } }
        }

        /// <summary>退场动画：0=淡出 1=上飘 2=缩小 3=下坠。</summary>
        private static int ExitAnim
        {
            get { try { return Plugin.LyricExitAnim != null ? Plugin.LyricExitAnim.Value : 1; } catch { return 1; } }
        }

        /// <summary>
        /// 提亮但保住色相：先把峰值推到 1（保留比例），再往白里混一点点制造发光感。
        /// 直接乘 RGB 会让饱和色在 Brightness ≥ 1.4 时全部变成纯白。
        /// </summary>
        private static Color Boost(Color c, float b)
        {
            if (b <= 1.001f)
                return new Color(Mathf.Clamp01(c.r * b), Mathf.Clamp01(c.g * b), Mathf.Clamp01(c.b * b), 1f);

            float peak = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
            if (peak < .01f) return c;

            float k = Mathf.Clamp01(b - 1f);
            float gain = Mathf.Lerp(1f, 1f / peak, k);        // 峰值最终正好到 1，色相不变
            Color o = new Color(Mathf.Clamp01(c.r * gain),
                                Mathf.Clamp01(c.g * gain),
                                Mathf.Clamp01(c.b * gain), 1f);
            return Color.Lerp(o, Color.white, k * .32f);      // 最多往白混 32%，留一点发光感
        }

        /// <summary>未唱字的透明度：越低越像「逐字浮现」，越高整句越完整。</summary>
        private static float RestAlpha
        {
            get { try { return Plugin.LyricRestAlpha != null ? Plugin.LyricRestAlpha.Value : .58f; } catch { return .58f; } }
        }

        /// <summary>字重：0 常规 1 加粗（靠 SDF _FaceDilate 加厚，不依赖字体是否有 Bold 字面）。</summary>
        private static int LyricWeight
        {
            get { try { return Plugin.LyricWeight != null ? Plugin.LyricWeight.Value : 0; } catch { return 0; } }
        }

        private static float CharSpacing
        {
            get { try { return Plugin.LyricCharSpacing != null ? Plugin.LyricCharSpacing.Value : 2f; } catch { return 2f; } }
        }

        private static float SizeScale
        {
            get { try { return Plugin.LyricSizeScale != null ? Plugin.LyricSizeScale.Value : 1f; } catch { return 1f; } }
        }

        private static float Breath
        {
            get { try { return Plugin.LyricBreath != null ? Plugin.LyricBreath.Value : .01f; } catch { return .01f; } }
        }

        private static float Drift
        {
            get { try { return Plugin.LyricDrift != null ? Plugin.LyricDrift.Value : .01f; } catch { return .01f; } }
        }

        /// <summary>逐字光晕脉冲强度（0–1.5）：每个字唱到时亮度冲高一次。</summary>
        private static float GlowPulse
        {
            get { try { return Plugin.LyricGlowPulse != null ? Plugin.LyricGlowPulse.Value : .6f; } catch { return .6f; } }
        }

        /// <summary>逐字排布：0 直排 1 弧排 2 阶梯 3 散聚 4 斜行 5 波浪（只动位置/角度，不改颜色）。</summary>
        private static int WarpMode
        {
            get { try { return Plugin.LyricWarp != null ? Plugin.LyricWarp.Value : 0; } catch { return 0; } }
        }

        /// <summary>稳定的伪随机（同一个字每次算出来一样，避免抖动闪烁）。</summary>
        private static float Hash01(int i)
        {
            unchecked
            {
                uint x = (uint)i * 2654435761u;
                x ^= x >> 15; x *= 2246822519u; x ^= x >> 13;
                return (x & 0xFFFFFF) / 16777215f;
            }
        }

        /// <summary>整句运动方式：0 静止 1 呼吸 2 漂浮 3 摇摆 4 心跳 5 潮汐。</summary>
        private static int MotionMode
        {
            get { try { return Plugin.LyricMotion != null ? Plugin.LyricMotion.Value : 1; } catch { return 1; } }
        }

        // ───────────────────────────── 查找 ─────────────────────────────

        private int FindIndex(float seconds)
        {
            if (_lines.Count == 0) return -1;
            if (seconds < (float)_lines[0].Time.TotalSeconds) return -1;

            int lo = 0, hi = _lines.Count - 1, best = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if ((float)_lines[mid].Time.TotalSeconds <= seconds) { best = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            return best;
        }
    }
}
