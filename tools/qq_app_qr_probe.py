#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""QQ 音乐 App 扫码登录协议验证脚本（只验证握手/订阅，不等扫码）。

流程对照 luren-dc/QQMusicApi:
  1. musicu.fcg music.login.LoginServer.CreateQRCode -> qrcodeID + base64 PNG
  2. MQTT 5 over WSS mu.y.qq.com:443/ws/handshake
     CONNECT properties: auth-method=pass, user-props(tmeAppID/business/hashTag/clientTag/userID)
  3. SUBSCRIBE management.qrcode_login/{qrcodeID} user-props(authorization=tmelogin,pubsub=unicast)
  4. 等 PUBLISH（type=scanned/canceled/timeout/loginFailed/cookies）
"""
import base64
import json
import random
import ssl
import sys
import threading
import time

import paho.mqtt.client as mqtt
from paho.mqtt.packettypes import PacketTypes
from paho.mqtt.properties import Properties

try:
    import requests
except ImportError:
    requests = None
    import urllib.request

FCG = "https://u.y.qq.com/cgi-bin/musicu.fcg"
UA = ("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
      "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36")


def create_qr():
    body = {
        "req_0": {
            "module": "music.login.LoginServer",
            "method": "CreateQRCode",
            "param": {"tmeAppID": "qqmusic", "ct": 24, "cv": 4747474},
        },
        "comm": {"ct": 23, "cv": 0, "tmeAppID": "qqmusic"},
    }
    if requests:
        r = requests.post(FCG, json=body,
                          headers={"Referer": "https://y.qq.com/", "User-Agent": UA},
                          timeout=15)
        j = r.json()
    else:
        req = urllib.request.Request(FCG, data=json.dumps(body).encode(), headers={
            "Referer": "https://y.qq.com/", "User-Agent": UA,
            "Content-Type": "application/json"})
        j = json.loads(urllib.request.urlopen(req, timeout=15).read())
    d = j["req_0"]["data"]
    return d["qrcodeID"], d["qrcode"]


def main():
    qrid, qrcode = create_qr()
    print("qrcodeID =", qrid)
    png = base64.b64decode(qrcode.split(",")[-1])
    with open("qr_test.png", "wb") as f:
        f.write(png)
    print("qr png -> qr_test.png", len(png), "bytes（用 QQ 音乐 App 扫这个图）")

    got = threading.Event()
    results = []

    def on_connect(client, userdata, flags, reason_code, props):
        print("[mqtt] CONNACK reason=%s props=%s" % (reason_code, props))
        sub_props = Properties(PacketTypes.SUBSCRIBE)
        sub_props.UserProperty = [("authorization", "tmelogin"), ("pubsub", "unicast")]
        topic = "management.qrcode_login/%s" % qrid
        client.subscribe(topic, qos=0, properties=sub_props)
        print("[mqtt] subscribed", topic)

    def on_subscribe(client, userdata, mid, reason_codes, props):
        print("[mqtt] SUBACK mid=%s codes=%s" % (mid, reason_codes))

    def on_message(client, userdata, msg):
        up = {}
        p = getattr(msg, "properties", None)
        if p is not None:
            for k, v in (getattr(p, "UserProperty", None) or []):
                up[str(k)] = str(v)
        print("[mqtt] PUBLISH type=%s payload=%s" % (up.get("type"), msg.payload[:400]))
        results.append((up.get("type"), msg.payload))
        if up.get("type") in ("cookies", "canceled", "timeout", "loginFailed"):
            got.set()

    def on_disconnect(client, userdata, flags, reason_code, props):
        print("[mqtt] DISCONNECT reason=%s" % reason_code)

    cid = "%d%d" % (int(time.time() * 1000), random.randint(1000, 9999))
    client = mqtt.Client(callback_api_version=mqtt.CallbackAPIVersion.VERSION2,
                         client_id=cid, protocol=mqtt.MQTTv5,
                         transport="websockets")
    client.tls_set_context(ssl.create_default_context())
    client.ws_set_options(path="/ws/handshake", headers={
        "Origin": "https://y.qq.com",
        "Referer": "https://y.qq.com/",
        "User-Agent": UA,
    })
    client.on_connect = on_connect
    client.on_subscribe = on_subscribe
    client.on_message = on_message
    client.on_disconnect = on_disconnect

    props = Properties(PacketTypes.CONNECT)
    props.AuthenticationMethod = "pass"
    props.UserProperty = [
        ("tmeAppID", "qqmusic"),
        ("business", "management"),
        ("hashTag", qrid),
        ("clientTag", "management.user"),
        ("userID", qrid),
    ]

    print("[mqtt] connecting wss://mu.y.qq.com:443/ws/handshake ...")
    try:
        client.connect_async("mu.y.qq.com", 443, keepalive=45,
                             clean_start=True, properties=props)
    except Exception as e:
        print("[mqtt] connect_async FAILED:", e)
        sys.exit(1)
    client.loop_start()

    # 等 120 秒看是否有 CONNACK / 消息（没有扫码则无 PUBLISH，但握手+订阅应打印）
    got.wait(120)
    if results:
        for t, p in results:
            print("RESULT type=%s" % t)
            if t == "cookies":
                j = json.loads(p)
                ck = j.get("cookies", {})
                print("  qqmusic_uin =", ck.get("qqmusic_uin", {}).get("value"))
                print("  qqmusic_key =", (ck.get("qqmusic_key", {}).get("value") or "")[:24], "...")
    else:
        print("120s 内没有收到 PUBLISH（正常：没有人扫码）")
    client.loop_stop()
    client.disconnect()
    print("done")


if __name__ == "__main__":
    main()
