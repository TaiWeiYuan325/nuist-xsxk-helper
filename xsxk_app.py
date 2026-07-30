# -*- coding: utf-8 -*-
"""
南信大选课助手（桌面版）
- 自动获取 API 参数与课程列表，软件内配置志愿队列与定时
- 导入油猴脚本导出的 xsxk-session.json 即可获得 token 与课程参数
- 仅使用 Python 标准库（tkinter），无需安装依赖
运行: python xsxk_app.py
"""
import json
import os
import queue
import re
import threading
import time
import tkinter as tk
from tkinter import ttk, filedialog, messagebox
import urllib.request
import urllib.parse

RETRYABLE = ("暂未开始", "未开始", "未开放", "系统繁忙", "稍后", "队列拥堵", "请求频繁", "超时", "结束")
AUTH_MSGS = ("未登录", "登录失效", "认证失败", "token失效", "token过期")
CLASS_TYPES = ["TYKC", "XGKC", "FANKC", "ALLKC", "FAWKC", "TJKC", "CXKC"]

# 列表接口常见字段名（不同类别字段可能不同，做启发式取值）
F_NAME = ("KCM", "KCMC", "courseName", "kcm", "name")
F_TEACHER = ("SKJS", "JSM", "teacherName", "teacher", "js")
F_CAP = ("KRL", "capacity", "krl", "KXRL")
F_CHOSEN = ("YXS", "chosen", "YXRS", "yxrs", "selected")
F_TIME = ("SKDD", "YPSJDD", "time", "SKSJ")


def pick(row, keys):
    for k in keys:
        if row.get(k) not in (None, ""):
            return str(row[k])
    return ""


_TOKENISH = re.compile(r"^[0-9a-zA-Z+/=_-]{20,}$")


def row_name(row):
    """课程名：优先含"板块/课"特征的字段；排除班级列表、token、时间串"""
    n = pick(row, F_NAME)
    candidates = []
    for k, v in row.items():
        if not isinstance(v, str) or k in ("JXBID", "secretVal"):
            continue
        if _TOKENISH.match(v) or re.search(r"\d+-\d+周.*节", v):  # token/时间串不算
            continue
        if v.count("班") >= 2:  # "25人智01班,25信工01班,..." 是自然班级列表
            continue
        candidates.append(v)
    # 优先带课程名特征的候选（含"板块"或"课"），否则取最长
    featured = [v for v in candidates if ("板块" in v or "课" in v)]
    pool = featured or candidates
    longest = max(pool, key=len) if pool else ""
    if n and (not longest or len(longest) <= len(n)):
        return n
    return longest or n


def row_time(row):
    t = pick(row, F_TIME)
    if t:
        return t
    for v in row.values():
        if isinstance(v, str) and re.search(r"\d+-\d+周.*节", v):
            return v
    return ""


def parse_start_time(s):
    """兼容 '2026-07-31 16:00:00' / '2026-7-30 11:00' 等格式"""
    s = (s or "").strip().replace("/", "-").replace("T", " ")
    for fmt in ("%Y-%m-%d %H:%M:%S", "%Y-%m-%d %H:%M"):
        try:
            return time.mktime(time.strptime(s, fmt))
        except ValueError:
            continue
    raise ValueError(f"无法解析时间: {s}")


def judge(text):
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


def pick_free_grade(resp_text):
    """从 /volunteer/list/choose 响应中挑最低的未使用志愿级别；无可用返回 None"""
    try:
        j = json.loads(resp_text)
    except ValueError:
        return None
    if j.get("code") != 200:
        return None
    free = [g["grade"] for g in (j.get("data") or [])
            if isinstance(g, dict) and g.get("isUse") in (None, False) and isinstance(g.get("grade"), int)]
    return min(free) if free else None


def parse_json_text(text):
    """解析 JSON；失败时附带服务器原始响应片段，便于诊断"""
    try:
        return json.loads(text)
    except ValueError:
        snippet = (text[:200].replace("\n", " ")) if text else "(空响应)"
        raise ValueError(f"响应非JSON: {snippet}")


def walk_rows(obj, out):
    if isinstance(obj, dict):
        if obj.get("JXBID") and obj.get("secretVal"):
            out[obj["JXBID"]] = obj
        for v in obj.values():
            walk_rows(v, out)
    elif isinstance(obj, list):
        for v in obj:
            walk_rows(v, out)


class Api:
    def __init__(self, base, headers=None):
        self.base = base.rstrip("/")
        self.headers = dict(headers or {})

    def _browser_headers(self, extra=None):
        """模拟页面 axios 的完整请求头（服务器按此区分接口调用与页面导航）"""
        origin = self.base.rsplit("/xsxk", 1)[0]
        h = {
            "User-Agent": ("Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
                           "AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"),
            "Accept": "application/json, text/plain, */*",
            "Accept-Language": "zh-CN,zh;q=0.9,en;q=0.8",
            "Origin": origin,
            "Referer": self.base + "/elective/grablessons",
        }
        h.update(self.headers)
        if extra:
            h.update(extra)
        return h

    def post(self, path, form=None, json_body=None, timeout=10):
        data = None
        hdrs = self._browser_headers()
        if json_body is not None:
            data = json.dumps(json_body, ensure_ascii=False).encode("utf-8")
            hdrs["Content-Type"] = "application/json"
        elif form is not None:
            data = urllib.parse.urlencode(form).encode("utf-8")
            hdrs["Content-Type"] = "application/x-www-form-urlencoded"
        req = urllib.request.Request(self.base + path, data=data, headers=hdrs, method="POST")
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            return resp.read().decode("utf-8", "ignore")

    def get(self, path, params=None, timeout=10):
        url = self.base + path
        if params:
            url += "?" + urllib.parse.urlencode(params)
        hdrs = self._browser_headers({
            "Accept": "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8"})
        req = urllib.request.Request(url, headers=hdrs, method="GET")
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            return resp.read().decode("utf-8", "ignore")


def parse_batch_id(html):
    """从 profile/index.html 中解析当前批次 code（var batch = {...}）"""
    m = re.search(r"var\s+batch\s*=", html or "")
    if not m:
        return None
    i = html.find("{", m.end())
    if i < 0:
        return None
    try:
        obj, _ = json.JSONDecoder().raw_decode(html[i:])
    except ValueError:
        return None
    code = obj.get("code")
    return code if isinstance(code, str) and code else None


def fmt_time(v):
    """轮次时间兼容 epoch 毫秒和字符串"""
    if v in (None, ""):
        return ""
    if isinstance(v, (int, float)) or (isinstance(v, str) and v.isdigit()):
        return time.strftime("%Y-%m-%d %H:%M:%S", time.localtime(int(v) / 1000))
    return str(v)


def to_epoch_ms(v):
    """epoch 毫秒或 'YYYY-MM-DD HH:MM:SS' 字符串 -> epoch 毫秒；无法解析返回 None"""
    if v in (None, ""):
        return None
    try:
        return int(v)
    except (TypeError, ValueError):
        pass
    for f in ("%Y-%m-%d %H:%M:%S", "%Y-%m-%d %H:%M"):
        try:
            return int(time.mktime(time.strptime(str(v), f)) * 1000)
        except ValueError:
            continue
    return None


def parse_class_types(html):
    """从 grablessons 页面解析 grablessonsVue.lcParam.currentBatch.menuList -> [(code, 显示名)]"""
    marker = "grablessonsVue.lcParam.currentBatch"
    i = (html or "").find(marker)
    if i < 0:
        return []
    j = html.find("{", i)
    try:
        obj, _ = json.JSONDecoder().raw_decode(html[j:])
    except ValueError:
        return []
    out = []
    for item in obj.get("menuList") or []:
        if isinstance(item, dict) and item.get("teachingClassType") and item.get("displayName"):
            out.append((item["teachingClassType"], item["displayName"]))
    return out


def parse_class_types_loose(html):
    """宽松兜底：在页面任意位置找 teachingClassType/displayName 键值对（兼容菜单数据搬家、JS 未加引号的键名）"""
    out, seen = [], set()
    pats = [
        (r'["\']?teachingClassType["\']?\s*:\s*["\']([A-Za-z0-9]+)["\'][^{}]{0,300}?["\']?displayName["\']?\s*:\s*["\']([^"\']+)["\']', (1, 2)),
        (r'["\']?displayName["\']?\s*:\s*["\']([^"\']+)["\'][^{}]{0,300}?["\']?teachingClassType["\']?\s*:\s*["\']([A-Za-z0-9]+)["\']', (2, 1)),
    ]
    for pat, (ci, ni) in pats:
        for m in re.finditer(pat, html or ""):
            code, name = m.group(ci), m.group(ni)
            if code and code not in seen:
                seen.add(code)
                out.append((code, name))
    return out


class App(tk.Tk):
    def __init__(self):
        super().__init__()
        self.title("南信大选课助手 v1.0")
        self.geometry("980x720")
        self.api = Api("https://xsxk.nuist.edu.cn/xsxk")
        self.rows = {}            # JXBID -> row dict
        self.volunteers = []      # [{kw,cid,secret,type}]
        self.offset = 0.0         # 服务器时间偏移（秒）
        self.batches = []         # 全部轮次（electiveBatchList）
        self.manual_batch = None  # 手动选择的轮次 dict；None = 自动跟随当前轮次
        self.stop_flag = threading.Event()
        self.log_q = queue.Queue()
        self.cap_q = queue.Queue()   # 内置浏览器捕获的数据
        self.api_q = queue.Queue()   # 转发给浏览器线程执行的 API 请求
        self.pw_page = None          # 内置浏览器页面（API 代理用）
        self.pw_ctx = None           # 内置浏览器上下文（页面空白时的内核直连兜底）
        self.pw_ua = ""              # 内置浏览器 User-Agent
        self._nav_batch = None       # 浏览器已导航到 grablessons 的轮次 code（服务器会话当前轮次）
        self._pw_alive = threading.Event()
        self._build_ui()
        self.after(100, self._drain_log)
        self.after(1000, self._net_loop)

    # ---------------- UI ----------------
    def _build_ui(self):
        top = ttk.LabelFrame(self, text="连接")
        top.pack(fill="x", padx=8, pady=4)
        ttk.Label(top, text="服务器:").grid(row=0, column=0, sticky="e")
        self.ent_base = ttk.Entry(top, width=38)
        self.ent_base.grid(row=0, column=1, sticky="w")
        self.ent_base.insert(0, "https://xsxk.nuist.edu.cn/xsxk")
        ttk.Button(top, text="导入会话JSON", command=self.import_session).grid(row=0, column=2, padx=6)
        ttk.Button(top, text="验证Token", command=self.check_token).grid(row=0, column=3)
        ttk.Button(top, text="内置浏览器登录", command=self.open_browser).grid(row=0, column=4, padx=6)
        ttk.Button(top, text="刷新轮次/类别", command=lambda: self.refresh_batch(silent=False)).grid(row=0, column=5)
        self.var_debug = tk.BooleanVar(value=False)
        ttk.Checkbutton(top, text="记录浏览器请求", variable=self.var_debug).grid(row=0, column=6, padx=6)
        ttk.Label(top, text="Authorization:").grid(row=1, column=0, sticky="e")
        self.ent_auth = ttk.Entry(top, width=38, show="*")
        self.ent_auth.grid(row=1, column=1, sticky="w")
        ttk.Label(top, text="batchid:").grid(row=1, column=2, sticky="e")
        self.ent_batch = ttk.Entry(top, width=18)
        self.ent_batch.grid(row=1, column=3, sticky="w")
        self.lbl_token = ttk.Label(top, text="未验证", foreground="gray")
        self.lbl_token.grid(row=2, column=1, sticky="w")
        self.lbl_net = ttk.Label(top, text="🌐 尚未校时", foreground="gray")
        self.lbl_net.grid(row=2, column=3, columnspan=2, sticky="w")

        mid = ttk.LabelFrame(self, text="课程列表")
        mid.pack(fill="both", expand=True, padx=8, pady=4)
        bar = ttk.Frame(mid)
        bar.pack(fill="x")
        ttk.Label(bar, text="轮次:").pack(side="left")
        self.cmb_batch = ttk.Combobox(bar, values=["自动（当前轮次）"], width=34, state="readonly")
        self.cmb_batch.pack(side="left")
        self.cmb_batch.current(0)
        self.cmb_batch.bind("<<ComboboxSelected>>", self.on_batch_selected)
        ttk.Label(bar, text="类别:").pack(side="left", padx=(8, 0))
        self.cmb_type = ttk.Combobox(bar, values=CLASS_TYPES, width=8, state="normal")
        self.cmb_type.pack(side="left")
        self.cmb_type.set("TYKC")
        # 手动切换类别后自动刷新课程列表（程序 set 不触发此事件，不会循环）
        self.cmb_type.bind("<<ComboboxSelected>>",
                           lambda _e: threading.Thread(target=self._fetch_courses, daemon=True).start())
        ttk.Label(bar, text="校区:").pack(side="left", padx=(8, 0))
        self.ent_campus = ttk.Entry(bar, width=5)
        self.ent_campus.insert(0, "01")
        self.ent_campus.pack(side="left")
        ttk.Button(bar, text="获取课程列表", command=self.fetch_courses).pack(side="left", padx=8)
        ttk.Label(bar, text="搜索:").pack(side="left")
        self.ent_search = ttk.Entry(bar, width=20)
        self.ent_search.pack(side="left")
        self.ent_search.bind("<KeyRelease>", lambda e: self.fill_table())
        ttk.Button(bar, text="加入志愿 →", command=self.add_volunteer).pack(side="right")

        cols = ("name", "teacher", "time", "cap", "chosen", "jxbid")
        self.tree = ttk.Treeview(mid, columns=cols, show="headings", height=10)
        for c, t, w in zip(cols, ("课程", "教师", "时间地点", "容量", "已选", "JXBID"),
                           (220, 90, 240, 60, 60, 180)):
            self.tree.heading(c, text=t)
            self.tree.column(c, width=w)
        self.tree.pack(fill="both", expand=True)
        self.tree.bind("<Double-1>", lambda e: self.add_volunteer())

        bot = ttk.Frame(self)
        bot.pack(fill="both", expand=True, padx=8, pady=4)

        qf = ttk.LabelFrame(bot, text="志愿队列（按优先级）")
        qf.pack(side="left", fill="both", expand=True)
        self.lst = tk.Listbox(qf, height=6)
        self.lst.pack(fill="both", expand=True)
        qbtn = ttk.Frame(qf)
        qbtn.pack(fill="x")
        ttk.Button(qbtn, text="上移", command=lambda: self.move_vol(-1)).pack(side="left")
        ttk.Button(qbtn, text="下移", command=lambda: self.move_vol(1)).pack(side="left")
        ttk.Button(qbtn, text="删除", command=self.del_vol).pack(side="left")

        sf = ttk.LabelFrame(bot, text="抢课设置")
        sf.pack(side="left", fill="both", expand=True, padx=(8, 0))
        row1 = ttk.Frame(sf); row1.pack(fill="x", pady=2)
        ttk.Label(row1, text="开抢时间:").pack(side="left")
        self.ent_start = ttk.Entry(row1, width=22)
        self.ent_start.pack(side="left")
        ttk.Label(row1, text="(如 2026-07-31 16:00:00，留空手动)", foreground="gray").pack(side="left")
        row2 = ttk.Frame(sf); row2.pack(fill="x", pady=2)
        ttk.Label(row2, text="重试间隔(ms):").pack(side="left")
        self.ent_interval = ttk.Entry(row2, width=8)
        self.ent_interval.insert(0, "400")
        self.ent_interval.pack(side="left")
        ttk.Button(row2, text="保存配置", command=self.save_config).pack(side="left", padx=8)
        ttk.Button(row2, text="加载配置", command=self.load_config).pack(side="left")
        row2b = ttk.Frame(sf); row2b.pack(fill="x", pady=2)
        self.var_volmode = tk.BooleanVar(value=False)
        ttk.Checkbutton(row2b, text="志愿模式（通识课：按队列顺序批量提交全部志愿，选中不停止）",
                        variable=self.var_volmode).pack(side="left")
        row3 = ttk.Frame(sf); row3.pack(fill="x", pady=4)
        self.btn_go = ttk.Button(row3, text="开始抢课", command=self.start_grab)
        self.btn_go.pack(side="left")
        ttk.Button(row3, text="停止", command=self.stop_grab).pack(side="left", padx=6)
        self.lbl_cd = ttk.Label(row3, text="", foreground="blue")
        self.lbl_cd.pack(side="left", padx=8)

        lf = ttk.LabelFrame(self, text="日志")
        lf.pack(fill="both", expand=True, padx=8, pady=4)
        self.txt = tk.Text(lf, height=10, state="disabled")
        self.txt.pack(fill="both", expand=True)

    # ---------------- 基础动作 ----------------
    def log(self, msg):
        self.log_q.put(msg)

    def _drain_log(self):
        try:
            while True:
                m = self.log_q.get_nowait()
                t = time.strftime("%H:%M:%S") + f".{int(time.time() * 1000) % 1000:03d}"
                self.txt.configure(state="normal")
                self.txt.insert("end", f"[{t}] {m}\n")
                self.txt.see("end")
                self.txt.configure(state="disabled")
        except queue.Empty:
            pass
        try:
            while True:
                self._on_browser_capture(self.cap_q.get_nowait())
        except queue.Empty:
            pass
        self.after(100, self._drain_log)

    def _mk_api(self):
        h = {}
        if self.ent_auth.get().strip():
            h["Authorization"] = self.ent_auth.get().strip()
        if self.ent_batch.get().strip():
            h["batchid"] = self.ent_batch.get().strip()
        self.api = Api(self.ent_base.get().strip(), h)
        return self.api

    # ---------------- API 统一入口：内置浏览器开着时代理到页面里发请求（继承 cookie，避免被踢回 HTML） ----------------
    def api_call(self, method, path, timeout=15, **kw):
        """浏览器活着 → 转发给浏览器线程在页面上下文 fetch；否则回退 urllib。返回原始响应文本。"""
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
        api = self._mk_api()
        if method in ("GET", "NAV"):
            return api.get(path, params=kw.get("params"), timeout=kw.get("timeout", 10))
        return api.post(path, form=kw.get("form"), json_body=kw.get("json_body"),
                        timeout=kw.get("timeout", 10))

    def _pw_nav(self, kw):
        """顶层页面导航到 grablessons——服务器只认这种"真实打开页面"来确立会话当前轮次，
        XHR/fetch 永远无法切换（这就是手动在网页里切轮次后软件才正常的原因）。返回页面 HTML。"""
        if self.pw_page is None:
            raise ValueError("无可用页面")
        base = self.ent_base.get().strip() or "https://xsxk.nuist.edu.cn/xsxk"
        url = base.rstrip("/") + "/elective/grablessons"
        if kw.get("params"):
            url += "?" + urllib.parse.urlencode(kw["params"])
        self.pw_page.goto(url, wait_until="domcontentloaded",
                          timeout=kw.get("timeout", 30) * 1000)
        return self.pw_page.content()

    def _pw_fetch(self, method, path, kw):
        """页面上下文 fetch（同源，最贴近真实操作）优先；页面空白/fetch 失败时降级到浏览器内核直连"""
        try:
            return self._pw_page_fetch(method, path, kw)
        except Exception as e:
            self.log(f"⚠️ 页面内请求失败，改用浏览器内核直连: {str(e)[:80]}")
            return self._pw_ctx_fetch(method, path, kw)

    def _pw_page_fetch(self, method, path, kw):
        """在内置浏览器页面上下文执行 fetch（credentials:'include' 自动带 cookie 会话）"""
        if self.pw_page is None:
            raise ValueError("无可用页面")
        base = self.ent_base.get().strip() or "https://xsxk.nuist.edu.cn/xsxk"
        url = base.rstrip("/") + path
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
        if self.ent_auth.get().strip():
            headers["Authorization"] = self.ent_auth.get().strip()
        if self.ent_batch.get().strip():
            headers["batchid"] = self.ent_batch.get().strip()
        return self.pw_page.evaluate(
            "async (a) => { const r = await fetch(a.url, {method: a.method, headers: a.headers, "
            "body: a.body, credentials: 'include'}); return r.text(); }",
            {"url": url, "method": method, "headers": headers, "body": body})

    def _pw_ctx_fetch(self, method, path, kw):
        """浏览器内核直连（context.request）：共享 cookie，不依赖任何页面加载，不受跨域限制——页面全空白也能发"""
        base = self.ent_base.get().strip() or "https://xsxk.nuist.edu.cn/xsxk"
        url = base.rstrip("/") + path
        headers = {"User-Agent": self.pw_ua or
                   "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
                   "(KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36"}
        if self.ent_auth.get().strip():
            headers["Authorization"] = self.ent_auth.get().strip()
        if self.ent_batch.get().strip():
            headers["batchid"] = self.ent_batch.get().strip()
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

    # ---------------- 内置浏览器登录（Playwright 内建 Chromium，跨平台一致） ----------------
    def open_browser(self):
        try:
            import playwright.sync_api  # noqa: F401
        except ImportError:
            messagebox.showinfo(
                "缺少组件",
                "需要安装内建 Chromium（一次性，约 150MB）：\n\n"
                "python -m pip install playwright\n"
                "python -m playwright install chromium\n\n"
                "注意：要用你平时运行本软件的那个 python 来执行。\n装完重启软件。")
            return
        if getattr(self, "_browser_open", False):
            self.log("内置浏览器已在运行")
            return
        threading.Thread(target=self._browser_thread, daemon=True).start()

    def _browser_thread(self):
        from playwright.sync_api import sync_playwright
        self._browser_open = True
        profile = os.path.join(os.path.dirname(os.path.abspath(__file__)), ".browser-profile")
        base = self.ent_base.get().strip() or "https://xsxk.nuist.edu.cn/xsxk"
        url = base.rstrip("/") + "/profile/index.html"
        try:
            with sync_playwright() as p:
                ctx = p.chromium.launch_persistent_context(
                    profile, headless=False, no_viewport=True, args=["--start-maximized"])

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
                self.log("内置 Chromium 已打开，请登录选课系统（登录态已持久化，下次打开免登录）")
                self.pw_page = ctx.pages[0] if ctx.pages else ctx.new_page()
                self.pw_ctx = ctx
                try:
                    self.pw_ua = self.pw_page.evaluate("navigator.userAgent") or ""
                except Exception:
                    self.pw_ua = ""
                self._pw_alive.set()
                # API 代理泵：浏览器存活期间，代为执行 api_call 转发来的请求
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
            if self.var_debug.get():
                self.cap_q.put({"type": "debug", "msg": f"REQ {req.method} {u.split('/xsxk')[-1]} | body: {(req.post_data or '')[:200]}"})
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
            if self.var_debug.get():
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

    # ---------------- 自动刷新轮次（batchId）与课程类别 ----------------
    def refresh_batch(self, silent=True):
        threading.Thread(target=self._refresh_batch, args=(silent,), daemon=True).start()

    def _refresh_batch(self, silent=True):
        if not self.ent_auth.get().strip():
            return
        now = time.time()
        if silent and now - getattr(self, "_last_refresh", 0) < 2:
            return   # 防抖：捕获 Authorization/batchid 会连发两次，避免重复刷新
        self._last_refresh = now
        # 0. 冷启动：还没有 batchid 时，先导航到默认选课页——页面 JS 会自动发请求，
        #    捕获到 batchid 头后会再次触发本流程
        if not self.ent_batch.get().strip() and self._pw_alive.is_set():
            try:
                self.api_call("NAV", "/elective/grablessons", timeout=30)
                self.log("已打开默认选课页，等待捕获当前轮次…")
            except Exception as e:
                self.log(f"⚠️ 打开默认选课页失败: {e}")
            return
        # 1. 拉全部轮次列表（student.electiveBatchList）
        batches = []
        try:
            form = {}
            if self.ent_batch.get().strip():
                form["batchId"] = self.ent_batch.get().strip()
            j = parse_json_text(self.api_call("POST", "/elective/user", form=form))
            batches = ((j.get("data") or {}).get("student") or {}).get("electiveBatchList") or []
            if batches:
                self._update_batch_list(batches)
            elif not silent:
                self.log(f"轮次列表为空: {str(j.get('msg'))[:80]}")
        except Exception as e:
            self.log(f"⚠️ 获取轮次列表失败: {e}")
        # 2. 自动模式：按时间窗从轮次列表推算当前轮次
        #    （profile 页面的 var batch.code 是无效标识，带它的请求一律被 302，已弃用）
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
            old = self.ent_batch.get().strip()
            if bid != old:
                self.ent_batch.delete(0, "end")
                self.ent_batch.insert(0, bid)
                self._mk_api()
                self.log(f"🔄 批次已自动更新: …{bid[-8:]}（原 {('…' + old[-8:]) if old else '空'}）")
            elif not silent:
                self.log(f"当前批次无变化: …{bid[-8:]}")
        # 3. 拉目标批次的课程类别（内部会按需做页面导航切换服务器轮次）
        target = (self.manual_batch or {}).get("code") or bid or self.ent_batch.get().strip()
        if target:
            self._load_types(target)

    def _load_types(self, batch_code):
        try:
            if batch_code and batch_code != self._nav_batch and self._pw_alive.is_set():
                # 顶层导航切换服务器会话轮次（fetch 做不到），导航返回的 HTML 顺手解析类别
                html2 = self.api_call("NAV", "/elective/grablessons",
                                      params={"batchId": batch_code}, timeout=30)
                self._nav_batch = batch_code
                self.log(f"🔀 服务器轮次已切换（页面导航）: …{batch_code[-8:]}")
            else:
                html2 = self.api_call("GET", "/elective/grablessons", params={"batchId": batch_code})
            types = parse_class_types(html2) or parse_class_types_loose(html2)
            if types:
                self.cmb_type["values"] = [f"{c} {n}" for c, n in types]
                codes = [c for c, _ in types]
                cur = self.cmb_type.get().strip().split(" ")[0] if self.cmb_type.get().strip() else ""
                if cur not in codes:
                    self.cmb_type.set(f"{types[0][0]} {types[0][1]}")
                self.log("本批次课程类别: " + " / ".join(f"{n}({c})" for c, n in types))
            else:
                snippet = re.sub(r"\s+", " ", html2 or "")[:200]
                self.log(f"⚠️ 类别解析失败（页面 {len(html2 or '')} 字符，把这段发给作者）: {snippet}")
        except Exception as e:
            self.log(f"⚠️ 获取课程类别失败: {e}")

    def _update_batch_list(self, batches):
        self.batches = batches
        labels = []
        for b in batches:
            name = b.get("name") or "?"
            label = f"{name}（{fmt_time(b.get('beginTime'))} 开始）"
            if b.get("canSelect") != "1":
                label += " ⚠️不可选"
            labels.append(label)
        self.cmb_batch["values"] = ["自动（当前轮次）"] + labels
        # 保持手动选择
        if self.manual_batch:
            for i, b in enumerate(batches):
                if b.get("code") == self.manual_batch.get("code"):
                    self.cmb_batch.current(i + 1)
                    break
        elif self.cmb_batch.current() < 0:
            self.cmb_batch.current(0)
        if labels:
            self.log(f"发现 {len(labels)} 个选课轮次")

    def on_batch_selected(self, _event=None):
        idx = self.cmb_batch.current()
        if idx <= 0:
            self.manual_batch = None
            self.log("轮次模式: 自动（跟随当前轮次）")
            self.refresh_batch(silent=True)
            return
        b = self.batches[idx - 1]
        self.manual_batch = b
        self.ent_batch.delete(0, "end")
        self.ent_batch.insert(0, b.get("code", ""))
        self._mk_api()
        self.log(f"🎯 已选择轮次: {b.get('name')}｜{fmt_time(b.get('beginTime'))} ~ {fmt_time(b.get('endTime'))}")
        if b.get("canSelect") != "1":
            self.log(f"⚠️ 该轮次当前不可选: {b.get('noSelectReason') or '未到开始时间'}")
        if not self.ent_start.get().strip() and b.get("beginTime"):
            self.ent_start.delete(0, "end")
            self.ent_start.insert(0, fmt_time(b.get("beginTime")))
            self.log("已自动填入开抢时间 = 轮次开始时间")
        # 类别抓取走浏览器导航/请求会阻塞，必须放后台线程，否则 UI 冻结；
        # _load_types 内部会自动做页面导航切换服务器轮次；完成后自动拉课程列表
        threading.Thread(target=self._switch_and_fetch, args=(b.get("code", ""),), daemon=True).start()

    def _switch_and_fetch(self, code):
        """手动选轮次的一条龙：导航切换服务器轮次 → 加载类别 → 自动拉课程列表"""
        self._load_types(code)
        self._fetch_courses()

    def _on_browser_capture(self, data):
        if data.get("type") == "debug":
            self.log("🌐 " + data.get("msg", ""))
            return
        if data.get("type") == "headers":
            hd = data.get("headers") or {}
            changed = []
            for k, v in hd.items():
                if k.lower() == "authorization" and v != self.ent_auth.get().strip():
                    self.ent_auth.delete(0, "end")
                    self.ent_auth.insert(0, v)
                    changed.append("Authorization")
                if k.lower() == "batchid" and v != self.ent_batch.get().strip():
                    self.ent_batch.delete(0, "end")
                    self.ent_batch.insert(0, v)
                    self._nav_batch = v   # 浏览器自己完成了页面导航，服务器会话已是该轮次
                    changed.append("batchid")
            if changed:
                self._mk_api()
                self.log(f"🔑 已自动捕获: {'、'.join(changed)}")
                self.lbl_token.config(text="✅ 已自动捕获登录态", foreground="green")
                self.refresh_batch(silent=True)  # 登录后自动同步当前批次与类别
        elif data.get("type") == "rows":
            rows = data.get("rows") or []
            for r in rows:
                if r.get("JXBID"):
                    self.rows[r["JXBID"]] = r
            if data.get("clazzType"):
                self.cmb_type.set(data["clazzType"])
            self.fill_table()
            self.log(f"📚 浏览器中捕获课程 {len(rows)} 门（课程库累计 {len(self.rows)} 门）")

    def import_session(self):
        path = filedialog.askopenfilename(filetypes=[("JSON", "*.json")])
        if not path:
            return
        try:
            with open(path, encoding="utf-8") as f:
                sess = json.load(f)
        except Exception as e:
            messagebox.showerror("导入失败", str(e))
            return
        if sess.get("baseUrl"):
            self.ent_base.delete(0, "end")
            self.ent_base.insert(0, sess["baseUrl"])
        hd = sess.get("headers") or {}
        for k, v in hd.items():
            if k.lower() == "authorization":
                self.ent_auth.delete(0, "end"); self.ent_auth.insert(0, v)
            if k.lower() == "batchid":
                self.ent_batch.delete(0, "end"); self.ent_batch.insert(0, v)
        if sess.get("listType"):
            self.cmb_type.set(sess["listType"])
        if sess.get("startAt"):
            self.ent_start.delete(0, "end"); self.ent_start.insert(0, sess["startAt"])
        # 导入快照课程库
        for cid, entry in (sess.get("courseDb") or {}).items():
            try:
                self.rows[cid] = json.loads(entry.get("json", "{}"))
            except ValueError:
                pass
        if self.rows:
            self.fill_table()
        self.log(f"已导入会话: {path}（快照课程 {len(self.rows)} 门）")
        self.refresh_batch(silent=True)

    def check_token(self):
        threading.Thread(target=self._check_token, daemon=True).start()

    def _check_token(self):
        try:
            form = {}
            if self.ent_batch.get().strip():
                form["batchId"] = self.ent_batch.get().strip()
            j = parse_json_text(self.api_call("POST", "/elective/user", form=form))
            if j.get("code") == 200:
                stu = (j.get("data") or {}).get("student") or {}
                name = stu.get("XM") or stu.get("XH") or "未知"
                self.lbl_token.config(text=f"✅ Token 有效（{name}）", foreground="green")
                self.log(f"Token 验证通过: {name}")
            else:
                self.lbl_token.config(text=f"❌ {j.get('msg')}", foreground="red")
                self.log(f"Token 验证失败: {j.get('msg')}")
        except Exception as e:
            self.lbl_token.config(text="❌ 请求失败", foreground="red")
            self.log(f"Token 验证异常: {e}")

    def _net_loop(self):
        threading.Thread(target=self._net_once, daemon=True).start()
        self.after(10000, self._net_loop)

    def _net_once(self):
        try:
            t0 = time.time()
            j = parse_json_text(self.api_call("POST", "/web/now", form={}, timeout=5))
            rtt = (time.time() - t0) * 1000
            if j.get("code") == 200 and (j.get("data") or {}).get("currentTime"):
                self.offset = (j["data"]["currentTime"] - (t0 * 1000 + rtt / 2)) / 1000.0
                online = j["data"].get("onlineCount", "?")
                self.lbl_net.config(
                    text=f"🌐 在线 {online} 人 · 延迟 {rtt:.0f}ms · 偏移 {self.offset * 1000:+.0f}ms",
                    foreground="red" if rtt > 500 else "green")
        except Exception:
            self.lbl_net.config(text="🌐 校时失败（网络或服务器异常）", foreground="red")

    # ---------------- 课程列表 ----------------
    def fetch_courses(self):
        threading.Thread(target=self._fetch_courses, daemon=True).start()

    def _fetch_courses(self):
        ctype = (self.cmb_type.get().strip().split(" ")[0]) or "TYKC"  # 下拉项形如 "XGKC 通识选修课"
        self.log(f"拉取课程列表（{ctype}）…")
        try:
            payload = {
                "teachingClassType": ctype,
                "pageNumber": 1,
                "pageSize": 200,
                "orderBy": "",
                "campus": self.ent_campus.get().strip() or "01",
            }
            if ctype != "ALLKC":  # 真实流量：ALLKC 不带 SFYX，其他类别带
                payload["SFYX"] = "2"
            j = parse_json_text(self.api_call("POST", "/elective/nuist/clazz/list", json_body=payload))
        except Exception as e:
            self.log(f"❌ 获取课程列表失败: {e}")
            return
        self.rows = {}
        walk_rows(j, self.rows)
        self.log(f"获取到 {len(self.rows)} 门课程")
        if self.rows:
            sample = next(iter(self.rows.values()))
            self.log("原始行样本（若课程名/容量列显示异常，把这段发给作者）: "
                     + json.dumps(sample, ensure_ascii=False)[:800])
        self.fill_table()

    def fill_table(self):
        kw = self.ent_search.get().strip()
        self.tree.delete(*self.tree.get_children())
        for cid, row in self.rows.items():
            raw = json.dumps(row, ensure_ascii=False)
            if kw and kw not in raw:
                continue
            self.tree.insert("", "end", iid=cid, values=(
                row_name(row) or "(见详情)",
                pick(row, F_TEACHER),
                row_time(row)[:30],
                pick(row, F_CAP),
                pick(row, F_CHOSEN),
                cid,
            ))

    def add_volunteer(self):
        sel = self.tree.selection()
        if not sel:
            return
        ctype = (self.cmb_type.get().strip().split(" ")[0]) or "TYKC"
        for cid in sel:
            row = self.rows.get(cid)
            if not row:
                continue
            name = row_name(row) or cid
            teacher = pick(row, F_TEACHER)
            if any(v["cid"] == cid for v in self.volunteers):
                continue
            self.volunteers.append({"cid": cid, "secret": row["secretVal"], "type": ctype,
                                    "label": f"{name} {teacher}"})
        self.refresh_vol()

    def refresh_vol(self):
        self.lst.delete(0, "end")
        for i, v in enumerate(self.volunteers):
            self.lst.insert("end", f"{i + 1}. {v['label']}")

    def move_vol(self, d):
        sel = self.lst.curselection()
        if not sel:
            return
        i = sel[0]
        j = i + d
        if 0 <= j < len(self.volunteers):
            self.volunteers[i], self.volunteers[j] = self.volunteers[j], self.volunteers[i]
            self.refresh_vol()
            self.lst.selection_set(j)

    def del_vol(self):
        sel = self.lst.curselection()
        if not sel:
            return
        del self.volunteers[sel[0]]
        self.refresh_vol()

    # ---------------- 配置 ----------------
    def save_config(self):
        path = filedialog.asksaveasfilename(defaultextension=".json",
                                            initialfile="xsxk-config.json")
        if not path:
            return
        data = {
            "baseUrl": self.ent_base.get().strip(),
            "headers": {"Authorization": self.ent_auth.get().strip(),
                        "batchid": self.ent_batch.get().strip()},
            "listType": self.cmb_type.get().strip(),
            "startAt": self.ent_start.get().strip(),
            "retryInterval": int(self.ent_interval.get() or 400),
            "volunteerMode": self.var_volmode.get(),
            "manualBatch": bool(self.manual_batch),
            "volunteers": self.volunteers,
        }
        with open(path, "w", encoding="utf-8") as f:
            json.dump(data, f, ensure_ascii=False, indent=2)
        self.log(f"配置已保存: {path}")

    def load_config(self):
        path = filedialog.askopenfilename(filetypes=[("JSON", "*.json")])
        if not path:
            return
        with open(path, encoding="utf-8") as f:
            data = json.load(f)
        self.ent_base.delete(0, "end"); self.ent_base.insert(0, data.get("baseUrl", ""))
        hd = data.get("headers") or {}
        self.ent_auth.delete(0, "end"); self.ent_auth.insert(0, hd.get("Authorization", ""))
        self.ent_batch.delete(0, "end"); self.ent_batch.insert(0, hd.get("batchid", ""))
        self.cmb_type.set(data.get("listType", "TYKC"))
        self.ent_start.delete(0, "end"); self.ent_start.insert(0, data.get("startAt", ""))
        self.ent_interval.delete(0, "end"); self.ent_interval.insert(0, str(data.get("retryInterval", 400)))
        self.var_volmode.set(bool(data.get("volunteerMode", False)))
        if data.get("manualBatch") and hd.get("batchid"):
            self.manual_batch = {"code": hd["batchid"], "name": "（配置中的轮次）"}
        else:
            self.manual_batch = None
        self.volunteers = data.get("volunteers") or []
        self.refresh_vol()
        self.log(f"配置已加载: {path}")

    # ---------------- 抢课 ----------------
    def start_grab(self):
        if not self.volunteers:
            messagebox.showwarning("提示", "志愿队列为空，请先从课程列表加入志愿")
            return
        self.stop_flag.clear()
        self.btn_go.config(state="disabled")
        threading.Thread(target=self._grab_loop, daemon=True).start()

    def stop_grab(self):
        self.stop_flag.set()
        self.btn_go.config(state="normal")
        self.log("已手动停止")

    def _grab_loop(self):
        self._refresh_batch(silent=True)   # 开抢前自动同步最新批次与类别（切轮次无需手动操作）
        interval = (int(self.ent_interval.get() or 400)) / 1000.0
        start_at = self.ent_start.get().strip()

        if start_at:
            try:
                target = parse_start_time(start_at)
                while not self.stop_flag.is_set():
                    remain = target - (time.time() + self.offset)
                    if remain <= 0:
                        break
                    self.lbl_cd.config(text=f"距开抢 {remain:.1f} 秒")
                    time.sleep(0.1)
            except ValueError:
                self.log("⚠️ 开抢时间格式错误，立即开始")
        self.lbl_cd.config(text="")
        if self.stop_flag.is_set():
            return
        self.log("⏰ 开抢！")

        vols = list(self.volunteers)
        types_in_queue = {v["type"] for v in vols}
        if self.var_volmode.get() and types_in_queue != {"XGKC"}:
            self.log("⚠️ 提醒：志愿模式通常只用于通识选修课(XGKC)，当前队列含其他类别")
        if not self.var_volmode.get() and "XGKC" in types_in_queue:
            self.log("⚠️ 提醒：队列含通识选修课(XGKC)，通识课必须带志愿级别提交，建议勾选「志愿模式」")

        # ---------- 志愿模式（通识课 XGKC）：按队列顺序批量提交全部志愿 ----------
        if self.var_volmode.get():
            batch = self.ent_batch.get().strip()
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
                # 抽签不拼速度，但必须交得上：瞬时错误最多重试 3 次
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
                        break   # 无重试意义
                    if attempt < 3:
                        self.log(f"🔁 「{v['label']}」第{attempt}次提交未成功（{msg[:60]}），稍后重试")
                        time.sleep(max(interval, 0.5))
                else:
                    pass
                if typ == "auth":
                    self.log(f"🔒 {msg} —— 登录态失效，停止")
                    break
                if typ not in ("success",) and attempt >= 3 and typ in ("notOpen", "fail", "badHtml"):
                    self.log(f"❌ 「{v['label']}」3 次尝试均失败: {msg}")
                elif typ in ("full", "impossible"):
                    self.log(f"⛔ 「{v['label']}」{msg}")
                time.sleep(interval)
            self.log(f"志愿提交结束：成功 {ok_count}/{len(vols)}")
            messagebox.showinfo("完成", f"志愿提交结束：成功 {ok_count}/{len(vols)}")
            self.btn_go.config(state="normal")
            return

        # ---------- 抢课模式（先到先得）：选中即停，满员切备选 ----------
        idx = 0
        while not self.stop_flag.is_set():
            v = vols[idx]
            try:
                text = self.api_call("POST", "/elective/clazz/add", form={
                    "clazzType": v["type"], "clazzId": v["cid"], "secretVal": v["secret"],
                })
                typ, msg = judge(text)
            except Exception as e:
                typ, msg = "fail", f"网络异常: {e}"

            if typ == "success":
                self.log(f"✅ 选上「{v['label']}」！({msg})")
                messagebox.showinfo("抢课成功", f"选上：{v['label']}")
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
                messagebox.showerror("登录失效", "请重新登录并导入会话")
                break
            else:
                self.log(f"❌ 「{v['label']}」{typ}: {msg}")
            time.sleep(interval)
        self.btn_go.config(state="normal")


if __name__ == "__main__":
    App().mainloop()
