# -*- coding: utf-8 -*-
# 参考 PNG 解码（1-bit 灰度）+ 与 C# 解码差异分析
import zlib, struct, sys

path = sys.argv[1]
data = open(path, "rb").read()
assert data[:8] == b"\x89PNG\r\n\x1a\n"
pos = 8; idat = b""
while pos + 8 <= len(data):
    ln = struct.unpack(">I", data[pos:pos+4])[0]
    t = data[pos+4:pos+8]
    if t == b"IHDR":
        w, h, bd, ct, comp, filt, inter = struct.unpack(">IIBBBBB", data[pos+8:pos+8+13])
    elif t == b"IDAT":
        idat += data[pos+8:pos+8+ln]
    pos += 12 + ln
raw = zlib.decompress(idat)
stride = (w * bd + 7) // 8
print("IHDR: %dx%d bitDepth=%d colorType=%d interlace=%d stride=%d" % (w, h, bd, ct, inter, stride))
print("zlib 解压后 %d 字节, 需要 %d" % (len(raw), (stride+1)*h))

# 去滤波
un = bytearray()
prev = bytearray(stride)
rp = 0
for y in range(h):
    ft = raw[rp]; rp += 1
    line = bytearray(raw[rp:rp+stride]); rp += stride
    if ft == 1:
        for i in range(1, stride): line[i] = (line[i] + line[i-1]) & 0xFF
    elif ft == 2:
        for i in range(stride): line[i] = (line[i] + prev[i]) & 0xFF
    elif ft == 3:
        for i in range(stride):
            a = line[i-1] if i >= 1 else 0
            line[i] = (line[i] + ((a + prev[i]) >> 1)) & 0xFF
    elif ft == 4:
        for i in range(stride):
            a = line[i-1] if i >= 1 else 0
            b = prev[i]; c = prev[i-1] if i >= 1 else 0
            p = a + b - c
            pa, pb, pc = abs(p-a), abs(p-b), abs(p-c)
            pr = a if (pa <= pb and pa <= pc) else (b if pb <= pc else c)
            line[i] = (line[i] + pr) & 0xFF
    un.extend(line)
    prev = line

# 期望矩阵
base = path[:-4] if path.endswith(".png") else path
if base.endswith("test1bit"):
    exp_path = base.replace("test1bit", "expected") + ".txt"
else:
    exp_path = base.replace("test_ft", "expected_ft") + ".txt"
exp = [l.strip() for l in open(exp_path) if l.strip()]
assert len(exp) == h, "期望行数 %d != %d" % (len(exp), h)

# 位展开并对比
diff = 0; first = None
for y in range(h):
    row = []
    for x in range(w):
        bitpos = x * bd
        v = (un[y*stride + (bitpos >> 3)] >> (8 - bd - (bitpos & 7))) & ((1 << bd) - 1)
        want = 1 if exp[y][x] == '1' else 0
        row.append(v)
        if v != want:
            diff += 1
            if first is None: first = (x, y)
print("Python 参考解码 像素差异:", diff, "/", w*h, "首个错位:", first)
if diff and diff < 40:
    for y in range(min(h, 6)):
        got = "".join(str((un[y*stride + (x*bd >> 3)] >> (8 - bd - (x*bd & 7))) & 1) for x in range(w))
        print("y=%d 期望 %s\n     实得 %s" % (y, exp[y], got))
