"""把网易扫码登录链接渲染成二维码图片，省得再开浏览器找二维码。

用法：
    python make_login_qr.py <codekey> [输出路径]
    python make_login_qr.py                 # 自动从 PeakMusic/_login.log 里抓最新的 codekey

链接格式固定为 https://music.163.com/login?codekey=<unikey>，
网易云 App 扫这条链接即可拉起授权确认。
"""

import io
import os
import re
import sys

LOG = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "_login.log")
DEFAULT_OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "login-qr.png")


def read_codekey_from_log():
    path = os.path.normpath(LOG)
    if not os.path.exists(path):
        return None
    # 沙盒控制台输出是 GBK，日志文件可能被判成 UTF-16，两种都试
    for enc in ("utf-16", "utf-8", "gbk"):
        try:
            with io.open(path, "r", encoding=enc, errors="ignore") as f:
                text = f.read().replace("\x00", "")
        except Exception:
            continue
        found = re.findall(r"codekey=([0-9a-fA-F\-]{36})", text)
        if found:
            return found[-1]
    return None


def main():
    key = None
    out = os.path.normpath(DEFAULT_OUT)

    # 参数顺序随意：命中 codekey / 36 位 key 的当 key，.png 结尾的当输出路径
    for a in sys.argv[1:]:
        if a.lower().endswith(".png"):
            out = os.path.normpath(a)
            continue
        m = re.search(r"codekey=([0-9a-fA-F\-]{36})", a)
        if m:
            key = m.group(1)
        elif re.match(r"^[0-9a-fA-F\-]{36}$", a):
            key = a

    if key is None:
        key = read_codekey_from_log()

    if not key:
        print("没找到 codekey，先跑 PeakMusic.Sandbox.exe login")
        return 2

    url = "https://music.163.com/login?codekey=" + key

    import qrcode
    from qrcode.constants import ERROR_CORRECT_M

    qr = qrcode.QRCode(version=None, error_correction=ERROR_CORRECT_M, box_size=12, border=3)
    qr.add_data(url)
    qr.make(fit=True)
    img = qr.make_image(fill_color="black", back_color="white").convert("RGB")
    img.save(out)

    print("url=" + url)
    print("png=" + out)
    print("size=%dx%d" % img.size)
    return 0


if __name__ == "__main__":
    sys.exit(main())
