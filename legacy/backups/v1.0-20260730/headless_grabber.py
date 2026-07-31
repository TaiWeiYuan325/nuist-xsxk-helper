# -*- coding: utf-8 -*-
"""
无头抢课程序（不依赖网页，高峰期网页打不开时使用）

用法:
    python headless_grabber.py xsxk-session.json
    python headless_grabber.py xsxk-session.json --base http://localhost:8655/xsxk   # 本地模拟测试

工作流程:
    1. 读取油猴脚本"导出会话"生成的 JSON（token、课程参数、志愿、开抢时间）
    2. POST /web/now 校时（3 次采样取最小延迟）
    3. POST /elective/clazz/list 刷新课程参数（secretVal 可能过期）
    4. 等到开抢时间（按服务器时间），循环提交 /elective/clazz/add
    5. 满员/冲突立即切下一个备选；成功即停止

只使用 Python 标准库，无需安装任何依赖。
"""
import json
import sys
import time
import urllib.request
import urllib.parse
import urllib.error

RETRYABLE = ("暂未开始", "未开始", "未开放", "系统繁忙", "稍后", "队列拥堵", "请求频繁", "超时", "结束")
AUTH_MSGS = ("未登录", "登录失效", "认证失败", "token失效", "token过期")


def log(msg):
    t = time.strftime("%H:%M:%S") + f".{int(time.time() * 1000) % 1000:03d}"
    print(f"[{t}] {msg}", flush=True)


class Api:
    def __init__(self, base, headers):
        self.base = base.rstrip("/")
        self.headers = dict(headers or {})

    def post(self, path, form=None, json_body=None, timeout=10):
        data = None
        hdrs = dict(self.headers)
        if json_body is not None:
            data = json.dumps(json_body).encode("utf-8")
            hdrs["Content-Type"] = "application/json"
        elif form is not None:
            data = urllib.parse.urlencode(form).encode("utf-8")
            hdrs["Content-Type"] = "application/x-www-form-urlencoded"
        req = urllib.request.Request(self.base + path, data=data, headers=hdrs, method="POST")
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            return resp.read().decode("utf-8", "ignore")


def judge(text):
    """与油猴脚本 judge() 一致的分类"""
    if not text or text.lstrip().startswith("<"):
        return "badHtml", "服务器返回了HTML而非JSON"
    try:
        j = json.loads(text)
    except ValueError:
        return "fail", text[:120]
    msg = str(j.get("msg") or "")[:120]
    code = j.get("code")
    if code == 200 or "成功" in msg:
        return "success", msg
    if "满" in msg or "容量" in msg:
        return "full", msg
    if any(k in msg for k in ("冲突", "已选", "重复", "超过")):
        return "impossible", msg
    if any(k.lower() in msg.lower() for k in AUTH_MSGS):
        return "auth", msg
    if any(k in msg for k in RETRYABLE):
        return "notOpen", msg
    return "fail", msg or "未知响应"


def sync_time(api):
    """3 次采样，保留 RTT 最小的一次；返回 (offset_ms, rtt_ms, online)"""
    best = None
    for _ in range(3):
        try:
            t0 = time.time()
            j = json.loads(api.post("/web/now", form={}))
            rtt = (time.time() - t0) * 1000
            if j.get("code") == 200 and (j.get("data") or {}).get("currentTime"):
                mid = t0 * 1000 + rtt / 2
                offset = j["data"]["currentTime"] - mid
                online = j["data"].get("onlineCount")
                if best is None or rtt < best[1]:
                    best = (offset, rtt, online)
        except Exception:
            pass
        time.sleep(0.2)
    return best


def walk_rows(obj, out):
    if isinstance(obj, dict):
        if obj.get("JXBID") and obj.get("secretVal"):
            out[obj["JXBID"]] = obj
        for v in obj.values():
            walk_rows(v, out)
    elif isinstance(obj, list):
        for v in obj:
            walk_rows(v, out)


def refresh_courses(api, list_body):
    """用导出的列表请求载荷刷新课程参数库；失败返回 None"""
    if not list_body:
        return None
    try:
        payload = json.loads(list_body)
    except ValueError:
        return None
    try:
        j = json.loads(api.post("/elective/nuist/clazz/list", json_body=payload))
    except Exception as e:
        log(f"⚠️ 刷新课程列表失败: {e}")
        return None
    rows = {}
    walk_rows(j, rows)
    return rows


def resolve_courses(keywords, rows, fallback_db):
    """把志愿关键词解析为 (keyword, JXBID, secretVal)，优先用实时列表，其次用导出快照"""
    resolved = []
    for kw in keywords:
        hit = None
        for cid, row in rows.items():
            if kw in json.dumps(row, ensure_ascii=False):
                hit = (kw, cid, row["secretVal"])
                break
        if not hit:
            for cid, entry in (fallback_db or {}).items():
                if kw in entry.get("json", ""):
                    hit = (kw, cid, entry["secretVal"])
                    log(f"「{kw}」实时列表未找到，使用导出时的参数快照")
                    break
        if hit:
            resolved.append(hit)
        else:
            log(f"⚠️ 志愿「{kw}」在课程数据中找不到，已跳过")
    return resolved


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    opts = {a.split("=")[0][2:]: a.split("=")[1] for a in sys.argv[1:] if a.startswith("--") and "=" in a}
    if not args:
        print(__doc__)
        sys.exit(1)

    with open(args[0], encoding="utf-8") as f:
        sess = json.load(f)

    base = opts.get("base") or sess.get("baseUrl") or "https://xsxk.nuist.edu.cn/xsxk"
    api = Api(base, sess.get("headers"))
    keywords = sess.get("courses") or []
    interval = (sess.get("retryInterval") or 400) / 1000.0

    log(f"目标: {base}")
    log("校时中…")
    st = sync_time(api)
    offset = 0.0
    if st:
        offset = st[0] / 1000.0
        log(f"校时完成: 延迟 {st[1]:.0f}ms · 在线 {st[2]} 人 · 偏移 {st[0]:+.0f}ms")
    else:
        log("⚠️ 校时失败，按本机时间继续")

    log("刷新课程参数…")
    rows = refresh_courses(api, sess.get("listBody")) or {}
    if rows:
        log(f"课程库已刷新（{len(rows)} 门）")
    courses = resolve_courses(keywords, rows, sess.get("courseDb"))
    if not courses:
        log("❌ 没有可抢的课程，退出")
        sys.exit(1)
    clazz_type = sess.get("listType") or ""
    log("志愿队列: " + " → ".join(k for k, _, _ in courses))

    # 等待开抢时间（按服务器时间）
    start_at = sess.get("startAt")
    if start_at:
        target = None
        s = start_at.strip().replace("/", "-").replace("T", " ")
        for fmt in ("%Y-%m-%d %H:%M:%S", "%Y-%m-%d %H:%M"):
            try:
                target = time.mktime(time.strptime(s, fmt))
                break
            except ValueError:
                continue
        if target is None:
            log("⚠️ 开抢时间格式无法解析，立即开始")
        else:
            while True:
                remain = target - (time.time() + offset)
                if remain <= 0:
                    break
                print(f"\r距开抢 {remain:.1f} 秒   ", end="", flush=True)
                time.sleep(min(0.1, remain))
    log("⏰ 开抢！")

    idx = 0
    while True:
        kw, cid, secret = courses[idx]
        try:
            text = api.post("/elective/clazz/add", form={
                "clazzType": clazz_type, "clazzId": cid, "secretVal": secret,
            })
            typ, msg = judge(text)
        except Exception as e:
            typ, msg = "fail", f"网络异常: {e}"

        if typ == "success":
            log(f"✅ 选上「{kw}」！({msg})")
            break
        elif typ in ("full", "impossible"):
            old = idx
            idx = idx + 1 if idx < len(courses) - 1 else 0
            nxt = courses[idx][0]
            log(f"⛔ 「{kw}」{msg} → {'切备选「' + nxt + '」' if idx != old else '已是最后志愿，从头轮询'}")
        elif typ == "notOpen":
            log(f"⏳ {msg}，继续等待…")
        elif typ == "auth":
            log(f"🔒 {msg} —— 登录态失效，请重新导出会话，程序退出")
            sys.exit(2)
        else:
            log(f"❌ 「{kw}」{typ}: {msg}")
        time.sleep(interval)


if __name__ == "__main__":
    main()
