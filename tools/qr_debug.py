# -*- coding: utf-8 -*-
"""调试：打印 python-qrcode 实际用的模式与数据码字，跟 C# 对照。"""
import qrcode
import qrcode.util as qu
from qrcode.constants import ERROR_CORRECT_M

for text in ["HELLO PEAK 123", "https://music.163.com/login?codekey=" + "0123456789abcdef" * 4]:
    qr = qrcode.QRCode(error_correction=ERROR_CORRECT_M, border=0, mask_pattern=0)
    qr.add_data(text, optimize=0, mode=qu.MODE_8BIT_BYTE)
    qr.make(fit=True)
    buf = qu.create_data(qr.version, ERROR_CORRECT_M, qr.data_cache)
    print("text=%r version=%d first bytes=%s" % (
        text[:20], qr.version, " ".join("%02X" % b for b in buf[:8])))
