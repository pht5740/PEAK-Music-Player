# -*- coding: utf-8 -*-
# 复现 PeakMusic r31 的 eapi unikey 请求，验证加密与头
import hashlib, json, random, string, urllib.request, urllib.parse
from Crypto.Cipher import AES  # pip pycryptodex? 用 pycryptodome

EAPI_KEY = b"e82ckenh8dichen8"

def rand_hex(n):
    return ''.join(random.choice('0123456789abcdef') for _ in range(n))

def eapi_encrypt(url_path, text):
    message = "nobody" + url_path + "use" + text + "md5forencrypt"
    digest = hashlib.md5(message.encode()).hexdigest()
    data_str = url_path + "-36cd479b6b5-" + text + "-36cd479b6b5-" + digest
    pad = 16 - len(data_str) % 16
    raw = data_str + chr(pad) * pad
    cipher = AES.new(EAPI_KEY, AES.MODE_ECB)
    out = cipher.encrypt(raw.encode())
    return out.hex()

def rand_upper_hex(n):
    return ''.join(random.choice('0123456789ABCDEF') for _ in range(n))

device_id = rand_upper_hex(26) + "\\r"
now = str(int(__import__('time').time()))
header = {
    "osver": "Microsoft-Windows-11-Professional-build-26100-64bit",
    "deviceId": device_id,
    "os": "pc",
    "appver": "3.1.12.204072",
    "versioncode": "140",
    "mobilename": "",
    "buildver": now,
    "resolution": "1920x1080",
    "__csrf": "",
    "channel": "netease",
    "requestId": str(int(__import__('time').time()*1000)) + "_0001",
}
payload = {"type": 3, "header": header, "e_r": False}
text = json.dumps(payload, separators=(',', ':'))
path = "/api/login/qrcode/unikey"
params = eapi_encrypt(path, text)

cookie = "; ".join(k + "=" + urllib.parse.quote(v, safe='') for k, v in header.items())
req = urllib.request.Request(
    "https://interfacepc.music.163.com/eapi/login/qrcode/unikey",
    data=urllib.parse.urlencode({"params": params}).encode(),
    headers={
        "User-Agent": "Mozilla/5.0 (Windows NT 10.0; WOW64) AppleWebKit/537.36 (KHTML, like Gecko) Safari/537.36 Chrome/91.0.4472.164 NeteaseMusicDesktop/3.0.18.203152",
        "Cookie": cookie,
        "Content-Type": "application/x-www-form-urlencoded",
    }, method="POST")
try:
    resp = urllib.request.urlopen(req, timeout=20)
    body = resp.read()
    print("STATUS", resp.status)
    print("ENC", resp.headers.get("Content-Encoding"))
    print("BODY[:400]", body[:400])
except urllib.error.HTTPError as e:
    print("HTTPError", e.code)
    print("BODY[:400]", e.read()[:400])
