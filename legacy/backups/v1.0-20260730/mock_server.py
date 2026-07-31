# -*- coding: utf-8 -*-
"""
抢课脚本本地模拟测试服务器
运行: python mock_server.py
然后浏览器打开 http://localhost:8655
模拟场景:
  - 「杨秀章」永远返回 课容量已满  -> 测试"满员切备选"
  - 「王明德」前 2 次失败, 第 3 次成功  -> 测试"重试直到成功"
  - 其他课程永远返回 本轮次选课暂未开始  -> 测试"持续轮询"
"""
import json
import time
from http.server import BaseHTTPRequestHandler, HTTPServer
from urllib.parse import urlparse, parse_qs

PORT = 8655
attempts = {}

PAGE = """<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<title>模拟选课系统</title>
<style>
  body { font-family: sans-serif; background: #f0f2f5; margin: 0; padding: 20px; }
  h2 { color: #303133; }
  .grid { display: grid; grid-template-columns: repeat(3, 1fr); gap: 16px; max-width: 1100px; }
  .card { background: #fff; border: 1px solid #e4e7ed; border-radius: 6px; padding: 14px; position: relative; }
  .card .title { font-weight: bold; color: #303133; margin-bottom: 6px; }
  .card .sub { color: #606266; font-size: 13px; margin-bottom: 4px; }
  .card .cap { color: #909399; font-size: 12px; }
  .selbtn { position: absolute; top: 10px; right: 10px; background: #409eff; color: #fff;
            border: none; border-radius: 4px; padding: 5px 14px; cursor: pointer; font-size: 13px; }
  .el-message-box { display: none; position: fixed; top: 30%; left: 50%; transform: translateX(-50%);
                    width: 420px; background: #fff; border-radius: 6px; z-index: 3000;
                    box-shadow: 0 2px 12px rgba(0,0,0,.3); padding-bottom: 14px; }
  .el-message-box__header { padding: 14px; font-weight: bold; border-bottom: 1px solid #ebeef5; }
  .el-message-box__content { padding: 18px; color: #606266; }
  .el-message-box__btns { text-align: right; padding: 0 14px; }
  .el-message-box__btns button { border: 1px solid #dcdfe6; background: #fff; border-radius: 4px;
                                 padding: 7px 16px; cursor: pointer; margin-left: 10px; }
  .el-message-box__btns .el-button--primary { background: #409eff; color: #fff; border-color: #409eff; }
  .mask { display: none; position: fixed; inset: 0; background: rgba(0,0,0,.4); z-index: 2999; }
</style>
</head>
<body>
<h2>模拟选课系统（本地测试页）</h2>
<div class="grid" id="grid"></div>
<div class="mask" id="mask"></div>
<div class="el-message-box" id="mb">
  <div class="el-message-box__header"><span>提醒</span></div>
  <div class="el-message-box__content"><p>确认选择课程吗？</p></div>
  <div class="el-message-box__btns">
    <button type="button" id="cancel"><span>取消</span></button>
    <button type="button" class="el-button--primary" id="ok"><span>确定</span></button>
  </div>
</div>
<script>
// 页面加载时请求课程列表（模拟真实系统，让抢课脚本能捕获 JXBID/secretVal）
fetch('/xsxk/elective/clazz/list', {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ teachingClassType: 'TYKC', pageNumber: 1, pageSize: 100 }),
});
const CLAZZS = [
  { id: 'C001', teacher: '[84]杨秀章', name: '马原挂牌模块4-1班', cap: 160, chosen: 160 },
  { id: 'C002', teacher: '[85]王明德', name: '马原挂牌模块4-2班', cap: 160, chosen: 159 },
  { id: 'C003', teacher: '[86]陈国强', name: '马原挂牌模块4-3班', cap: 99,  chosen: 0 },
  { id: 'C004', teacher: '[49]李雪峰', name: '体育（3）板块2：篮球', cap: 40, chosen: 0 },
  { id: 'C005', teacher: '[51]王芳',   name: '体育（3）板块2：健美操', cap: 40, chosen: 0 },
  { id: 'C006', teacher: '[70]许晴岚', name: '羽毛球模块-12班', cap: 36, chosen: 0 },
];
let current = null;
const grid = document.getElementById('grid');
CLAZZS.forEach(c => {
  const d = document.createElement('div');
  d.className = 'card';
  d.innerHTML = `<button class="selbtn">选择</button>
    <div class="title">${c.teacher}</div>
    <div class="sub">${c.name}</div>
    <div class="sub">1-16周 星期三 第9节-第11节</div>
    <div class="cap">课容量：${c.cap}人　已选：${c.chosen}人</div>
    <div class="cap">订教材：未订购</div>`;
  d.querySelector('.selbtn').onclick = () => { current = c; show(true); };
  grid.appendChild(d);
});
function show(v) {
  document.getElementById('mb').style.display = v ? 'block' : 'none';
  document.getElementById('mask').style.display = v ? 'block' : 'none';
}
document.getElementById('cancel').onclick = () => show(false);
document.getElementById('ok').onclick = () => {
  show(false);
  if (!current) return;
  fetch('/xsxk/elective/clazz/add', {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body: 'clazzType=TYKC&clazzId=' + current.id + '&secretVal=mocktoken',
  }).then(r => r.json()).then(j => {
    const t = document.createElement('div');
    t.textContent = j.msg;
    t.style.cssText = 'position:fixed;top:20px;left:50%;transform:translateX(-50%);background:#fff;border:1px solid #ebeef5;border-radius:4px;padding:10px 20px;z-index:4000;box-shadow:0 2px 8px rgba(0,0,0,.15)';
    document.body.appendChild(t);
    setTimeout(() => t.remove(), 3000);
  });
};
</script>
</body>
</html>
"""


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *a):
        pass

    def do_GET(self):
        body = PAGE.encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "text/html; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_POST(self):
        length = int(self.headers.get("Content-Length", 0))
        raw = self.rfile.read(length).decode("utf-8", "ignore")
        path = urlparse(self.path).path

        if path == "/xsxk/web/now":
            resp = {"code": 200, "msg": "ok", "data": {"currentTime": int(time.time() * 1000), "onlineCount": 1}}
            self._send_json(resp)
            return

        if path in ("/xsxk/elective/clazz/list", "/xsxk/elective/nuist/clazz/list"):
            rows = [
                {"JXBID": "C001", "secretVal": "tok-C001", "KCM": "马原挂牌模块4-1班", "SKJS": "杨秀章", "capacity": 160},
                {"JXBID": "C002", "secretVal": "tok-C002", "KCM": "马原挂牌模块4-2班", "SKJS": "王明德", "capacity": 160},
                {"JXBID": "C003", "secretVal": "tok-C003", "KCM": "马原挂牌模块4-3班", "SKJS": "陈国强", "capacity": 99},
                {"JXBID": "C004", "secretVal": "tok-C004", "KCM": "体育（3）板块2：篮球", "SKJS": "李雪峰", "capacity": 40},
                {"JXBID": "C005", "secretVal": "tok-C005", "KCM": "体育（3）板块2：健美操", "SKJS": "王芳", "capacity": 40},
                {"JXBID": "C006", "secretVal": "tok-C006", "KCM": "羽毛球模块-12班", "SKJS": "许晴岚", "capacity": 36},
            ]
            self._send_json({"code": 200, "msg": "ok", "data": {"rows": rows}})
            return

        if path == "/xsxk/volunteer/list/choose":
            used = sum(1 for k, v in attempts.items() if v > 0)
            grades = [{"grade": g, "name": f"第{g}志愿", "isUse": (True if g <= used else None)}
                      for g in range(1, 9)]
            self._send_json({"code": 200, "msg": "操作成功", "data": grades})
            return

        cid = parse_qs(raw).get("clazzId", [""])[0]
        attempts[cid] = attempts.get(cid, 0) + 1

        if path != "/xsxk/elective/clazz/add":
            resp = {"code": 404, "msg": "not found", "data": None}
        elif cid == "C001":
            resp = {"code": 500, "msg": "课容量已满，请选择其他课程", "data": None}
        elif cid == "C002":
            if attempts[cid] < 3:
                resp = {"code": 500, "msg": "选课失败，请稍后再试", "data": None}
            else:
                resp = {"code": 200, "msg": "选课成功", "data": None}
        else:
            resp = {"code": 500, "msg": "本轮次选课暂未开始", "data": None}

        self._send_json(resp)
        print(f"[mock] {cid} 第{attempts[cid]}次 -> {resp['msg']}")

    def _send_json(self, obj):
        body = json.dumps(obj, ensure_ascii=False).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)


if __name__ == "__main__":
    print(f"模拟选课系统已启动: http://localhost:{PORT}")
    print("测试完毕后回到这里按 Ctrl+C 停止")
    HTTPServer(("127.0.0.1", PORT), Handler).serve_forever()
