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
        private GameObject _root;
        private Entry _current;
        private int _serial;
        private bool _fontFailed;
        private string _renderKey = "";
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

            string key = _songKey + "/" + index;
            if (key != _renderKey || (_current == null && Time.unscaledTime >= _retryAt))
            {
                Retire();
                _renderKey = key;
                _retryAt = Time.unscaledTime + .8f;
                _current = Spawn(line, endMs, camera);
            }

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
                        break;
                    }
                    ReleaseAsset(candidate);
                }
                if (_font == null) throw new InvalidOperationException("系统字体文件无法生成可用图集");

                _resources.Add(_font);
                _resources.Add(_font.material);
                Plugin.Log?.LogInfo(Tag + " SDF 字体就绪，已加载中文字形。");
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
            };
            e.Root.transform.SetParent(_root.transform, false);

            e.Main = MakeText(e.Root, "Original", text, 36f);
            e.Translation = MakeText(e.Root, "Translation", "", 36f * .72f);
            float ww = WorldWidth(camera);
            e.Main.rectTransform.sizeDelta = new Vector2(ww, 20f);
            e.Translation.rectTransform.sizeDelta = new Vector2(ww, 20f);
            float px = Plugin.LyricSize != null ? Plugin.LyricSize.Value : 56f;
            float worldH = CalibrateFont(camera, e.Main, e.Translation, px);
            e.Translation.transform.localPosition = new Vector3(0f, -worldH * .6f - .1f, 0f);

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
            float brightness = isDark ? 1f : Brightness;
            var white = new Color(brightness, brightness, brightness, 1f);

            Color face = LyricColors.Lit;
            if (material.HasProperty("_FaceColor"))
                material.SetColor("_FaceColor", new Color(face.r * brightness, face.g * brightness, face.b * brightness, 1f));
            if (material.HasProperty("_Color")) material.SetColor("_Color", white);
            if (material.HasProperty("_FaceDilate")) material.SetFloat("_FaceDilate", .08f);

            if (material.HasProperty("_OutlineWidth"))
            {
                material.SetFloat("_OutlineWidth", .14f);
                material.EnableKeyword("OUTLINE_ON");
            }
            if (material.HasProperty("_OutlineColor"))
                material.SetColor("_OutlineColor", isDark ? new Color(1f, 1f, 1f, .8f) : new Color(.01f, .015f, .025f, .95f));

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

        private void Animate(Camera camera, double positionMs)
        {
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                var e = _entries[i];
                float time = Time.unscaledTime;
                float age = time - e.Born;
                float leaving = e.Retired < 0 ? 0f : (time - e.Retired) / .4f;

                if (e.Retired >= 0 && leaving >= 1f)
                {
                    _entries.RemoveAt(i);
                    DestroyEntry(e);
                    continue;
                }

                float alpha = Mathf.Clamp01(age / .25f);
                float scale = 1f;
                if (e.Retired >= 0) alpha *= 1f - leaving;

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
                e.Root.transform.position = e.Anchor;
                e.Root.transform.localScale = Vector3.one * (scale * dist / Distance);

                if (e.Retired < 0) e.LastPositionMs = positionMs;

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

                    float visible = alpha * (float)frame.Alpha;
                    var original = e.Original[mesh].vertices;
                    var vertices = info.meshInfo[mesh].vertices;
                    var colors = info.meshInfo[mesh].colors32;

                    Vector3 centre = (original[vi] + original[vi + 2]) * .5f;
                    var rotation = Quaternion.Euler(0f, 0f, (float)(frame.Rotation * 180 / Math.PI));
                    float pixel = .03f * 1f * (Distance / 6.5f);

                    for (int k = 0; k < 4; k++)
                    {
                        vertices[vi + k] = centre + rotation * ((original[vi + k] - centre) * (float)frame.Scale)
                            + new Vector3((float)frame.XOffset * pixel, (float)frame.YOffset * pixel, 0f);
                        colors[vi + k] = new Color(1f, 1f, 1f, Mathf.Clamp01(visible));
                    }
                }
                e.Main.UpdateVertexData(TMP_VertexDataUpdateFlags.Vertices | TMP_VertexDataUpdateFlags.Colors32);

                e.Main.color = Color.white;
                e.Translation.color = Color.white;
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

        private static float Brightness
        {
            get { return 1f; }
        }

        private static bool Wiggle
        {
            get { return Plugin.LyricGlow != null ? true : true; } // 抖动默认开（原版默认开）
        }

        private static int WiggleFrequency
        {
            get { return 8; }
        }

        private static float WiggleAmount
        {
            get { return .8f; }
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
