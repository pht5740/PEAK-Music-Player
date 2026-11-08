# -*- coding: utf-8 -*-
"""生成 QR 参考数据：给 C# 编码器做逐模块比对。

用法：
  python qr_reference.py tables          # 打印 ECC-M 的分块表 / 对齐图形位置
  python qr_reference.py matrices        # 生成 tests/*.txt 参考矩阵
"""
import os
import sys
import json

import qrcode
import qrcode.util as qu
from qrcode.constants import ERROR_CORRECT_M


def rs_blocks(version, ecc):
    return qu.base.rs_blocks(version, ecc)


def pattern_position(version):
    return qu.PATTERN_POSITION_TABLE[version]

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "qr_tests")


def dump_tables():
    lines = []
    for v in range(1, 11):
        blocks = rs_blocks(v, ERROR_CORRECT_M)
        ec = blocks[0].total_count - blocks[0].data_count
        total = sum(b.total_count for b in blocks)
        data = sum(b.data_count for b in blocks)
        groups = {}
        for b in blocks:
            groups.setdefault(b.data_count, 0)
            groups[b.data_count] += 1
        parts = ["%d x %d" % (cnt, dc) for dc, cnt in sorted(groups.items())]
        lines.append("V%-2d total=%-4d data=%-4d ecPerBlock=%-3d blocks=%s align=[%s]" % (
            v, total, data, ec, " + ".join(parts), ",".join(str(x) for x in pattern_position(v))))
    return "\n".join(lines)


CASES = {
    "url": "https://music.163.com/login?codekey=" + "0123456789abcdef" * 4,
    "ascii": "HELLO PEAK 123",
    "cjk": "网易云音乐扫码登录测试",
}


def dump_matrices():
    if not os.path.isdir(OUT):
        os.makedirs(OUT)
    manifest = []
    for name, text in CASES.items():
        for mask in range(8):
            qr = qrcode.QRCode(error_correction=ERROR_CORRECT_M, border=0,
                               mask_pattern=mask)
            qr.add_data(qu.QRData(text.encode("utf-8"), mode=qu.MODE_8BIT_BYTE))
            qr.make(fit=True)
            rows = ["".join("1" if cell else "0" for cell in row) for row in qr.modules]
            path = os.path.join(OUT, "%s_m%d.txt" % (name, mask))
            with open(path, "w", encoding="utf-8") as f:
                f.write("version=%d\nmask=%d\n" % (qr.version, mask))
                f.write("\n".join(rows))
            manifest.append({"case": name, "mask": mask, "version": qr.version,
                             "size": len(rows), "text": text, "file": path})
    with open(os.path.join(OUT, "manifest.json"), "w", encoding="utf-8") as f:
        json.dump(manifest, f, ensure_ascii=False, indent=1)
    return "%d files -> %s" % (len(manifest), OUT)


if __name__ == "__main__":
    cmd = sys.argv[1] if len(sys.argv) > 1 else "tables"
    if cmd == "tables":
        print(dump_tables())
    else:
        print(dump_matrices())
