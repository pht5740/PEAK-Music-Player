using System;
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
        private GameObject _root;
        private Entry _current;
        private int _serial;
        private bool _fontFailed;
        private string _renderKey = "";
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
            _renderKey = "";
            ClearEntries();
        }

        internal void Hide()
        {
            ClearEntries();
            _renderKey = "";
        }

        /// <summary>
        /// 每帧驱动。camera 为 null（菜单/加载）时调用方应改走屏幕歌词，这里直接清空。
        /// </summary>
        internal void Tick(MusicPlayer player, Camera camera, float nowSeconds)
        {
            if (player == null || camera == null || _lines.Count == 0)
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
            if (player.Current != null && _songKey != player.Current.Key)
            {
                _songKey = player.Current.Key;
                ClearEntries();
                _renderKey = "";
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
                _renderKey = "";
            }

            string key = _songKey + "/" + index;
            if (key != _renderKey || (_current == null && Time.unscaledTime >= _retryAt))
            {
                Retire();
                _renderKey = key;
                _retryAt = Time.unscaledTime + .8f;
                _current = Spawn(line, endMs, camera);
            }

            // 特效层：跟着当前这句走（换句爆点、逐字触发都在它内部判断）
            if (_fx == null && _root != null) _fx = LyricFx.Create(_root.transform);
            if (_fx != null)
                _fx.Tick(camera, Distance, FxMode, index, positionMs, startMs, endMs, line.Text.Length);

            Animate(camera, positionMs);
        }

        // ───────────────────────────── 字体 ─────────────────────────────

        private bool EnsureFont()
        {
            if (_font != null) return true;
            if (_fontFailed) return false;

            try
            {
                _root = new GameObject("PeakMusic world lyrics");
                UnityEngine.Object.DontDestroyOnLoad(_root);

                foreach (string file in new[] { "msyh.ttc", "msyh.ttf", "simhei.ttf", "simsun.ttc" })
                {
                    var candidate = FileFont(file);
                    if (candidate == null) continue;
                    if (candidate.material != null && candidate.material.shader.isSupported)
                    {
                        _font = candidate;
                        _mainFontFile = file;
                        break;
                    }
                    ReleaseAsset(candidate);
                }
                if (_font == null) throw new InvalidOperationException("系统字体文件无法生成可用图集");

                _resources.Add(_font);
                _resources.Add(_font.material);

                // 主字体只有一个就是「韩文变方块」的病根：msyh.ttc 里没有任何韩文字形
                // （2026-11-12 实测 29,905 个字形，谚文一个都没有）。TMP 找不到字形就画白框，
                // 所以凡是雅黑没覆盖的语言都会这样。实测覆盖情况：
                //   雅黑：中 ✓ 英 ✓ 日假名 ✓ 俄 ✓ 希腊 ✓ ｜ 韩 ✗ 泰 ✗ 越南 ✗ 阿拉伯 ✗
                //   malgun.ttf：韩 ✓ 日 ✓ 俄 ✓（无中文）
                //   tahoma.ttf：泰 ✓ 越南 ✓ 阿拉伯 ✓ 俄 ✓ 英 ✓（无中日韩）
                BuildFallbacks();

                Plugin.Log?.LogInfo(Tag + " SDF 字体就绪（主 " + _mainFontFile + "）。");
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

        /// <summary>
        /// 回退字体顺序 = 覆盖优先级。TMP 只有主字体缺字形时才往这里找，
        /// 所以顺序不影响中文观感，只影响「哪种语言先命中」。
        /// </summary>
        private static readonly string[] FallbackFiles =
        {
            "malgun.ttf",      // 韩文（Windows 自带，覆盖 11,172 个谚文音节）
            "malgunsl.ttf",    // 韩文细体（malgun 缺失时兜底）
            "tahoma.ttf",      // 泰文 / 越南文 / 阿拉伯 / 拉丁
            "YuGothM.ttc",     // 日文汉字补充（雅黑只有假名，没有部分和制汉字）
        };

        /// <summary>把系统里的其它语言字体挂成 TMP 的回退链，缺字形时自动降级过去。</summary>
        private void BuildFallbacks()
        {
            var list = new List<TMP_FontAsset>();
            var got = new List<string>();
            try
            {
                foreach (string file in FallbackFiles)
                {
                    if (file == _mainFontFile) continue;
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
                    _resources.Add(asset);
                    if (asset.material != null) _resources.Add(asset.material);
                    got.Add(file);
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

        private static TMP_FontAsset FileFont(string name)
        {
            string path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), name);
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

            string text = Limit(line.Text, 160);

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
            float ww = WorldWidth(camera);
            e.Main.rectTransform.sizeDelta = new Vector2(ww, 20f);
            e.Translation.rectTransform.sizeDelta = new Vector2(ww, 20f);
            float px = (Plugin.LyricSize != null ? Plugin.LyricSize.Value : 56f) * SizeScale;
            float worldH = CalibrateFont(camera, e.Main, e.Translation, px);
            e.Translation.transform.localPosition = new Vector3(0f, -worldH * .6f - .1f, 0f);

            // 基准色只设一次：主行留白（逐字色写在顶点上），副行用样式里的翻译色
            e.Main.color = Color.white;
            e.Translation.color = LyricColors.Sub;

            if (!Place(e, camera))
            {
                DestroyEntry(e);
                return null;
            }

            e.Original = e.Main.textInfo.CopyMeshInfoVertexData();
            e.CharacterToGrapheme = new int[e.Main.textInfo.characterCount];
            for (int i = 0; i < e.CharacterToGrapheme.Length; i++)
            {
                int source = e.Main.textInfo.characterInfo[i].index;
                int grapheme = 0;
                while (grapheme + 1 < e.Animation.Graphemes.Length && e.Animation.Graphemes[grapheme + 1].SourceStart <= source) grapheme++;
                e.CharacterToGrapheme[i] = grapheme;
            }

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
        }

        // ───────────────────────────── 动画 ─────────────────────────────

        private void Retire()
        {
            if (_current == null) return;
            _current.Retired = Time.unscaledTime;
            _current = null;
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
                h = h * 31 + LyricWeight;
                h = h * 31 + Mathf.RoundToInt(RestAlpha * 100f);
            }
            catch { }
            return h;
        }

        private void Animate(Camera camera, double positionMs)
        {
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
                Vector3 dir = camera.ViewportToWorldPoint(e.Viewport) - camera.transform.position;
                if (dir.sqrMagnitude > 0.0001f)
                {
                    dir.Normalize();
                    RaycastHit hit;
                    int mask = LayerMask.GetMask("Map", "Terrain");
                    if (Physics.Raycast(camera.transform.position, dir, out hit, Distance, mask, QueryTriggerInteraction.Ignore))
                        dist = Mathf.Max(2.5f, hit.distance - 0.4f);
                    e.Anchor = camera.transform.position + dir * dist;
                }
                e.Root.transform.rotation = Face(e.Anchor, camera);
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
                    Color rgb = Color.Lerp(restRgb, litRgb, litT);

                    // 逐字光晕脉冲：对照「每个字自己冲高一次再回落」的做法 ——
                    // 字刚唱到的那 ~160ms 亮度抬高，之后回常态。这是它那种「字在发光」的关键。
                    // Pop 直接由缩放曲线反推（1.55→1 的回弹），不去读 Core 的新字段 ——
                    // 部署的 Core 是另一份构建，加字段会让插件在运行时抛 MissingFieldException。
                    float pulse = GlowPulse;
                    float pop = Mathf.Clamp01(((float)frame.Scale - 1f) / .55f);
                    if (pulse > .001f && pop > .001f)
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
                                float j1 = Hash01(c) - .5f;
                                float j2 = Hash01(c + 977) - .5f;
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

                // 注意：原来这里每帧 e.Main.color = Color.white 会把上面写好的逐字色洗掉，
                // 所以主行/副行的基准色改成出生时设一次，这里只驱动副行透明度。
                e.Translation.alpha = alpha;
            }
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
            _font = null;
        }

        // ───────────────────────────── 参数（对应 cfg，未配则用默认） ─────────────────────────────

        private static float Distance
        {
            get { return Plugin.LyricDistance != null ? Plugin.LyricDistance.Value : 14f; }
        }

        /// <summary>句子中心离屏幕横向的比例（0.12–0.40）。面板「左右」行也能调。</summary>
        private static float LyricEdge
        {
            get { return Plugin.LyricEdge != null ? Plugin.LyricEdge.Value : 0.25f; }
        }

        /// <summary>句子中心在屏幕上的垂直位置（0.1–0.9）。面板「高度」行也能调。</summary>
        /// <summary>特效档 0–9（0 = 关）。</summary>
        private static int FxMode
        {
            get { try { return Plugin.LyricFxMode != null ? Plugin.LyricFxMode.Value : 0; } catch { return 0; } }
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
            float worldH = Mathf.Clamp(px, 16f, 140f) / 1080f * screenWorldH;

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
