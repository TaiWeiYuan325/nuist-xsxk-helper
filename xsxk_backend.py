# -*- coding: utf-8 -*-
"""南信大选课助手 - 后端服务（无 UI）
复用 xsxk_app.py 的全部网络/解析/抢课逻辑，通过 localhost HTTP JSON API 供 WinUI3 前端调用。
启动: python xsxk_backend.py   （默认端口 18765）
"""
import json
import os
import queue
import re
import sys
import threading
import time
import urllib.parse
from collections import deque
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

from xsxk_app import (Api, judge, parse_json_text, walk_rows, row_name, row_time, pick,
                      F_TEACHER, F_CAP, F_CHOSEN, parse_class_types, parse_class_types_loose,
                      fmt_time, to_epoch_ms, parse_start_time, pick_free_grade)

PORT = 18765
BASE_DEFAULT = "https://xsxk.nuist.edu.cn/xsxk"


class Core:
    """UI 无关的选课核心：状态 + 浏览器代理 + 抢课循环。等价于 xsxk_app.App 去掉 tkinter。"""

    def __init__(self):
        self.base = BASE_DEFAULT
        self.auth = ""
        self.batch = ""
        self.ctype = "TYKC"
        self.campus = "01"
        self.interval_ms = 400
        self.start_at = ""
        self.volmode = False
        self.debug = False

        self.rows = {}            # JXBID -> row dict
        self.volunteers = []      # [{cid,secret,type,label}]
        self.batches = []         # electiveBatchList
        self.manual_batch = None
        self.types = []           # [(code, name)]
        self.offset = 0.0
        self.net_info = {"ok": None, "online": None, "rtt": None, "offset": 0}
        self.countdown = ""
        self.token_status = ""
        self.grabbing = False
        self.last_result = ""

        self.stop_flag = threading.Event()
        self.api_q = queue.Queue()
        self.cap_q = queue.Queue()
        self.pw_page = None
        self.pw_ctx = None
        self.pw_ua = ""
        self._nav_batch = None
        self._pw_alive = threading.Event()
        self._browser_open = False
        self._last_refresh = 0.0

        self._log_seq = 0
        self._logs = deque(maxlen=3000)
        self._lock = threading.Lock()

        # WebSocket（选课结果推送：/add 只是进队列，真正结果靠 WS 通知）
        self._ws = None
        self._ws_running = False
        self._ws_connected = False
        self._ws_success = None     # {"cid":..., "name":...} 选课成功推送
        self._ws_full = {}          # cid -> msg 课容量已满推送
        self._ws_lock = threading.Lock()

        threading.Thread(target=self._cap_drain_loop, daemon=True).start()
        threading.Thread(target=self._net_loop, daemon=True).start()

    # ---------------- 日志 ----------------
    def log(self, msg):
        with self._lock:
            self._log_seq += 1
            self._logs.append({"n": self._log_seq,
                               "time": time.strftime("%H:%M:%S"),
                               "text": str(msg)})

    def logs_after(self, n):
        with self._lock:
            return [x for x in self._logs if x["n"] > n], self._log_seq

    # ---------------- 状态快照 ----------------
    def snapshot(self):
        labels = []
        for b in self.batches:
            label = f"{b.get('name') or '?'}（{fmt_time(b.get('beginTime'))} 开始）"
            if b.get("canSelect") != "1":
                label += " ⚠️不可选"
            labels.append(label)
        sel = 0
        if self.manual_batch:
            for i, b in enumerate(self.batches):
                if b.get("code") == self.manual_batch.get("code"):
                    sel = i + 1
                    break
        courses = []
        for cid, row in self.rows.items():
            courses.append({
                "cid": cid,
                "name": row_name(row) or cid,
                "teacher": pick(row, F_TEACHER),
                "time": (row_time(row) or "")[:30],
                "cap": pick(row, F_CAP),
                "chosen": pick(row, F_CHOSEN),
                "raw": json.dumps(row, ensure_ascii=False),
            })
        return {
            "base": self.base, "auth": bool(self.auth), "batch": self.batch,
            "ctype": self.ctype, "campus": self.campus,
            "interval_ms": self.interval_ms, "start_at": self.start_at,
            "volmode": self.volmode, "debug": self.debug,
            "browser_open": self._browser_open,
            "batch_labels": labels, "batch_sel": sel,
            "types": [{"code": c, "name": n} for c, n in self.types],
            "courses": courses,
            "volunteers": [{"cid": v["cid"], "label": v["label"], "type": v["type"]}
                           for v in self.volunteers],
            "net": self.net_info, "countdown": self.countdown,
            "token_status": self.token_status,
            "grabbing": self.grabbing, "last_result": self.last_result,
            "ws": self._ws_connected,
        }

    # ---------------- API 统一入口 ----------------
    def _mk_api(self):
        h = {}
        if self.auth:
            h["Authorization"] = self.auth
        if self.batch:
            h["batchid"] = self.batch
        self.api = Api(self.base, h)
        return self.api

    def api_call(self, method, path, timeout=15, **kw):
        kw.setdefault("timeout", timeout)
        if self._pw_alive.is_set() and self.pw_ctx is not None:
            res_q = queue.Queue()
            self.api_q.put((method, path, kw, res_q))
            try:
                ok, val = res_q.get(timeout=timeout + 5)
            except queue.Empty:
                raise ValueError("浏览器代理请求超时")
            if ok:
                return val
            raise ValueError(val)
        if method == "COOKIES":
            raise ValueError("内置浏览器未打开，无法获取会话 Cookie")
        api = self._mk_api()
        if method in ("GET", "NAV"):
            return api.get(path, params=kw.get("params"), timeout=kw.get("timeout", 10))
        return api.post(path, form=kw.get("form"), json_body=kw.get("json_body"),
                        timeout=kw.get("timeout", 10))

    def _pw_fetch(self, method, path, kw):
        try:
            return self._pw_page_fetch(method, path, kw)
        except Exception as e:
            self.log(f"⚠️ 页面内请求失败，改用浏览器内核直连: {str(e)[:80]}")
            return self._pw_ctx_fetch(method, path, kw)

    def _pw_page_fetch(self, method, path, kw):
        if self.pw_page is None:
            raise ValueError("无可用页面")
        url = self.base.rstrip("/") + path
        headers, body = {}, None
        if method == "GET":
            if kw.get("params"):
                url += "?" + urllib.parse.urlencode(kw["params"])
        elif "json_body" in kw:
            headers["Content-Type"] = "application/json"
            body = json.dumps(kw["json_body"], ensure_ascii=False)
        else:
            headers["Content-Type"] = "application/x-www-form-urlencoded"
            body = urllib.parse.urlencode(kw.get("form") or {})
        if self.auth:
            headers["Authorization"] = self.auth
        if self.batch:
            headers["batchid"] = self.batch
        return self.pw_page.evaluate(
            "async (a) => { const r = await fetch(a.url, {method: a.method, headers: a.headers, "
            "body: a.body, credentials: 'include'}); return r.text(); }",
            {"url": url, "method": method, "headers": headers, "body": body})

    def _pw_ctx_fetch(self, method, path, kw):
        url = self.base.rstrip("/") + path
        headers = {"User-Agent": self.pw_ua or
                   "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
                   "(KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36"}
        if self.auth:
            headers["Authorization"] = self.auth
        if self.batch:
            headers["batchid"] = self.batch
        timeout_ms = kw.get("timeout", 10) * 1000
        req = self.pw_ctx.request
        if method == "GET":
            resp = req.get(url, params=kw.get("params"), headers=headers, timeout=timeout_ms)
        elif "json_body" in kw:
            headers["Content-Type"] = "application/json"
            resp = req.post(url, data=json.dumps(kw["json_body"], ensure_ascii=False),
                            headers=headers, timeout=timeout_ms)
        else:
            resp = req.post(url, form=kw.get("form") or {}, headers=headers, timeout=timeout_ms)
        return resp.text()

    def _pw_nav(self, kw):
        """顶层页面导航到 grablessons——服务器只认真实页面导航来确立会话当前轮次"""
        if self.pw_page is None:
            raise ValueError("无可用页面")
        url = self.base.rstrip("/") + "/elective/grablessons"
        if kw.get("params"):
            url += "?" + urllib.parse.urlencode(kw["params"])
        self.pw_page.goto(url, wait_until="domcontentloaded",
                          timeout=kw.get("timeout", 30) * 1000)
        return self.pw_page.content()

    # ---------------- 内置浏览器 ----------------
    def open_browser(self):
        if self._browser_open:
            self.log("内置浏览器已在运行")
            return
        threading.Thread(target=self._browser_thread, daemon=True).start()

    def _browser_thread(self):
        from playwright.sync_api import sync_playwright
        self._browser_open = True
        base_dir = os.path.dirname(os.path.abspath(
            sys.executable if getattr(sys, "frozen", False) else __file__))
        profile = os.path.join(base_dir, ".browser-profile")
        url = self.base.rstrip("/") + "/profile/index.html"
        try:
            with sync_playwright() as p:
                ctx = p.chromium.launch_persistent_context(
                    profile, headless=False, viewport={"width": 1024, "height": 768},
                    args=["--window-size=1024,768"])

                def hook(page):
                    page.on("request", self._on_pw_request)
                    page.on("response", self._on_pw_response)

                for pg in ctx.pages:
                    hook(pg)
                ctx.on("page", hook)
                if not ctx.pages:
                    ctx.new_page().goto(url)
                elif ctx.pages[0].url in ("about:blank", ""):
                    ctx.pages[0].goto(url)
                self.log("内置 Chromium 已打开，请登录选课系统（抢课期间请勿关闭此浏览器窗口）")
                self.pw_page = ctx.pages[0] if ctx.pages else ctx.new_page()
                self.pw_ctx = ctx
                try:
                    self.pw_ua = self.pw_page.evaluate("navigator.userAgent") or ""
                except Exception:
                    self.pw_ua = ""
                self._pw_alive.set()
                while True:
                    try:
                        if not ctx.pages:
                            break
                    except Exception:
                        break
                    try:
                        method, path, kw, res_q = self.api_q.get(timeout=0.3)
                    except queue.Empty:
                        continue
                    try:
                        if method == "NAV":
                            res_q.put((True, self._pw_nav(kw)))
                        elif method == "COOKIES":
                            ck = "; ".join(f"{c['name']}={c['value']}" for c in ctx.cookies()
                                           if "nuist.edu.cn" in c.get("domain", ""))
                            res_q.put((True, ck))
                        else:
                            res_q.put((True, self._pw_fetch(method, path, kw)))
                    except Exception as e:
                        res_q.put((False, f"浏览器内请求失败: {e}"))
        except Exception as e:
            self.log(f"内置浏览器异常: {e}")
        finally:
            self._pw_alive.clear()
            self.pw_page = None
            self.pw_ctx = None
            self._browser_open = False
            self.log("内置浏览器已关闭")

    def _on_pw_request(self, req):
        try:
            u = req.url
            if "/elective/" not in u and "/volunteer/" not in u and "/auth/" not in u:
                return
            if self.debug:
                self.cap_q.put({"type": "debug",
                                "msg": f"REQ {req.method} {u.split('/xsxk')[-1]} | body: {(req.post_data or '')[:200]}"})
            h = req.headers or {}
            data = {k: h[k] for k in ("authorization", "batchid") if h.get(k)}
            if data:
                self.cap_q.put({"type": "headers", "headers": data})
        except Exception:
            pass

    def _on_pw_response(self, resp):
        try:
            u = resp.url
            if "/elective/" not in u and "/volunteer/" not in u and "/auth/" not in u:
                return
            if self.debug:
                try:
                    body = resp.text()[:300].replace("\n", " ")
                except Exception:
                    body = "(无响应体)"
                self.cap_q.put({"type": "debug", "msg": f"RESP {resp.status} {u.split('/xsxk')[-1]} | {body}"})
            if "/elective/clazz/list" not in u:
                return
            rows = {}
            walk_rows(resp.json(), rows)
            ctype = ""
            try:
                ctype = json.loads(resp.request.post_data or "{}").get("teachingClassType", "") or ""
            except Exception:
                pass
            if rows:
                self.cap_q.put({"type": "rows", "rows": list(rows.values()), "clazzType": ctype})
        except Exception:
            pass

    def _cap_drain_loop(self):
        while True:
            try:
                data = self.cap_q.get(timeout=0.5)
            except queue.Empty:
                continue
            try:
                self._on_browser_capture(data)
            except Exception:
                pass

    def _on_browser_capture(self, data):
        if data.get("type") == "debug":
            self.log("🌐 " + data.get("msg", ""))
            return
        if data.get("type") == "headers":
            hd = data.get("headers") or {}
            changed = []
            for k, v in hd.items():
                if k.lower() == "authorization" and v != self.auth:
                    self.auth = v
                    changed.append("Authorization")
                if k.lower() == "batchid" and v != self.batch:
                    self.batch = v
                    self._nav_batch = v
                    changed.append("batchid")
            if changed:
                self.log(f"🔑 已自动捕获: {'、'.join(changed)}")
                self.token_status = "✅ 已自动捕获登录态"
                self.refresh_batch(silent=True)
        elif data.get("type") == "rows":
            rows = data.get("rows") or []
            for r in rows:
                if r.get("JXBID"):
                    self.rows[r["JXBID"]] = r
            if data.get("clazzType"):
                self.ctype = data["clazzType"]
            self.log(f"📚 浏览器中捕获课程 {len(rows)} 门（课程库累计 {len(self.rows)} 门）")

    # ---------------- 轮次 / 类别 ----------------
    def refresh_batch(self, silent=True):
        threading.Thread(target=self._refresh_batch, args=(silent,), daemon=True).start()

    def _refresh_batch(self, silent=True):
        if not self.auth:
            return
        now = time.time()
        if silent and now - self._last_refresh < 2:
            return
        self._last_refresh = now
        if not self.batch and self._pw_alive.is_set():
            try:
                self.api_call("NAV", "/elective/grablessons", timeout=30)
                self.log("已打开默认选课页，等待捕获当前轮次…")
            except Exception as e:
                self.log(f"⚠️ 打开默认选课页失败: {e}")
            return
        batches = []
        try:
            form = {}
            if self.batch:
                form["batchId"] = self.batch
            j = parse_json_text(self.api_call("POST", "/elective/user", form=form))
            batches = ((j.get("data") or {}).get("student") or {}).get("electiveBatchList") or []
            if batches:
                self.batches = batches
                self.log(f"发现 {len(batches)} 个选课轮次")
            elif not silent:
                self.log(f"轮次列表为空: {str(j.get('msg'))[:80]}")
        except Exception as e:
            self.log(f"⚠️ 获取轮次列表失败: {e}")
        bid = None
        if self.manual_batch is None and batches:
            now_ms = now * 1000
            cur = None
            for b in batches:
                begin, end = to_epoch_ms(b.get("beginTime")), to_epoch_ms(b.get("endTime"))
                if begin and end and begin <= now_ms <= end:
                    cur = b
                    break
            if cur is None:
                cand = [b for b in batches if b.get("canSelect") == "1"]
                cur = cand[0] if cand else None
            bid = (cur or {}).get("code")
        if bid and self.manual_batch is None:
            if bid != self.batch:
                old = self.batch
                self.batch = bid
                self.log(f"🔄 批次已自动更新: …{bid[-8:]}（原 {('…' + old[-8:]) if old else '空'}）")
            elif not silent:
                self.log(f"当前批次无变化: …{bid[-8:]}")
        target = (self.manual_batch or {}).get("code") or bid or self.batch
        if target:
            self._load_types(target)

    def _load_types(self, batch_code):
        try:
            if batch_code and batch_code != self._nav_batch and self._pw_alive.is_set():
                html2 = self.api_call("NAV", "/elective/grablessons",
                                      params={"batchId": batch_code}, timeout=30)
                self._nav_batch = batch_code
                self.log(f"🔀 服务器轮次已切换（页面导航）: …{batch_code[-8:]}")
            else:
                html2 = self.api_call("GET", "/elective/grablessons", params={"batchId": batch_code})
            types = parse_class_types(html2) or parse_class_types_loose(html2)
            if types:
                self.types = types
                codes = [c for c, _ in types]
                if self.ctype not in codes:
                    self.ctype = types[0][0]
                self.log("本批次课程类别: " + " / ".join(f"{n}({c})" for c, n in types))
            else:
                snippet = re.sub(r"\s+", " ", html2 or "")[:200]
                self.log(f"⚠️ 类别解析失败（页面 {len(html2 or '')} 字符）: {snippet}")
        except Exception as e:
            self.log(f"⚠️ 获取课程类别失败: {e}")

    def select_batch(self, idx):
        """idx: 0=自动；>=1 对应 batches[idx-1]"""
        if idx <= 0:
            self.manual_batch = None
            self.log("轮次模式: 自动（跟随当前轮次）")
            self.refresh_batch(silent=True)
            return
        if idx > len(self.batches):
            return
        b = self.batches[idx - 1]
        self.manual_batch = b
        self.batch = b.get("code", "")
        self.log(f"🎯 已选择轮次: {b.get('name')}｜{fmt_time(b.get('beginTime'))} ~ {fmt_time(b.get('endTime'))}")
        if b.get("canSelect") != "1":
            self.log(f"⚠️ 该轮次当前不可选: {b.get('noSelectReason') or '未到开始时间'}")
        if not self.start_at and b.get("beginTime"):
            self.start_at = fmt_time(b.get("beginTime"))
            self.log("已自动填入开抢时间 = 轮次开始时间")
        threading.Thread(target=self._switch_and_fetch, args=(self.batch,), daemon=True).start()

    def _switch_and_fetch(self, code):
        self._load_types(code)
        self._fetch_courses()

    # ---------------- 课程列表 ----------------
    def fetch_courses(self):
        threading.Thread(target=self._fetch_courses, daemon=True).start()

    def _fetch_courses(self):
        ctype = (self.ctype or "TYKC").split(" ")[0]
        self.log(f"拉取课程列表（{ctype}）…")
        try:
            payload = {
                "teachingClassType": ctype,
                "pageNumber": 1,
                "pageSize": 200,
                "orderBy": "",
                "campus": self.campus or "01",
            }
            if ctype != "ALLKC":
                payload["SFYX"] = "2"
            j = parse_json_text(self.api_call("POST", "/elective/nuist/clazz/list", json_body=payload))
        except Exception as e:
            self.log(f"❌ 获取课程列表失败: {e}")
            return
        self.rows = {}
        walk_rows(j, self.rows)
        self.log(f"获取到 {len(self.rows)} 门课程")

    # ---------------- 志愿队列 ----------------
    def add_volunteer(self, cid):
        row = self.rows.get(cid)
        if not row:
            return
        if any(v["cid"] == cid for v in self.volunteers):
            return
        name = row_name(row) or cid
        self.volunteers.append({"cid": cid, "secret": row.get("secretVal", ""),
                                "type": (self.ctype or "TYKC").split(" ")[0],
                                "label": f"{name} {pick(row, F_TEACHER)}"})
        self.log(f"➕ 已加入志愿: {name}")

    def del_volunteer(self, idx):
        if 0 <= idx < len(self.volunteers):
            v = self.volunteers.pop(idx)
            self.log(f"➖ 已移除志愿: {v['label']}")

    def move_volunteer(self, idx, d):
        j = idx + d
        if 0 <= idx < len(self.volunteers) and 0 <= j < len(self.volunteers):
            self.volunteers[idx], self.volunteers[j] = self.volunteers[j], self.volunteers[idx]

    # ---------------- Token ----------------
    def check_token(self):
        threading.Thread(target=self._check_token, daemon=True).start()

    def _check_token(self):
        try:
            form = {}
            if self.batch:
                form["batchId"] = self.batch
            j = parse_json_text(self.api_call("POST", "/elective/user", form=form))
            if j.get("code") == 200:
                stu = (j.get("data") or {}).get("student") or {}
                name = stu.get("XM") or stu.get("XH") or "未知"
                self.token_status = f"✅ Token 有效（{name}）"
                self.log(f"Token 验证通过: {name}")
            else:
                self.token_status = f"❌ {j.get('msg')}"
                self.log(f"Token 验证失败: {j.get('msg')}")
        except Exception as e:
            self.token_status = "❌ 请求失败"
            self.log(f"Token 验证异常: {e}")

    # ---------------- 校时 ----------------
    def _net_loop(self):
        while True:
            try:
                t0 = time.time()
                j = parse_json_text(self.api_call("POST", "/web/now", form={}, timeout=5))
                rtt = (time.time() - t0) * 1000
                if j.get("code") == 200 and (j.get("data") or {}).get("currentTime"):
                    self.offset = (j["data"]["currentTime"] - (t0 * 1000 + rtt / 2)) / 1000.0
                    self.net_info = {"ok": True, "online": j["data"].get("onlineCount"),
                                     "rtt": round(rtt), "offset": round(self.offset * 1000)}
            except Exception:
                self.net_info = {"ok": False, "online": None, "rtt": None, "offset": 0}
            time.sleep(10)

    # ---------------- 抢课 ----------------
    def start_grab(self):
        if not self.volunteers:
            self.log("⚠️ 志愿队列为空，请先从课程列表加入志愿")
            return
        self.stop_flag.clear()
        self.grabbing = True
        threading.Thread(target=self._grab_loop, daemon=True).start()

    def stop_grab(self):
        self.stop_flag.set()
        self.grabbing = False
        self._ws_stop()
        self.log("已手动停止")

    def _grab_loop(self):
        self._refresh_batch(silent=True)
        interval = (self.interval_ms or 400) / 1000.0
        start_at = self.start_at.strip()

        if start_at:
            try:
                target = parse_start_time(start_at)
                while not self.stop_flag.is_set():
                    remain = target - (time.time() + self.offset)
                    if remain <= 0:
                        break
                    self.countdown = f"{remain:.1f}"
                    time.sleep(0.1)
            except ValueError:
                self.log("⚠️ 开抢时间格式错误，立即开始")
        self.countdown = ""
        if self.stop_flag.is_set():
            self.grabbing = False
            return
        self.log("⏰ 开抢！")

        # /add 只是进队列，真正结果由 WebSocket 推送——抢课期间全程监听
        self._ws_ensure()

        vols = list(self.volunteers)
        types_in_queue = {v["type"] for v in vols}
        if self.volmode and types_in_queue != {"XGKC"}:
            self.log("⚠️ 提醒：志愿模式通常只用于通识选修课(XGKC)，当前队列含其他类别")
        if not self.volmode and "XGKC" in types_in_queue:
            self.log("⚠️ 提醒：队列含通识选修课(XGKC)，通识课必须带志愿级别提交，建议勾选「志愿模式」")

        # ---------- 志愿模式 ----------
        if self.volmode:
            batch = self.batch
            ok_count = 0
            for v in vols:
                if self.stop_flag.is_set():
                    break
                grade = None
                try:
                    grade = pick_free_grade(self.api_call("POST", "/volunteer/list/choose", form={
                        "clazzType": v["type"], "clazzId": v["cid"],
                    }))
                except Exception as e:
                    self.log(f"⚠️ 「{v['label']}」查询志愿级别失败: {e}")
                if grade is None:
                    self.log(f"⛔ 「{v['label']}」无可用志愿级别（8个志愿可能已报满），跳过")
                    continue
                typ, msg = "fail", ""
                for attempt in range(1, 4):
                    if self.stop_flag.is_set():
                        break
                    try:
                        text = self.api_call("POST", "/elective/clazz/add", form={
                            "clazzType": v["type"], "clazzId": v["cid"], "secretVal": v["secret"],
                            "batchId": batch, "needBook": "", "chooseVolunteer": str(grade),
                        })
                        typ, msg = judge(text)
                    except Exception as e:
                        typ, msg = "fail", f"网络异常: {e}"
                    if typ == "success":
                        ok_count += 1
                        self.log(f"✅ 「{v['label']}」已报为第{grade}志愿（{msg}）")
                        break
                    if typ in ("auth", "full", "impossible"):
                        break
                    if attempt < 3:
                        self.log(f"🔁 「{v['label']}」第{attempt}次提交未成功（{msg[:60]}），稍后重试")
                        time.sleep(max(interval, 0.5))
                if typ == "auth":
                    self.log(f"🔒 {msg} —— 登录态失效，停止")
                    break
                if typ != "success" and attempt >= 3 and typ in ("notOpen", "fail", "badHtml"):
                    self.log(f"❌ 「{v['label']}」3 次尝试均失败: {msg}")
                elif typ in ("full", "impossible"):
                    self.log(f"⛔ 「{v['label']}」{msg}")
                time.sleep(interval)
            self.last_result = f"志愿提交结束：成功 {ok_count}/{len(vols)}"
            self.log(self.last_result)
            self._ws_stop()
            self.grabbing = False
            return

        # ---------- 抢课模式 ----------
        idx = 0
        while not self.stop_flag.is_set():
            v = vols[idx]

            # WS 已推送选课成功 → 直接收工
            with self._ws_lock:
                ws_hit = self._ws_success
                ws_full_msg = self._ws_full.get(v["cid"]) or self._ws_full.get("")
            if ws_hit:
                self.last_result = f"选上：{ws_hit['name']}"
                self.log(f"✅ 选上「{ws_hit['name']}」！（WebSocket 服务器确认）")
                break
            # WS 已推送当前课满员 → 不重投，直接切备选
            if ws_full_msg:
                old = idx
                idx = idx + 1 if idx < len(vols) - 1 else 0
                self.log(f"⛔ 「{v['label']}」{ws_full_msg}（WS 推送）→ "
                         + (f"切备选「{vols[idx]['label']}」" if idx != old else "已是最后志愿，从头轮询"))
                with self._ws_lock:
                    self._ws_full.pop(v["cid"], None)
                    self._ws_full.pop("", None)
                time.sleep(interval)
                continue

            try:
                text = self.api_call("POST", "/elective/clazz/add", form={
                    "clazzType": v["type"], "clazzId": v["cid"], "secretVal": v["secret"],
                })
                typ, msg = judge(text)
            except Exception as e:
                typ, msg = "fail", f"网络异常: {e}"

            if typ == "success":
                if self._ws_connected:
                    # 高峰期 200 只代表「已进队列」，等 WS 最终裁决（最多 8 秒）
                    self.log(f"📨 「{v['label']}」已进选课队列，等待服务器确认…")
                    deadline = time.time() + 8
                    while time.time() < deadline and not self.stop_flag.is_set():
                        with self._ws_lock:
                            ws_hit = self._ws_success
                            ws_full_msg = self._ws_full.get(v["cid"]) or self._ws_full.get("")
                        if ws_hit:
                            break
                        if ws_full_msg:
                            break
                        time.sleep(0.2)
                    if ws_hit:
                        self.last_result = f"选上：{ws_hit['name']}"
                        self.log(f"✅ 选上「{ws_hit['name']}」！（WebSocket 服务器确认）")
                        break
                    if ws_full_msg:
                        old = idx
                        idx = idx + 1 if idx < len(vols) - 1 else 0
                        self.log(f"⛔ 「{v['label']}」{ws_full_msg}（WS 推送）→ "
                                 + (f"切备选「{vols[idx]['label']}」" if idx != old else "已是最后志愿，从头轮询"))
                        with self._ws_lock:
                            self._ws_full.pop(v["cid"], None)
                            self._ws_full.pop("", None)
                    else:
                        self.log(f"⏳ 「{v['label']}」8 秒内未收到裁决，继续投递 /add")
                else:
                    self.last_result = f"选上：{v['label']}"
                    self.log(f"✅ 选上「{v['label']}」！({msg})")
                    break
            elif typ in ("full", "impossible"):
                old = idx
                idx = idx + 1 if idx < len(vols) - 1 else 0
                self.log(f"⛔ 「{v['label']}」{msg} → "
                         + (f"切备选「{vols[idx]['label']}」" if idx != old else "已是最后志愿，从头轮询"))
            elif typ == "notOpen":
                self.log(f"⏳ {msg}")
            elif typ == "auth":
                self.log(f"🔒 {msg} —— 登录态失效，已停止")
                break
            else:
                self.log(f"❌ 「{v['label']}」{typ}: {msg}")
            time.sleep(interval)
        self._ws_stop()
        self.grabbing = False

    # ---------------- WebSocket 选课结果推送 ----------------
    def _student_id(self):
        """从 JWT Authorization 的 payload 里解出学号（login_user_key）"""
        try:
            payload = self.auth.split(".")[1]
            payload += "=" * (-len(payload) % 4)
            import base64
            return str(json.loads(base64.urlsafe_b64decode(payload)).get("login_user_key") or "")
        except Exception:
            return ""

    def _ws_ensure(self):
        """抢课时启动 WS 监听。返回是否已连接。"""
        if self._ws_running:
            return self._ws_connected
        sid = self._student_id()
        if not sid:
            self.log("⚠️ 无法从登录态解析学号，WS 监听未启动（将退回 HTTP 判定模式）")
            return False
        try:
            cookie = self.api_call("COOKIES", "")
        except Exception as e:
            self.log(f"⚠️ 获取浏览器 Cookie 失败，WS 监听未启动: {e}")
            return False
        ws_url = self.base.replace("https://", "wss://").replace("http://", "ws://").rstrip("/") \
            + f"/websocket/{sid}"
        self._ws_running = True
        with self._ws_lock:
            self._ws_success = None
            self._ws_full = {}
        threading.Thread(target=self._ws_connect_loop, args=(ws_url, cookie), daemon=True).start()
        threading.Thread(target=self._ws_heartbeat_loop, daemon=True).start()
        self.log(f"📡 WebSocket 监听已启动（学号 {sid}），选课结果将由服务器实时推送")
        return True

    def _ws_stop(self):
        self._ws_running = False
        self._ws_connected = False
        try:
            if self._ws:
                self._ws.close()
        except Exception:
            pass

    def _ws_connect_loop(self, ws_url, cookie):
        import websocket
        while self._ws_running:
            try:
                self._ws = websocket.WebSocketApp(
                    ws_url,
                    on_message=self._ws_on_message,
                    on_open=lambda ws: self._ws_mark(True),
                    on_close=lambda ws, c, m: self._ws_mark(False),
                    on_error=lambda ws, e: self.log(f"📡 WS 异常: {str(e)[:80]}"),
                    cookie=cookie or None)
                self._ws.run_forever(ping_interval=0)
            except Exception as e:
                if self._ws_running:
                    self.log(f"📡 WS 连接失败: {str(e)[:80]}")
            self._ws_connected = False
            if self._ws_running:
                time.sleep(2)

    def _ws_mark(self, ok):
        self._ws_connected = ok
        if ok:
            self.log("📡 WS 已连接，等待服务器推送…")

    def _ws_heartbeat_loop(self):
        while self._ws_running:
            try:
                ws = self._ws
                if ws and ws.sock and ws.sock.connected:
                    ws.send("hi")
                time.sleep(5)
            except Exception:
                time.sleep(2)

    def _ws_on_message(self, ws, message):
        try:
            data = json.loads(message)
        except Exception:
            return
        code = data.get("code")
        msg = str(data.get("msg") or "")
        result = data.get("data")
        if code == 200 and result == "heart":
            return
        if code == 200 and "选课成功" in msg:
            cid, name = "", ""
            if isinstance(result, dict):
                cid = str(result.get("clazzId") or result.get("teachingClassID")
                           or result.get("JXBID") or "")
                for x in result.get("xkjgList") or []:
                    if str(x.get("clazzId") or x.get("JXBID") or "") == cid:
                        name = str(x.get("courseName") or x.get("KCM") or "")
                        break
            if not name:
                name = msg.split(":", 1)[1] if ":" in msg else msg
            queue_cids = {v["cid"] for v in self.volunteers}
            if not cid or cid in queue_cids:
                with self._ws_lock:
                    self._ws_success = {"cid": cid, "name": name}
                self.log(f"🎉 服务器推送：选课成功「{name}」！")
            else:
                self.log(f"📡 WS 推送了非队列课程的选课成功: {name}（{cid}）")
        elif code == 500:
            cid = ""
            if isinstance(result, dict):
                cid = str(result.get("clazzId") or result.get("teachingClassID")
                           or result.get("JXBID") or "")
            if any(k in msg for k in ("课容量已满", "人数已满", "容量已满", "名额已满", "课已满", "课容量已达上限", "满")):
                with self._ws_lock:
                    self._ws_full[cid] = msg
                self.log(f"📡 服务器推送：{msg}（课程ID {cid or '未知'}）")
            elif self.debug:
                self.log(f"📡 WS 消息: code={code} {msg[:80]}")

    # ---------------- 设置 ----------------
    def apply_settings(self, d):
        for key, attr in (("base", "base"), ("auth", "auth"), ("batch", "batch"),
                          ("ctype", "ctype"), ("campus", "campus"), ("start_at", "start_at")):
            if key in d and d[key] is not None:
                setattr(self, attr, str(d[key]).strip())
        if "interval_ms" in d and d["interval_ms"]:
            try:
                self.interval_ms = max(50, int(d["interval_ms"]))
            except (TypeError, ValueError):
                pass
        if "volmode" in d:
            self.volmode = bool(d["volmode"])
        if "debug" in d:
            self.debug = bool(d["debug"])


CORE = Core()


# ---------------- HTTP 层 ----------------
class Handler(BaseHTTPRequestHandler):
    def log_message(self, *a):
        pass

    def _send(self, obj, code=200):
        body = json.dumps(obj, ensure_ascii=False).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Access-Control-Allow-Origin", "*")
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        u = urllib.parse.urlparse(self.path)
        if u.path == "/api/state":
            self._send(CORE.snapshot())
        elif u.path == "/api/logs":
            q = urllib.parse.parse_qs(u.query)
            after = int(q.get("after", ["0"])[0] or 0)
            logs, nxt = CORE.logs_after(after)
            self._send({"logs": logs, "next": nxt})
        else:
            self._send({"error": "not found"}, 404)

    def do_POST(self):
        if self.path != "/api/action":
            self._send({"error": "not found"}, 404)
            return
        try:
            n = int(self.headers.get("Content-Length") or 0)
            data = json.loads(self.rfile.read(n) or b"{}")
        except Exception as e:
            self._send({"ok": False, "error": str(e)}, 400)
            return
        act = data.get("action")
        try:
            if act == "open_browser":
                CORE.open_browser()
            elif act == "refresh":
                CORE.refresh_batch(silent=False)
            elif act == "select_batch":
                CORE.select_batch(int(data.get("index", 0)))
            elif act == "fetch_courses":
                CORE.fetch_courses()
            elif act == "add_volunteer":
                CORE.add_volunteer(data.get("cid", ""))
            elif act == "del_volunteer":
                CORE.del_volunteer(int(data.get("index", -1)))
            elif act == "move_volunteer":
                CORE.move_volunteer(int(data.get("index", -1)), int(data.get("dir", 0)))
            elif act == "clear_volunteers":
                CORE.volunteers.clear()
            elif act == "start_grab":
                CORE.start_grab()
            elif act == "stop_grab":
                CORE.stop_grab()
            elif act == "check_token":
                CORE.check_token()
            elif act == "set":
                CORE.apply_settings(data.get("settings") or {})
            else:
                self._send({"ok": False, "error": f"unknown action {act}"}, 400)
                return
        except Exception as e:
            self._send({"ok": False, "error": str(e)}, 500)
            return
        self._send({"ok": True})


def main():
    # 打包便携版：若 exe 旁存在 pw-browsers 目录，优先使用其中的 Chromium
    here = os.path.dirname(os.path.abspath(sys.executable if getattr(sys, "frozen", False) else __file__))
    portable_browsers = os.path.join(here, "pw-browsers")
    if os.path.isdir(portable_browsers):
        os.environ["PLAYWRIGHT_BROWSERS_PATH"] = portable_browsers
    srv = ThreadingHTTPServer(("127.0.0.1", PORT), Handler)
    CORE.log(f"后端服务已启动: http://127.0.0.1:{PORT}")
    print(f"xsxk backend listening on http://127.0.0.1:{PORT}")
    srv.serve_forever()


if __name__ == "__main__":
    main()
