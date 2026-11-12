using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace PeakMusic
{
    /// <summary>
    /// 歌词特效层：纯程序化粒子（不依赖任何贴图素材、不依赖 Folia）。
    ///
    /// 所有效果共用一套 512 粒子的池子，每帧重建一个 Mesh（最多 2048 顶点），
    /// 因此开销固定、不产生 GC；关掉时完全不创建对象。
    ///
    /// 效果档（cfg <c>[歌词] Fx</c>）：
    ///   0 关 · 1 星尘 · 2 花火 · 3 绘光 · 4 时计 · 5 回环 · 6 心象 · 7 脉冲 · 8 波纹 · 9 极光
    /// </summary>
    internal sealed class LyricFx
    {
        private struct P
        {
            internal Vector2 Pos, Vel;
            internal float Life, MaxLife, Size, Rot, Spin;
            internal Color Color;
            internal int Shape;      // 0 方块 1 菱形 2 圆 3 三角
            internal float Gravity;
        }

        private const int MaxParticles = 512;

        private readonly P[] _p = new P[MaxParticles];
        private int _count;

        private GameObject _go;
        private Mesh _mesh;
        private MeshFilter _mf;
        private MeshRenderer _mr;
        private Material _mat;
        private Texture2D _tex;

        private int _lastLine = -1;
        private int _lastChars = -1;
        private float _ringAngle;
        private float _pulse;

        internal static LyricFx Create(Transform parent)
        {
            var fx = new LyricFx();
            fx.Build(parent);
            return fx;
        }

        private void Build(Transform parent)
        {
            _go = new GameObject("PeakMusic FX");
            _go.transform.SetParent(parent, false);
            _mf = _go.AddComponent<MeshFilter>();
            _mr = _go.AddComponent<MeshRenderer>();
            _mesh = new Mesh { name = "lyric-fx" };
            _mesh.MarkDynamic();
            _mf.sharedMesh = _mesh;

            _tex = SoftDot(64);
            var shader = Shader.Find("Sprites/Default");
            if (shader == null) shader = Shader.Find("Unlit/Transparent");
            _mat = new Material(shader) { mainTexture = _tex };
            _mr.sharedMaterial = _mat;
            _mr.shadowCastingMode = ShadowCastingMode.Off;
            _mr.receiveShadows = false;
            _mr.sortingOrder = 10;
        }

        /// <summary>圆形柔边贴图，运行时生成，避免引入素材。</summary>
        private static Texture2D SoftDot(int n)
        {
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false);
            var px = new Color32[n * n];
            float c = (n - 1) * .5f;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) / c;
                    float a = Mathf.Clamp01(1f - d);
                    a = a * a;                                  // 柔和衰减
                    px[y * n + x] = new Color32(255, 255, 255, (byte)(a * 255));
                }
            t.SetPixels32(px);
            t.Apply(false, true);
            return t;
        }

        // ───────────────────────── 每帧 ─────────────────────────

        /// <param name="mode">效果档 0–9</param>
        /// <param name="lineIndex">当前句下标（换句时触发爆点）</param>
        /// <param name="posMs">播放位置（毫秒）</param>
        /// <param name="lineStartMs">本句开始（毫秒）</param>
        /// <param name="lineEndMs">本句结束（毫秒）</param>
        /// <param name="chars">本句字数（用于逐字触发）</param>
        internal void Tick(Camera cam, float distance, int mode, int lineIndex,
                           double posMs, double lineStartMs, double lineEndMs, int chars)
        {
            if (_go == null) return;

            if (mode <= 0)
            {
                if (_count > 0) { _count = 0; Rebuild(); }
                _go.SetActive(false);
                return;
            }
            _go.SetActive(true);

            // 跟随相机：固定距离的一个平面
            Transform ct = cam.transform;
            _go.transform.position = ct.position + ct.forward * distance;
            _go.transform.rotation = ct.rotation;

            // **必须按屏幕真实高度缩放**：粒子坐标是按「屏幕高 = 1 世界单位」写的，
            // 但在 distance 米处的实际可见高度是 2*d*tan(fov/2)（默认约 16 单位）。
            // 少了这一句，所有粒子只有屏幕的 1/16 大 —— 看起来就是「点了特效但什么都没出现」。
            float worldH = 2f * Mathf.Max(.1f, distance) * Mathf.Tan(cam.fieldOfView * .5f * Mathf.Deg2Rad);
            float parentScale = 1f;
            Transform pt = _go.transform.parent;
            if (pt != null) { parentScale = pt.lossyScale.y; if (parentScale < 1e-4f) parentScale = 1f; }
            _go.transform.localScale = Vector3.one * (worldH / parentScale);

            float dt = Mathf.Min(Time.unscaledDeltaTime, .05f);
            float t = Time.unscaledTime;

            // ── 换句：整体爆点 + 方向性冲击 ──
            if (lineIndex != _lastLine)
            {
                _lastLine = lineIndex;
                _lastChars = -1;
                if (lineIndex >= 0) OnLineChange(mode);
            }

            // ── 逐字触发：按已唱比例推字数 ──
            double span = System.Math.Max(1.0, lineEndMs - lineStartMs);
            double prog = Mathf.Clamp01((float)((posMs - lineStartMs) / span));
            int lit = Mathf.Clamp(Mathf.FloorToInt((float)prog * Mathf.Max(1, chars)), 0, Mathf.Max(1, chars));
            if (lit != _lastChars)
            {
                if (_lastChars >= 0 && lit > _lastChars) OnChar(mode, lit - _lastChars);
                _lastChars = lit;
            }

            // ── 持续型效果 ──
            Ambient(mode, dt, t);

            Step(dt);
            Rebuild();
        }

        private void OnLineChange(int mode)
        {
            switch (mode)
            {
                case 2: Burst(26, 0f, 1.6f, new Color32(255, 214, 140, 255), 3); break;    // 花火
                case 7: _pulse = 1f; break;                                                 // 脉冲
                case 3: Spawn(5, new Color32(180, 230, 255, 220), 2, .05f, .5f); break;      // 绘光
                case 6: Shards(14); break;                                                  // 心象
                default: Spawn(6, new Color32(255, 255, 255, 160), 2, .02f, .4f); break;
            }
        }

        private void OnChar(int mode, int n)
        {
            switch (mode)
            {
                case 2: Burst(6 * n, .18f, .8f, new Color32(255, 226, 160, 255), 3); break;
                case 6: Shards(3 * n); break;
                case 8: Rings(2 * n); break;
                case 1: Spawn(2 * n, new Color32(220, 235, 255, 200), 2, .015f, .35f); break;
                default: Spawn(2 * n, new Color32(255, 255, 255, 150), 2, .02f, .3f); break;
            }
        }

        private void Ambient(int mode, float dt, float t)
        {
            switch (mode)
            {
                case 1:   // 星尘：缓慢上浮
                    if (Random.value < dt * 8f)
                        Spawn(1, new Color32(210, 225, 255, 170), 2, .01f, .28f, up: .06f);
                    break;
                case 3:   // 绘光：光球上升
                    if (Random.value < dt * 5f)
                        Spawn(1, new Color32(170, 220, 255, 200), 2, .06f, .55f, up: .18f);
                    break;
                case 4:   // 时计：两根指针绕着转
                    _ringAngle += dt * 55f;
                    break;
                case 5:   // 回环：一圈光点绕当前句
                    _ringAngle += dt * 40f;
                    break;
                case 9:   // 极光：横向漂移的软带
                    if (Random.value < dt * 12f)
                        Spawn(1, new Color32(140, 255, 220, 90), 0, .22f, 1.2f, up: .05f);
                    break;
                case 10:  // 流星：斜向下落的细线
                    if (Random.value < dt * 9f)
                    {
                        var pp = new P
                        {
                            Pos = new Vector2(Random.Range(-.6f, .6f), .45f),
                            Vel = new Vector2(Random.Range(-.25f, -.05f), Random.Range(-.9f, -.55f)),
                            MaxLife = .5f, Size = .012f, Shape = 1,
                            Color = new Color32(200, 230, 255, 220),
                        };
                        pp.Life = pp.MaxLife;
                        Push(pp);
                    }
                    break;
            }
            _pulse = Mathf.Max(0f, _pulse - dt * 1.6f);
        }

        // ───────────────────────── 粒子 ─────────────────────────

        private void Burst(int n, float spread, float speed, Color c, int shape)
        {
            for (int i = 0; i < n; i++)
            {
                float a = Random.value * Mathf.PI * 2f;
                float s = speed * Random.Range(.4f, 1f);
                Add(new Vector2(Mathf.Cos(a) * spread, Mathf.Sin(a) * spread * .6f),
                    new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * s,
                    Random.Range(.35f, .8f), Random.Range(.012f, .03f), c, shape, .55f);
            }
        }

        private void Spawn(int n, Color c, int shape, float size, float life, float up = 0f)
        {
            for (int i = 0; i < n; i++)
                Add(new Vector2(Random.Range(-.5f, .5f), Random.Range(-.06f, .06f)),
                    new Vector2(Random.Range(-.05f, .05f), up > 0 ? up * Random.Range(.6f, 1.2f) : .02f),
                    life, size, c, shape, 0f);
        }

        private void Shards(int n)
        {
            for (int i = 0; i < n; i++)
            {
                float a = Random.value * Mathf.PI * 2f;
                Add(Vector2.zero, new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * Random.Range(.3f, .7f),
                    Random.Range(.3f, .6f), Random.Range(.02f, .045f),
                    new Color32(255, 255, 255, 230), 3, .9f);
            }
        }

        private void Rings(int n)
        {
            for (int i = 0; i < n; i++)
            {
                var p = new P
                {
                    Pos = Vector2.zero,
                    Vel = Vector2.zero,
                    MaxLife = Random.Range(.4f, .7f),
                    Size = Random.Range(.05f, .1f),
                    Shape = 4,                       // 4 = 空心环
                    Color = new Color32(200, 240, 255, 200),
                };
                p.Life = p.MaxLife;
                Push(p);
            }
        }

        private void Add(Vector2 pos, Vector2 vel, float life, float size, Color c, int shape, float gravity)
        {
            var p = new P
            {
                Pos = pos, Vel = vel, MaxLife = life, Size = size,
                Color = c, Shape = shape, Gravity = gravity,
                Rot = Random.value * 6.28f, Spin = Random.Range(-2f, 2f),
            };
            p.Life = life;
            Push(p);
        }

        private void Push(P p)
        {
            if (_count >= MaxParticles) return;
            _p[_count++] = p;
        }

        private void Step(float dt)
        {
            for (int i = _count - 1; i >= 0; i--)
            {
                var p = _p[i];
                p.Life -= dt;
                if (p.Life <= 0f) { _p[i] = _p[--_count]; continue; }
                p.Vel.y -= p.Gravity * dt;
                p.Vel *= 1f - .9f * dt;
                p.Pos += p.Vel * dt;
                p.Rot += p.Spin * dt;
                _p[i] = p;
            }
        }

        // ───────────────────────── 画 ─────────────────────────

        private void Rebuild()
        {
            if (_mesh == null) return;

            var verts = new List<Vector3>(_count * 4 + 4);
            var uvs = new List<Vector2>(_count * 4 + 4);
            var cols = new List<Color>(_count * 4 + 4);
            var tris = new List<int>(_count * 6 + 6);

            float aspect = 1.78f;
            var cam = Camera.main;
            if (cam != null && cam.aspect > .1f) aspect = cam.aspect;
            float hw = .5f * aspect;      // 半宽（世界单位 @ 该平面 ≈ 屏幕比例）

            for (int i = 0; i < _count; i++)
            {
                var p = _p[i];
                float k = p.Life / p.MaxLife;
                Color c = p.Color;
                c.a *= Mathf.Clamp01(k * 1.6f);

                float s = p.Size * (p.Shape == 4 ? (1f + (1f - k) * 2.2f) : Mathf.Lerp(.6f, 1.1f, k));
                Vector2 pos = new Vector2(p.Pos.x * hw, p.Pos.y);
                float cs = Mathf.Cos(p.Rot), sn = Mathf.Sin(p.Rot);

                int b = verts.Count;
                for (int v = 0; v < 4; v++)
                {
                    float ox = (v == 0 || v == 3) ? -s : s;
                    float oy = (v < 2) ? s : -s;
                    if (p.Shape == 1) { ox *= .8f; oy *= .8f; }               // 菱形
                    if (p.Shape == 3) { oy *= (v < 2) ? 1.6f : .6f; }         // 三角近似
                    verts.Add(new Vector3(pos.x + ox * cs - oy * sn, pos.y + ox * sn + oy * cs, 0f));
                    uvs.Add(new Vector2(v == 0 || v == 3 ? 0 : 1, v < 2 ? 1 : 0));
                    cols.Add(c);
                }
                tris.Add(b); tris.Add(b + 1); tris.Add(b + 2);
                tris.Add(b); tris.Add(b + 2); tris.Add(b + 3);
            }

            // 时计/回环/脉冲这类"整句"的图形，用额外几个环来画
            if (_lastLine >= 0)
            {
                int mode = Plugin.LyricFxMode != null ? Plugin.LyricFxMode.Value : 0;
                if (mode == 5) AddRing(ref verts, ref uvs, ref cols, ref tris, .5f, .42f, _ringAngle, new Color(1f, .9f, .6f, .5f));
                if (mode == 4)
                {
                    AddRing(ref verts, ref uvs, ref cols, ref tris, .3f, .30f, _ringAngle, new Color(.8f, .95f, 1f, .45f));
                    AddRing(ref verts, ref uvs, ref cols, ref tris, .3f, .30f, -_ringAngle * .4f, new Color(1f, .8f, .5f, .35f));
                }
                if (_pulse > .01f)
                    AddRing(ref verts, ref uvs, ref cols, ref tris, .2f + (1f - _pulse) * .8f, .02f, 0f,
                            new Color(1f, 1f, 1f, _pulse * .6f), segments: 64);
            }

            _mesh.Clear();
            _mesh.SetVertices(verts);
            _mesh.SetUVs(0, uvs);
            _mesh.SetColors(cols);
            _mesh.SetTriangles(tris, 0);
            // 每帧重建顶点后必须重算包围盒：Clear() 会把 bounds 清成 0，
            // Unity 的视锥剔除会按那个假边界判，结果就是"特效生成了但一个都看不见"。
            _mesh.RecalculateBounds();
        }

        /// <summary>把一圈（或一段）环拆成若干四边形，塞进同一个 Mesh。</summary>
        private static void AddRing(ref List<Vector3> verts, ref List<Vector2> uvs, ref List<Color> cols,
                                    ref List<int> tris, float radius, float width, float angleDeg, Color c, int segments = 48)
        {
            float aspect = 1.78f;
            var cam = Camera.main;
            if (cam != null && cam.aspect > .1f) aspect = cam.aspect;
            float hw = .5f * aspect;

            float rot = angleDeg * Mathf.Deg2Rad;
            for (int i = 0; i < segments; i++)
            {
                float a0 = rot + i * Mathf.PI * 2f / segments;
                float a1 = rot + (i + 1) * Mathf.PI * 2f / segments;
                Vector2 p0 = new Vector2(Mathf.Cos(a0) * radius * hw, Mathf.Sin(a0) * radius);
                Vector2 p1 = new Vector2(Mathf.Cos(a1) * radius * hw, Mathf.Sin(a1) * radius);
                Vector2 n0 = new Vector2(-Mathf.Sin(a0), Mathf.Cos(a0)) * width;
                Vector2 n1 = new Vector2(-Mathf.Sin(a1), Mathf.Cos(a1)) * width;

                int b = verts.Count;
                verts.Add(p0 + n0); verts.Add(p0 - n0); verts.Add(p1 - n1); verts.Add(p1 + n1);
                uvs.Add(new Vector2(0, 1)); uvs.Add(new Vector2(0, 0));
                uvs.Add(new Vector2(1, 0)); uvs.Add(new Vector2(1, 1));
                for (int k = 0; k < 4; k++) cols.Add(c);
                tris.Add(b); tris.Add(b + 1); tris.Add(b + 2);
                tris.Add(b); tris.Add(b + 2); tris.Add(b + 3);
            }
        }

        internal void Dispose()
        {
            if (_mesh != null) Object.Destroy(_mesh);
            if (_mat != null) Object.Destroy(_mat);
            if (_tex != null) Object.Destroy(_tex);
            if (_go != null) Object.Destroy(_go);
            _mesh = null; _mat = null; _tex = null; _go = null; _count = 0;
        }
    }
}
