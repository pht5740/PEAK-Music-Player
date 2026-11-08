# -*- coding: utf-8 -*-
# 对照实验：带无效 MUSIC_U 的 eapi unikey 请求是否变空
import hashlib, json, random, time, urllib.request, urllib.parse
from Crypto.Cipher import AES

EAPI_KEY = b"e82ckenh8dichen8"

def rand_hex(n):
    return ''.join(random.choice('0123456789abcdef') for _ in range(n))

def eapi_encrypt(url_path, text):
    message = "nobody" + url_path + "use" + text + "md5forencrypt"
    digest = hashlib.md5(message.encode()).hexdigest()
    data_str = url_path + "-36cd479b6b5-" + text + "-36cd479b6b5-" + digest
    pad = 16 - len(data_str) % 16
    raw = data_str + chr(pad) * pad
    return AES.new(EAPI_KEY, AES.MODE_ECB).encrypt(raw.encode()).hex()

def run(with_music_u):
    device_id = ''.join(random.choice('0123456789ABCDEF') for _ in range(26)) + "\\r"
    now = str(int(time.time()))
    header = {
        "osver": "Microsoft-Windows-11-Professional-build-26100-64bit",
        "deviceId": device_id, "os": "pc", "appver": "3.1.12.204072",
        "versioncode": "140", "mobilename": "", "buildver": now,
        "resolution": "1920x1080", "__csrf": "", "channel": "netease",
        "requestId": str(int(time.time()*1000)) + "_0001",
    }
    if with_music_u:
        header["MUSIC_U"] = "DEADBEEFDEADBEEFDEADBEEFDEADBEEF"
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
        }, method="POST")
    try:
        r = urllib.request.urlopen(req, timeout=20)
        print("with MUSIC_U=" + str(with_music_u), "STATUS", r.status, "BODY", r.read()[:200])
    except urllib.error.HTTPError as e:
        print("with MUSIC_U=" + str(with_music_u), "HTTPError", e.code, e.read()[:200])

run(True)
run(False)
