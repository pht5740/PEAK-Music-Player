using System;
using System.Collections.Generic;
using System.Text;

namespace PeakMusic.Core.Qr
{
    /// <summary>
    /// 一张编码好的二维码。只提供"这个模块是不是黑的"，绘制交给调用方（Unity 里画成 Texture2D）。
    /// </summary>
    public sealed class QrCode
    {
        public readonly int Version;
        public readonly int Size;
        public readonly int Mask;
        private readonly bool[,] _m;      // _m[y, x]

        internal QrCode(bool[,] m, int version, int mask)
        {
            _m = m;
            Version = version;
            Size = m.GetLength(0);
            Mask = mask;
        }

        /// <summary>x = 列，y = 行，都在 [0, Size)。true = 黑。</summary>
        public bool Dark(int x, int y)
        {
            return _m[y, x];
        }
    }

    /// <summary>
    /// 极简 QR 编码器：byte 模式 + 纠错等级 M + 版本 1–10 自适应。
    ///
    /// 为什么自己写而不是引库：插件只有一个 exe + 两个 dll，不想为了画一个二维码
    /// 把 QRCoder / ZXing 整个塞进 Unity 运行时（NetStandard 兼容性也是一堆坑）。
    ///
    /// 能力边界（够用就行）：
    ///   · 只支持 byte 模式（UTF-8），不做数字/字母数字/Kanji 模式 —— 少一半代码，
    ///     而我们要编的只有 "https://music.163.com/login?codekey=…" 这种 ASCII URL，
    ///     以及 ≤216 字节的任意 UTF-8 文本（V10-M 的容量）。
    ///   · 纠错固定 M（约 15% 冗余）。屏幕上显示的码不缺角，M 足够。
    ///
    /// 正确性不是"看着像"就算：tools/qr_reference.py 用 python-qrcode 生成参考矩阵，
    /// Sandbox 的 <c>qrtest</c> 命令逐模块比对 3 组文本 × 8 种掩码 = 24 个矩阵。
    /// </summary>
    public static class QrEncoder
    {
        public const int MaxVersion = 10;

        // ── 标准表（版本 1..10，纠错 M）。用 python qrcode 的 rs_blocks 交叉核对过 ──
        private static readonly int[] TotalCodewords = { 0, 26, 44, 70, 100, 134, 172, 196, 242, 292, 346 };
        private static readonly int[] EcPerBlock = { 0, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26 };

        // { 组1块数, 组1每块数据码字, 组2块数, 组2每块数据码字 }
        private static readonly int[][] Blocks =
        {
            null,
            new[] { 1, 16, 0, 0 },
            new[] { 1, 28, 0, 0 },
            new[] { 1, 44, 0, 0 },
            new[] { 2, 32, 0, 0 },
            new[] { 2, 43, 0, 0 },
            new[] { 4, 27, 0, 0 },
            new[] { 4, 31, 0, 0 },
            new[] { 2, 38, 2, 39 },
            new[] { 3, 36, 2, 37 },
            new[] { 4, 43, 1, 44 },
        };

        // 对齐图形中心坐标（V1 没有）
        private static readonly int[][] AlignPos =
        {
            null,
            new int[0],
            new[] { 6, 18 },
            new[] { 6, 22 },
            new[] { 6, 26 },
            new[] { 6, 30 },
            new[] { 6, 34 },
            new[] { 6, 22, 38 },
            new[] { 6, 24, 42 },
            new[] { 6, 26, 46 },
            new[] { 6, 28, 50 },
        };

        // ── GF(256)，本原多项式 0x11D ──
        private static readonly byte[] Exp = new byte[512];
        private static readonly byte[] Log = new byte[256];

        static QrEncoder()
        {
            int x = 1;
            for (int i = 0; i < 255; i++)
            {
                Exp[i] = (byte)x;
                Log[x] = (byte)i;
                x <<= 1;
                if (x >= 256) x ^= 0x11D;
            }
            for (int i = 255; i < 512; i++) Exp[i] = Exp[i - 255];
        }

        private static byte Mul(byte a, byte b)
        {
            if (a == 0 || b == 0) return 0;
            return Exp[Log[a] + Log[b]];
        }

        public static int DataCodewords(int version)
        {
            int[] spec = Blocks[version];
            return spec[0] * spec[1] + spec[2] * spec[3];
        }

        /// <summary>编码。<paramref name="maskOverride"/> = -1 表示按标准罚分自动挑掩码。</summary>
        public static QrCode Encode(string text, int maskOverride = -1)
        {
            byte[] payload = Encoding.UTF8.GetBytes(text ?? "");
            int version = PickVersion(payload.Length);
            int size = 17 + 4 * version;

            byte[] codewords = BuildCodewords(payload, version);
            Canvas c = new Canvas(size);
            DrawFunctionPatterns(c, version);
            PlaceData(c, codewords);

            int mask = maskOverride >= 0 ? (maskOverride & 7) : ChooseMask(c);
            bool[,] final = ApplyMask(c, mask);
            DrawFormatBits(final, size, mask);
            if (version >= 7) DrawVersionBits(final, size, version);

            return new QrCode(final, version, mask);
        }

        // ───────────────────────────── 编码 ─────────────────────────────

        private static int PickVersion(int byteLen)
        {
            for (int v = 1; v <= MaxVersion; v++)
            {
                int countBits = v >= 10 ? 16 : 8;
                int need = 4 + countBits + byteLen * 8;
                if (need <= DataCodewords(v) * 8) return v;
            }
            throw new ArgumentException("内容太长：" + byteLen + " 字节，QR 版本 10（纠错 M）最多 216 字节");
        }

        private static byte[] BuildCodewords(byte[] payload, int version)
        {
            int capacity = DataCodewords(version) * 8;

            var bits = new BitStream();
            bits.Put(0x4, 4);                                        // 0100 = byte 模式
            bits.Put(payload.Length, version >= 10 ? 16 : 8);         // 字符计数
            for (int i = 0; i < payload.Length; i++) bits.Put(payload[i], 8);

            bits.Put(0, Math.Min(4, capacity - bits.Length));         // 终止符
            while (bits.Length % 8 != 0) bits.Put(0, 1);

            byte pad = 0xEC;
            while (bits.Length < capacity)
            {
                bits.Put(pad, 8);
                pad = pad == 0xEC ? (byte)0x11 : (byte)0xEC;
            }

            byte[] data = bits.ToBytes();
            int ecLen = EcPerBlock[version];
            byte[] gen = GenPoly(ecLen);

            int[] spec = Blocks[version];
            var dataBlocks = new List<byte[]>();
            var ecBlocks = new List<byte[]>();
            int off = 0;
            for (int g = 0; g < 2; g++)
            {
                int count = spec[g * 2];
                int len = spec[g * 2 + 1];
                for (int i = 0; i < count; i++)
                {
                    var d = new byte[len];
                    Array.Copy(data, off, d, 0, len);
                    off += len;
                    dataBlocks.Add(d);
                    ecBlocks.Add(RsRemainder(d, gen));
                }
            }

            var result = new byte[TotalCodewords[version]];
            int p = 0;
            int maxData = 0;
            for (int i = 0; i < dataBlocks.Count; i++)
                if (dataBlocks[i].Length > maxData) maxData = dataBlocks[i].Length;

            for (int i = 0; i < maxData; i++)
                for (int b = 0; b < dataBlocks.Count; b++)
                    if (i < dataBlocks[b].Length) result[p++] = dataBlocks[b][i];

            for (int i = 0; i < ecLen; i++)
                for (int b = 0; b < ecBlocks.Count; b++) result[p++] = ecBlocks[b][i];

            if (p != result.Length)
                throw new InvalidOperationException("码字交错长度不对：" + p + " != " + result.Length);
            return result;
        }

        private static byte[] GenPoly(int degree)
        {
            var g = new byte[degree + 1];
            g[0] = 1;
            int len = 1;
            for (int i = 0; i < degree; i++)
            {
                byte root = Exp[i];
                for (int j = len; j > 0; j--) g[j] = (byte)(g[j] ^ Mul(g[j - 1], root));
                len++;
            }
            return g;
        }

        private static byte[] RsRemainder(byte[] data, byte[] gen)
        {
            int degree = gen.Length - 1;
            var rem = new byte[degree];
            for (int k = 0; k < data.Length; k++)
            {
                byte factor = (byte)(data[k] ^ rem[0]);
                Array.Copy(rem, 1, rem, 0, degree - 1);
                rem[degree - 1] = 0;
                for (int i = 0; i < degree; i++) rem[i] = (byte)(rem[i] ^ Mul(gen[i + 1], factor));
            }
            return rem;
        }

        // ───────────────────────────── 矩阵：功能图形 ─────────────────────────────

        private sealed class Canvas
        {
            public readonly int Size;
            public readonly bool[,] M;
            public readonly bool[,] Fn;      // 功能模块标记（不参与掩码与数据放置）

            public Canvas(int size)
            {
                Size = size;
                M = new bool[size, size];
                Fn = new bool[size, size];
            }

            public bool Inside(int x, int y)
            {
                return x >= 0 && y >= 0 && x < Size && y < Size;
            }

            public void Set(int x, int y, bool dark)
            {
                if (!Inside(x, y)) return;
                M[y, x] = dark;
                Fn[y, x] = true;
            }

            public void Reserve(int x, int y)
            {
                if (Inside(x, y)) Fn[y, x] = true;
            }
        }

        private static void DrawFunctionPatterns(Canvas c, int version)
        {
            int size = c.Size;

            // 时序图形（先画，finder 会覆盖两端）
            for (int i = 0; i < size; i++)
            {
                c.Set(6, i, i % 2 == 0);
                c.Set(i, 6, i % 2 == 0);
            }

            // 三个定位图形 + 分隔符
            Finder(c, 0, 0);
            Finder(c, size - 7, 0);
            Finder(c, 0, size - 7);

            // 对齐图形
            int[] pos = AlignPos[version];
            for (int i = 0; i < pos.Length; i++)
            {
                for (int j = 0; j < pos.Length; j++)
                {
                    bool corner = (i == 0 && j == 0)
                               || (i == 0 && j == pos.Length - 1)
                               || (i == pos.Length - 1 && j == 0);
                    if (corner) continue;
                    Alignment(c, pos[i], pos[j]);
                }
            }

            // 固定的黑模块
            c.Set(8, size - 8, true);

            // 预留格式信息区（值在选好掩码后再填）
            for (int i = 0; i <= 8; i++)
            {
                c.Reserve(8, i);
                c.Reserve(i, 8);
            }
            for (int i = 0; i < 8; i++)
            {
                c.Reserve(8, size - 1 - i);
                c.Reserve(size - 1 - i, 8);
            }

            // 预留版本信息区（V7 起才有）
            if (version >= 7)
            {
                for (int i = 0; i < 18; i++)
                {
                    int a = size - 11 + i % 3, b = i / 3;
                    c.Reserve(a, b);
                    c.Reserve(b, a);
                }
            }
        }

        private static void Finder(Canvas c, int cx, int cy)
        {
            // cx, cy 是 7x7 定位图形左上角；外面那圈白边是分隔符
            for (int dy = -1; dy <= 7; dy++)
            {
                for (int dx = -1; dx <= 7; dx++)
                {
                    int x = cx + dx, y = cy + dy;
                    if (!c.Inside(x, y)) continue;
                    bool dark = (dx >= 0 && dx <= 6 && (dy == 0 || dy == 6))
                             || (dy >= 0 && dy <= 6 && (dx == 0 || dx == 6))
                             || (dx >= 2 && dx <= 4 && dy >= 2 && dy <= 4);
                    c.Set(x, y, dark);
                }
            }
        }

        private static void Alignment(Canvas c, int cx, int cy)
        {
            for (int dy = -2; dy <= 2; dy++)
            {
                for (int dx = -2; dx <= 2; dx++)
                {
                    int ring = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    c.Set(cx + dx, cy + dy, ring != 1);
                }
            }
        }

        private static void PlaceData(Canvas c, byte[] codewords)
        {
            int size = c.Size;
            int totalBits = codewords.Length * 8;
            int bit = 0;

            for (int right = size - 1; right >= 1; right -= 2)
            {
                if (right == 6) right = 5;                    // 跳过时序列
                for (int vert = 0; vert < size; vert++)
                {
                    bool upward = ((right + 1) & 2) == 0;
                    for (int j = 0; j < 2; j++)
                    {
                        int x = right - j;
                        int y = upward ? size - 1 - vert : vert;
                        if (c.Fn[y, x]) continue;

                        bool dark = false;
                        if (bit < totalBits)
                            dark = ((codewords[bit >> 3] >> (7 - (bit & 7))) & 1) != 0;
                        c.M[y, x] = dark;
                        bit++;
                    }
                }
            }
        }

        // ───────────────────────────── 掩码 ─────────────────────────────

        private static bool MaskBit(int mask, int x, int y)
        {
            switch (mask)
            {
                case 0: return (y + x) % 2 == 0;
                case 1: return y % 2 == 0;
                case 2: return x % 3 == 0;
                case 3: return (y + x) % 3 == 0;
                case 4: return (y / 2 + x / 3) % 2 == 0;
                case 5: return (y * x) % 2 + (y * x) % 3 == 0;
                case 6: return ((y * x) % 2 + (y * x) % 3) % 2 == 0;
                default: return ((y + x) % 2 + (y * x) % 3) % 2 == 0;
            }
        }

        private static bool[,] ApplyMask(Canvas c, int mask)
        {
            int size = c.Size;
            var m = new bool[size, size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    bool v = c.M[y, x];
                    if (!c.Fn[y, x] && MaskBit(mask, x, y)) v = !v;
                    m[y, x] = v;
                }
            }
            return m;
        }

        private static int ChooseMask(Canvas c)
        {
            int best = 0, bestScore = int.MaxValue;
            for (int mask = 0; mask < 8; mask++)
            {
                bool[,] m = ApplyMask(c, mask);
                DrawFormatBits(m, c.Size, mask);
                if (IsVersion7Plus(c.Size)) DrawVersionBits(m, c.Size, (c.Size - 17) / 4);
                int score = Penalty(m);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = mask;
                }
            }
            return best;
        }

        private static bool IsVersion7Plus(int size)
        {
            return size >= 45;
        }

        /// <summary>标准的四条罚分规则（连续同色 / 2x2 同色 / 类定位图形 / 黑白失衡）。</summary>
        private static int Penalty(bool[,] m)
        {
            int size = m.GetLength(0);
            int score = 0;

            // 1) 行、列里连续同色 ≥5
            for (int y = 0; y < size; y++)
            {
                int run = 1;
                for (int x = 1; x < size; x++)
                {
                    if (m[y, x] == m[y, x - 1]) run++;
                    else
                    {
                        if (run >= 5) score += 3 + (run - 5);
                        run = 1;
                    }
                }
                if (run >= 5) score += 3 + (run - 5);
            }
            for (int x = 0; x < size; x++)
            {
                int run = 1;
                for (int y = 1; y < size; y++)
                {
                    if (m[y, x] == m[y - 1, x]) run++;
                    else
                    {
                        if (run >= 5) score += 3 + (run - 5);
                        run = 1;
                    }
                }
                if (run >= 5) score += 3 + (run - 5);
            }

            // 2) 2x2 同色块
            for (int y = 0; y < size - 1; y++)
            {
                for (int x = 0; x < size - 1; x++)
                {
                    bool v = m[y, x];
                    if (v == m[y, x + 1] && v == m[y + 1, x] && v == m[y + 1, x + 1]) score += 3;
                }
            }

            // 3) 形似定位图形：1011101 前后各带 4 个白（两种朝向都算）
            int[] pat = { 1, 0, 1, 1, 1, 0, 1, 0, 0, 0, 0 };
            int[] rev = { 0, 0, 0, 0, 1, 0, 1, 1, 1, 0, 1 };
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x + 11 <= size; x++)
                {
                    bool f = true, r = true;
                    for (int k = 0; k < 11; k++)
                    {
                        bool v = m[y, x + k] ? true : false;
                        if (v != (pat[k] != 0)) f = false;
                        if (v != (rev[k] != 0)) r = false;
                        if (!f && !r) break;
                    }
                    if (f) score += 40;
                    if (r) score += 40;
                }
            }
            for (int x = 0; x < size; x++)
            {
                for (int y = 0; y + 11 <= size; y++)
                {
                    bool f = true, r = true;
                    for (int k = 0; k < 11; k++)
                    {
                        bool v = m[y + k, x];
                        if (v != (pat[k] != 0)) f = false;
                        if (v != (rev[k] != 0)) r = false;
                        if (!f && !r) break;
                    }
                    if (f) score += 40;
                    if (r) score += 40;
                }
            }

            // 4) 黑模块比例偏离 50%
            int dark = 0;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    if (m[y, x]) dark++;
            int total = size * size;
            int imbalance = (Math.Abs(dark * 20 - total * 10) + total - 1) / total - 1;
            if (imbalance > 0) score += imbalance * 10;

            return score;
        }

        // ───────────────────────────── 格式 / 版本信息 ─────────────────────────────

        private static int FormatBits(int mask)
        {
            const int eccM = 0;                  // 纠错等级 M 的 2 位值是 00
            int data = (eccM << 3) | mask;
            int rem = data;
            for (int i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
            return ((data << 10) | rem) ^ 0x5412;
        }

        private static void DrawFormatBits(bool[,] m, int size, int mask)
        {
            int bits = FormatBits(mask);
            for (int i = 0; i <= 5; i++) m[i, 8] = Bit(bits, i);
            m[7, 8] = Bit(bits, 6);
            m[8, 8] = Bit(bits, 7);
            m[8, 7] = Bit(bits, 8);
            for (int i = 9; i < 15; i++) m[8, 14 - i] = Bit(bits, i);

            for (int i = 0; i < 8; i++) m[8, size - 1 - i] = Bit(bits, i);
            for (int i = 8; i < 15; i++) m[size - 15 + i, 8] = Bit(bits, i);
            m[size - 8, 8] = true;               // 恒黑
        }

        private static void DrawVersionBits(bool[,] m, int size, int version)
        {
            int rem = version;
            for (int i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
            int bits = (version << 12) | rem;

            for (int i = 0; i < 18; i++)
            {
                bool b = Bit(bits, i);
                int a = size - 11 + i % 3, c = i / 3;
                m[c, a] = b;
                m[a, c] = b;
            }
        }

        private static bool Bit(int value, int index)
        {
            return ((value >> index) & 1) != 0;
        }

        // ───────────────────────────── 位流 ─────────────────────────────

        private sealed class BitStream
        {
            private readonly List<bool> _bits = new List<bool>();

            public int Length { get { return _bits.Count; } }

            public void Put(int value, int count)
            {
                for (int i = count - 1; i >= 0; i--) _bits.Add(((value >> i) & 1) != 0);
            }

            public byte[] ToBytes()
            {
                var r = new byte[(_bits.Count + 7) / 8];
                for (int i = 0; i < _bits.Count; i++)
                    if (_bits[i]) r[i >> 3] |= (byte)(0x80 >> (i & 7));
                return r;
            }
        }
    }
}
