# -*- coding: utf-8 -*-
# 生成 1-bit 灰度 PNG，只指定一种滤波类型（参数: ft），验证解码器各环节
import zlib, struct, random, sys

ft_only = int(sys.argv[1])   # -1 = 轮换 0..4；0..4 = 固定
W = H = 33
random.seed(42)
px = [[0]*W for _ in range(H)]
for y in range(H):
    for x in range(W):
        px[y][x] = random.randint(0, 1)
px[0][0] = px[0][W-1] = px[H-1][0] = 1

def paeth(a, b, c):
    p = a + b - c
    pa, pb, pc = abs(p-a), abs(p-b), abs(p-c)
    if pa <= pb and pa <= pc: return a
    if pb <= pc: return b
    return c

stride = (W + 7) // 8
raw = bytearray()
prev = bytearray(stride)
for y in range(H):
    packed = bytearray(stride)
    for x in range(W):
        if px[y][x]:
            packed[x >> 3] |= 0x80 >> (x & 7)
    ft = (y % 5) if ft_only < 0 else ft_only
    line = bytearray()
    for i in range(stride):
        a = packed[i-1] if i >= 1 else 0      # 规范：a/b/c 都是"未滤波的原始字节"
        b = prev[i]
        c = prev[i-1] if i >= 1 else 0
        v = packed[i]
        if ft == 0: line.append(v)
        elif ft == 1: line.append((v - a) & 0xFF)
        elif ft == 2: line.append((v - b) & 0xFF)
        elif ft == 3: line.append((v - ((a + b) >> 1)) & 0xFF)
        else: line.append((v - paeth(a, b, c)) & 0xFF)
    raw.append(ft)
    raw.extend(line)
    prev = bytearray(packed)

def chunk(t, d):
    return struct.pack(">I", len(d)) + t + d + struct.pack(">I", zlib.crc32(t + d) & 0xFFFFFFFF)

ihdr = struct.pack(">IIBBBBB", W, H, 1, 0, 0, 0, 0)
tag = "mix" if ft_only < 0 else "ft%d" % ft_only
png = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr) + chunk(b"IDAT", zlib.compress(bytes(raw), 9)) + chunk(b"IEND", b"")
open(r"C:\Users\Admin\WorkBuddy\2026-11-07-20-32-32\_qrtest\test_%s.png" % tag, "wb").write(png)
with open(r"C:\Users\Admin\WorkBuddy\2026-11-07-20-32-32\_qrtest\expected_%s.txt" % tag, "w") as f:
    for y in range(H):
        f.write("".join(str(v) for v in px[y]) + "\n")
print("wrote test_%s.png" % tag)
