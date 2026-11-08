using System;
using System.IO;
using System.IO.Compression;

namespace PeakMusic.Core.Sources
{
    /// <summary>
    /// 极简 PNG 解码器，只覆盖 QQ 二维码需要的子集：
    ///   8-bit 颜色（RGB / RGBA / 灰度 / 索引色），非交错（interlace = 0），
    ///   zlib 压缩的数据用 <see cref="DeflateStream"/> 解。
    ///
    /// 为什么不直接 Unity 的 Texture2D.LoadImage：那个方法在 UnityEngine.ImageConversionModule，
    /// 而该模块是 netstandard 2.1 编译的，net472 工程引用它会在编译期炸 CS1705。
    /// 所以这里自己解码出 RGBA 字节，再喂给 Texture2D.SetPixels32。
    /// </summary>
    public static class PngDecoder
    {
        public sealed class Image
        {
            public int Width;
            public int Height;
            /// <summary>RGBA 字节，每像素 4 字节，长度 = Width * Height * 4。行序从上到下。</summary>
            public byte[] Pixels;
        }

        public static Image Decode(byte[] png)
        {
            if (png == null || png.Length < 8) return null;
            if (png[0] != 0x89 || png[1] != 'P' || png[2] != 'N' || png[3] != 'G') return null;

            int pos = 8;
            int width = 0, height = 0, bitDepth = 0, colorType = 0;
            byte[] idat = null;
            byte[] plte = null;
            byte[] trns = null;

            // 遍历 chunk
            while (pos + 8 <= png.Length)
            {
                int len = ReadInt32(png, pos);
                string type = new string(new[] { (char)png[pos + 4], (char)png[pos + 5], (char)png[pos + 6], (char)png[pos + 7] });
                int dataStart = pos + 8;

                if (type == "IHDR")
                {
                    width = ReadInt32(png, dataStart);
                    height = ReadInt32(png, dataStart + 4);
                    bitDepth = png[dataStart + 8];
                    colorType = png[dataStart + 9];
                }
                else if (type == "IDAT")
                {
                    idat = Append(idat, png, dataStart, len);
                }
                else if (type == "PLTE")
                {
                    plte = new byte[len];
                    Array.Copy(png, dataStart, plte, 0, len);
                }
                else if (type == "tRNS")
                {
                    trns = new byte[len];
                    Array.Copy(png, dataStart, trns, 0, len);
                }
                else if (type == "IEND")
                {
                    break;
                }

                pos = dataStart + len + 4;   // 跳过 data + CRC
            }

            if (width <= 0 || height <= 0 || idat == null) return null;

            int channels;
            switch (colorType)
            {
                case 0: channels = 1; break;    // 灰度
                case 2: channels = 3; break;    // RGB
                case 3: channels = 1; break;    // 索引色
                case 4: channels = 2; break;    // 灰度 + alpha
                case 6: channels = 4; break;    // RGBA
                default: return null;
            }

            // QQ 的 ptqrshow 返回 1-bit 灰度 PNG（bitDepth=1），所以必须支持子字节位深：
            // 1/2/4-bit 时每像素不足 1 字节，按位打包；滤波仍按字节算。
            if (bitDepth != 1 && bitDepth != 2 && bitDepth != 4 && bitDepth != 8 && bitDepth != 16) return null;
            int bytesPerPixel = bitDepth >= 8 ? channels * bitDepth / 8 : 1;
            int stride = bitDepth >= 8
                ? width * bytesPerPixel
                : (width * channels * bitDepth + 7) / 8;
            byte[] raw;   // 去滤波后的原始扫描行（每行前面 1 字节滤波类型 + stride 字节数据）

            // IDAT 是 zlib 包装（2 字节头 + deflate + 4 字节校验）。.NET 的 DeflateStream
            // 对 zlib 头的兼容各版本不一，稳妥起见自己剥头：CMF=0x78 且 (CMF&0x0F)==8 即 deflate。
            int idatStart = 0;
            if (idat.Length >= 2 && (idat[0] & 0x0F) == 8 && ((idat[0] << 8) + idat[1]) % 31 == 0)
                idatStart = 2;

            using (var ms = new MemoryStream(idat, idatStart, idat.Length - idatStart))
            using (var inflater = new DeflateStream(ms, CompressionMode.Decompress))
            {
                raw = ReadAll(inflater, (stride + 1) * height);
            }

            if (raw == null || raw.Length < (stride + 1) * height) return null;

            // 去滤波（PNG 的 5 种滤波类型：0 None / 1 Sub / 2 Up / 3 Average / 4 Paeth）
            byte[] unfiltered = new byte[stride * height];
            int rawPos = 0;
            for (int y = 0; y < height; y++)
            {
                int filter = raw[rawPos++];
                int row = y * stride;
                for (int x = 0; x < stride; x++)
                {
                    int a = x >= bytesPerPixel ? unfiltered[row + x - bytesPerPixel] : 0;
                    int b = y > 0 ? unfiltered[row - stride + x] : 0;
                    int c = (x >= bytesPerPixel && y > 0) ? unfiltered[row - stride + x - bytesPerPixel] : 0;
                    int val = raw[rawPos++];

                    switch (filter)
                    {
                        case 1: val = (val + a) & 0xFF; break;
                        case 2: val = (val + b) & 0xFF; break;
                        case 3: val = (val + ((a + b) >> 1)) & 0xFF; break;
                        case 4: val = (val + Paeth(a, b, c)) & 0xFF; break;
                    }
                    unfiltered[row + x] = (byte)val;
                }
            }

            // 转 RGBA
            var outPixels = new byte[width * height * 4];
            int o = 0;
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = y * width + x;
                byte r, g, b, a = 255;

                if (bitDepth < 8)
                {
                    // 子字节位深：按位展开（QQ 二维码就是 1-bit 灰度）。
                    // 必须按行算位偏移——行尾有填充位（stride*8 ≥ width*bitDepth），
                    // 不能把整张图当连续位流，否则第 2 行起全部错位（r18 修的扫码乱码就是这个）。
                    int v = Sample(unfiltered, y * stride, x * channels, bitDepth);

                    if (colorType == 3)   // 索引色
                    {
                        if (plte != null && v * 3 + 2 < plte.Length)
                        {
                            r = plte[v * 3];
                            g = plte[v * 3 + 1];
                            b = plte[v * 3 + 2];
                        }
                        else { r = g = b = 0; }
                        if (trns != null && v < trns.Length) a = trns[v];
                    }
                    else                  // 灰度：把最大位值缩放到 255（1-bit: 0/1 → 0/255）
                    {
                        int max = (1 << bitDepth) - 1;
                        byte s = max > 0 ? (byte)(v * 255 / max) : (byte)0;
                        r = g = b = s;
                    }
                }
                else if (colorType == 3)   // 8-bit 索引色
                {
                    int idx = unfiltered[i * channels];
                    if (plte != null && idx * 3 + 2 < plte.Length)
                    {
                        r = plte[idx * 3];
                        g = plte[idx * 3 + 1];
                        b = plte[idx * 3 + 2];
                    }
                    else { r = g = b = 0; }
                    if (trns != null && idx < trns.Length) a = trns[idx];
                }
                else if (channels >= 3)
                {
                    int p = i * channels * (bitDepth / 8);
                    r = unfiltered[p];
                    g = unfiltered[p + bitDepth / 8];
                    b = unfiltered[p + 2 * (bitDepth / 8)];
                    if (channels == 4) a = unfiltered[p + 3 * (bitDepth / 8)];
                }
                else   // 灰度
                {
                    int p = (y * stride + x * channels) * (bitDepth / 8);
                    r = g = b = unfiltered[p];
                    if (channels == 2) a = unfiltered[p + bitDepth / 8];
                }

                outPixels[o++] = r;
                outPixels[o++] = g;
                outPixels[o++] = b;
                outPixels[o++] = a;
            }

            return new Image { Width = width, Height = height, Pixels = outPixels };
        }

        /// <summary>从打包字节里取一个位深为 bitDepth 的样本（高位在前，PNG 规范）。
        /// rowStart = 该行在 data 里的字节起点，col = 该行内第几个样本。</summary>
        private static int Sample(byte[] data, int rowStart, int col, int bitDepth)
        {
            int bitPos = rowStart * 8 + col * bitDepth;
            int byteIdx = bitPos >> 3;
            int shift = 8 - bitDepth - (bitPos & 7);
            return (data[byteIdx] >> shift) & ((1 << bitDepth) - 1);
        }

        private static int Paeth(int a, int b, int c)
        {
            int p = a + b - c;
            int pa = Math.Abs(p - a);
            int pb = Math.Abs(p - b);
            int pc = Math.Abs(p - c);
            if (pa <= pb && pa <= pc) return a;
            if (pb <= pc) return b;
            return c;
        }

        private static int ReadInt32(byte[] buf, int off)
        {
            return (buf[off] << 24) | (buf[off + 1] << 16) | (buf[off + 2] << 8) | buf[off + 3];
        }

        private static byte[] Append(byte[] dst, byte[] src, int off, int len)
        {
            if (dst == null) dst = new byte[0];
            var r = new byte[dst.Length + len];
            Array.Copy(dst, 0, r, 0, dst.Length);
            Array.Copy(src, off, r, dst.Length, len);
            return r;
        }

        private static byte[] ReadAll(Stream s, int expected)
        {
            using (var ms = new MemoryStream())
            {
                var buf = new byte[8192];
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    ms.Write(buf, 0, n);
                    if (ms.Length > expected * 2) break;   // 防御：解出异常大就停
                }
                return ms.ToArray();
            }
        }
    }
}
