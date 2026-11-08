"""阶段 1 测试用的最小 HTTP 服务：支持 Range，行为尽量贴近真实 CDN。

为什么不用 `python -m http.server`：它不认 Range 头。
真实音乐 CDN（网易 m701 / m801）都支持 Range，Unity 做 seek 时会依赖它，
所以测试用的服务也必须支持，否则测出来的行为是假的。

用法：
    python serve.py            # 127.0.0.1:8765，不限速
    python serve.py 9000       # 换端口
    python serve.py 8765 512   # 限速 512 KB/s，用来验证 streamAudio 是不是真的"边下边播"

为什么要限速：本机 125 MB/s，3.7 MB 的曲子 30 ms 就下完了，
此时"秒开"和"整曲下完再播"两种实现的表现完全一样，测不出区别。
限速到 512 KB/s 后，整曲要 7 秒；如果起播仍然是一瞬间，才说明流式真的生效。
"""

import http.server
import os
import re
import sys
import time

ROOT = os.path.dirname(os.path.abspath(__file__))
PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 8765
RATE = int(sys.argv[2]) * 1024 if len(sys.argv) > 2 else 0   # bytes/s，0 = 不限

CONTENT_TYPES = {
    ".mp3": "audio/mpeg",
    ".ogg": "audio/ogg",
    ".wav": "audio/wav",
    ".m4a": "audio/mp4",
    ".jpg": "image/jpeg",
    ".png": "image/png",
}


class RangeHandler(http.server.BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def do_GET(self):
        rel = self.path.split("?")[0].lstrip("/")
        path = os.path.normpath(os.path.join(ROOT, rel))

        if not path.startswith(ROOT) or not os.path.isfile(path):
            self.send_error(404, "not found")
            return

        size = os.path.getsize(path)
        if size == 0:
            self.send_error(416, "empty file")
            return

        start, end, partial = 0, size - 1, False
        rng = self.headers.get("Range")
        if rng:
            m = re.match(r"bytes=(\d+)-(\d*)", rng.strip())
            if m:
                start = int(m.group(1))
                end = int(m.group(2)) if m.group(2) else size - 1
                end = min(end, size - 1)
                if start > end:
                    self.send_error(416, "bad range")
                    return
                partial = True

        length = end - start + 1
        ctype = CONTENT_TYPES.get(os.path.splitext(path)[1].lower(), "application/octet-stream")

        self.send_response(206 if partial else 200)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(length))
        self.send_header("Accept-Ranges", "bytes")
        self.send_header("Cache-Control", "no-store")
        if partial:
            self.send_header("Content-Range", "bytes %d-%d/%d" % (start, end, size))
        self.end_headers()

        sent = 0
        t0 = time.monotonic()
        with open(path, "rb") as f:
            f.seek(start)
            remaining = length
            while remaining > 0:
                chunk = f.read(min(65536, remaining))
                if not chunk:
                    break
                try:
                    self.wfile.write(chunk)
                except (BrokenPipeError, ConnectionResetError):
                    break
                remaining -= len(chunk)
                sent += len(chunk)
                if RATE > 0:
                    # 按"已发字节数 / 目标速率"卡时间，多发的就睡回来
                    ahead = sent / RATE - (time.monotonic() - t0)
                    if ahead > 0:
                        time.sleep(ahead)

        sys.stderr.write("GET %s -> %s %d/%d bytes%s\n" % (
            self.path, "206" if partial else "200", sent, length,
            ("  rate=%dKB/s" % (RATE // 1024)) if RATE > 0 else ""))
        sys.stderr.flush()

    def log_message(self, fmt, *args):
        pass


if __name__ == "__main__":
    srv = http.server.ThreadingHTTPServer(("127.0.0.1", PORT), RangeHandler)
    print("serving %s on http://127.0.0.1:%d  rate=%s" % (
        ROOT, PORT, ("%d KB/s" % (RATE // 1024)) if RATE > 0 else "unlimited"))
    sys.stdout.flush()
    srv.serve_forever()
