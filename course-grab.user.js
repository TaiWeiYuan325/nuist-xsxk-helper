// ==UserScript==
// @name         NUIST 选课抢课助手
// @namespace    https://local.kimi/course-grab
// @version      1.1
// @description  连点式抢课：点击"选择"→自动点 el-message-box 确定→解析 /elective/clazz/add 响应→满员/冲突立即切备选，其余失败循环重试
// @match        *://*.nuist.edu.cn/*
// @match        http://localhost:8655/*
// @match        http://127.0.0.1:8655/*
// @grant        none
// @run-at       document-start
// ==/UserScript==

(function () {
  'use strict';

  /******************* 配置（面板修改后会自动保存到 localStorage） *******************/
  const DEFAULTS = {
    courses: [],          // 志愿关键词（按优先级），匹配卡片内文字，如 '杨秀章' 或 '马原挂牌模块4-1班'
    startAt: '',          // 开抢时间 '2026-07-31 16:00:00'，留空手动开抢
    retryInterval: 400,   // 重试间隔 ms
    turbo: false,         // 极速模式：重试时直接重放捕获的请求，不再走点击
  };
  const state = {
    cfg: { ...DEFAULTS, ...(JSON.parse(localStorage.getItem('cg_cfg') || '{}')) },
    running: false,
    autoStarted: false,   // 倒计时自动开抢只触发一次；手动开始后置位，防止停止后被定时器复活
    courseIdx: 0,
    lastAdd: { time: 0, body: '', resp: '' },   // 最近一次选课接口的捕获
    captured: {},                                // courseIdx -> 请求体（极速模式用）
    turboFail: 0,
    headers: {},        // 从页面请求中捕获的请求头（Authorization / batchid 等）
    courseDb: {},       // JXBID -> { json, secretVal, JXBID }，来自课程列表接口
    listType: '',       // 页面列表请求里的 teachingClassType（如 TYKC）
    listBody: '',       // 页面列表请求的原始载荷（导出给无头抢课程序刷新参数用）
    timeOffset: 0,      // 服务器时间 - 本地时间（毫秒），校时结果
    timeSynced: false,
    onlineCount: null,  // 选课系统当前在线人数（/web/now 返回）
    rtt: null,          // 最近一次校时请求的往返延迟 ms
    lastSync: 0,
  };
  const saveCfg = () => localStorage.setItem('cg_cfg', JSON.stringify(state.cfg));
  window.__cg = state; // 调试入口：Console 里可用 __cg.courseDb 查看捕获的课程数据

  /******************* 拦截选课接口（XHR + fetch），捕获响应、请求头和课程参数 *******************/
  const ADD_RE = /\/elective\/clazz\/add/;
  const LIST_RE = /\/elective\/(nuist\/)?clazz\/list/;
  function record(url, body, resp) {
    if (!ADD_RE.test(url)) return;
    state.lastAdd = { time: Date.now(), body: String(body || ''), resp: String(resp || '') };
  }
  function recordHeaders(h) {
    if (!h) return;
    for (const k of Object.keys(h)) {
      // 只保留认证和业务头，避免带上有害的 content-length 等
      if (/^(authorization|batchid|token|x-requested-with)$/i.test(k)) state.headers[k] = h[k];
    }
  }
  // 从列表响应中提取每门课的 JXBID + secretVal（参考 xsxk-nuist 项目：secretVal 由列表接口下发）
  function ingestList(body, resp) {
    let type = '';
    try {
      if (body) {
        state.listBody = String(body);
        const bj = JSON.parse(body);
        if (bj && bj.teachingClassType) { state.listType = bj.teachingClassType; type = bj.teachingClassType; }
      }
    } catch (e) {}
    let j = null;
    try { j = JSON.parse(resp); } catch (e) { return; }
    const walk = (o) => {
      if (!o) return;
      if (Array.isArray(o)) { o.forEach(walk); return; }
      if (typeof o === 'object') {
        if (o.JXBID && o.secretVal) {
          state.courseDb[o.JXBID] = { json: JSON.stringify(o), secretVal: o.secretVal, JXBID: o.JXBID, type };
        }
        Object.keys(o).forEach(k => walk(o[k]));
      }
    };
    walk(j);
  }
  const oo = XMLHttpRequest.prototype.open, os = XMLHttpRequest.prototype.send,
        oh = XMLHttpRequest.prototype.setRequestHeader;
  XMLHttpRequest.prototype.open = function (m, u) { this.__u = u; this.__h = {}; return oo.apply(this, arguments); };
  XMLHttpRequest.prototype.setRequestHeader = function (k, v) {
    try { (this.__h = this.__h || {})[k] = v; } catch (e) {}
    return oh.apply(this, arguments);
  };
  XMLHttpRequest.prototype.send = function (body) {
    this.__b = body;
    this.addEventListener('load', function () {
      record(this.__u, this.__b, this.responseText);
      if (/\/elective\//.test(this.__u || '')) recordHeaders(this.__h);
      if (LIST_RE.test(this.__u || '')) ingestList(this.__b, this.responseText);
    });
    return os.apply(this, arguments);
  };
  const of = window.fetch;
  window.fetch = async function (url, opts) {
    const res = await of.apply(this, arguments);
    try {
      const u = typeof url === 'string' ? url : url.url;
      if (opts && opts.headers && /\/elective\//.test(u)) {
        const h = opts.headers instanceof Headers ? Object.fromEntries(opts.headers.entries()) : opts.headers;
        recordHeaders(h);
      }
      if (ADD_RE.test(u)) res.clone().text().then(t => record(u, opts && opts.body, t));
      if (LIST_RE.test(u)) res.clone().text().then(t => ingestList(opts && opts.body, t));
    } catch (e) {}
    return res;
  };

  /******************* 服务器校时（POST /web/now，参考 xsxk-nuist） *******************/
  const now = () => Date.now() + state.timeOffset;
  async function syncTime() {
    try {
      const t0 = Date.now();
      const res = await of.call(window, '/xsxk/web/now', { method: 'POST', credentials: 'include', headers: { ...state.headers } });
      const j = await res.json();
      const t1 = Date.now();
      state.lastSync = Date.now();
      state.rtt = t1 - t0;
      if (j && j.code === 200 && j.data && j.data.currentTime) {
        state.timeOffset = j.data.currentTime - Math.round((t0 + t1) / 2);
        state.timeSynced = true;
        if (typeof j.data.onlineCount === 'number') state.onlineCount = j.data.onlineCount;
      }
      // 更新面板上的网络状态行
      const el = document.getElementById('cg-net');
      if (el) {
        const online = state.onlineCount != null ? `在线 ${state.onlineCount} 人` : '在线 -';
        const rtt = state.rtt != null ? `延迟 ${state.rtt}ms` : '延迟 -';
        el.textContent = `🌐 ${online} · ${rtt} · 偏移 ${state.timeOffset >= 0 ? '+' : ''}${state.timeOffset}ms`;
        el.style.color = state.rtt != null && state.rtt > 500 ? '#f38ba8' : '#a6e3a1';
      }
    } catch (e) {}
  }

  /******************* 工具 *******************/
  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
  const visible = (el) => {
    if (!el) return false;
    const r = el.getBoundingClientRect(), s = getComputedStyle(el);
    return r.width > 0 && r.height > 0 && s.display !== 'none' && s.visibility !== 'hidden';
  };

  // 在含关键词的卡片里找"选择"按钮（限定 .el-card.jxb-card 卡片范围，避免误点页面其他同名元素）
  function findSelectButton(keyword) {
    const cards = [...document.querySelectorAll('.el-card.jxb-card')];
    const card = cards.find(c => visible(c) && (c.innerText || '').includes(keyword));
    if (card) {
      return [...card.querySelectorAll('button')].find(b =>
        visible(b) && (b.innerText || '').trim() === '选择') || null;
    }
    // 兜底：卡片 class 变了时退回模糊匹配
    const btns = [...document.querySelectorAll('button')].filter(b =>
      visible(b) && (b.innerText || '').trim() === '选择');
    for (const b of btns) {
      let box = b;
      for (let i = 0; i < 8 && box; i++) {
        const t = box.innerText || '';
        if (t.includes(keyword) && t.includes('课容量')) return b;
        box = box.parentElement;
      }
    }
    return null;
  }

  // Element UI 确认弹窗的"确定"按钮（兼容 el-message-box 和 el-dialog）
  function findConfirm() {
    const box = document.querySelector('.el-message-box');
    if (box && visible(box)) {
      const b = box.querySelector('.el-message-box__btns .el-button--primary');
      if (b) return b;
    }
    for (const dlg of document.querySelectorAll('.el-dialog')) {
      if (!visible(dlg)) continue;
      const b = [...dlg.querySelectorAll('button')].find(x =>
        (x.innerText || '').trim() === '确定' && visible(x));
      if (b) return b;
    }
    return null;
  }
  // 结果提示弹窗（选课结果 el-message），顺便关掉它
  function dismissMessage() {
    document.querySelectorAll('.el-message').forEach(m => m.remove());
  }
  // 读取当前页面上的 toast 提示文本（el-message / el-notification）
  function readToasts() {
    const texts = [];
    document.querySelectorAll('.el-message, .el-notification').forEach(m => {
      const t = (m.innerText || '').trim();
      if (t && visible(m)) texts.push(t);
    });
    return texts;
  }
  // 取卡片概要文字（课容量/已选），用于诊断
  function cardSummary(btn, keyword) {
    let box = btn;
    for (let i = 0; i < 8 && box; i++) {
      const t = (box.innerText || '').replace(/\s+/g, ' ');
      if (t.includes(keyword) && t.includes('课容量')) return t.slice(0, 120);
      box = box.parentElement;
    }
    return '';
  }

  /******************* 结果判定 *******************/
  function judge(respText) {
    // 服务器返回 HTML 页面（通常是请求头不对或登录态失效）
    if (/^\s*</.test(respText || '')) return { type: 'badHtml', msg: '服务器返回了HTML页面而非JSON' };
    let j = null;
    try { j = JSON.parse(respText); } catch (e) {}
    const msg = ((j && j.msg) || respText || '').slice(0, 120);
    const code = j ? j.code : null;
    if (code === 200 || /成功/.test(msg)) return { type: 'success', msg };
    if (/满|容量/.test(msg)) return { type: 'full', msg };
    if (/冲突|已选|重复|超过/.test(msg)) return { type: 'impossible', msg };
    if (/未登录|登录失效|认证失败|token失效|token过期/i.test(msg)) return { type: 'auth', msg };
    // 以下都是"保持当前志愿继续重试"（参考 xsxk-nuist 的 RETRYABLE_MESSAGES）
    if (/暂未开始|未开始|未开放|不在.*时间|结束|系统繁忙|稍后|队列拥堵|请求频繁|超时/.test(msg)) return { type: 'notOpen', msg };
    return { type: 'fail', msg: msg || '未知响应' };
  }

  /******************* 主流程 *******************/
  let logEl = null;
  const log = (m) => {
    if (!logEl) return;
    m = String(m).replace(/\s+/g, ' ').slice(0, 200); // 截断，防止超长响应刷屏
    const line = document.createElement('div');
    line.textContent = `[${new Date().toLocaleTimeString('zh-CN', { hour12: false })}.${String(Date.now() % 1000).padStart(3, '0')}] ${m}`;
    logEl.prepend(line);
    while (logEl.children.length > 40) logEl.lastChild.remove();
  };

  async function clickAttempt(keyword) {
    const btn = findSelectButton(keyword);
    if (!btn) return { type: 'noEntry', msg: '未找到按钮' };
    const t0 = Date.now();
    dismissMessage(); // 清掉旧 toast，避免误判
    btn.click();
    // 等确认弹窗（最多 3s）；期间若出现 toast 则按 toast 内容判定
    let ok = false;
    for (let i = 0; i < 30; i++) {
      const c = findConfirm();
      if (c) { c.click(); ok = true; break; }
      const toasts = readToasts();
      if (toasts.length) {
        const r = judge(toasts.join(' | '));
        dismissMessage();
        return r;
      }
      // 有些情况下接口直接返回（无确认弹窗）
      if (state.lastAdd.time >= t0) {
        state.captured[state.courseIdx] = state.lastAdd.body;
        return judge(state.lastAdd.resp);
      }
      await sleep(100);
    }
    if (!ok) {
      const info = cardSummary(btn, keyword);
      return { type: 'noModal', msg: '未弹确认框' + (info ? '｜卡片: ' + info : '') };
    }
    // 记住这门课的请求体，供极速模式重放
    // 等接口响应（最多 3s）
    for (let i = 0; i < 30; i++) {
      if (state.lastAdd.time >= t0) {
        state.captured[state.courseIdx] = state.lastAdd.body;
        dismissMessage();
        return judge(state.lastAdd.resp);
      }
      await sleep(100);
    }
    dismissMessage();
    return { type: 'timeout', msg: '接口无响应' };
  }

  // 纯 API 极速模式：直接用列表接口下发的 JXBID + secretVal 提交，无需点击（参考 xsxk-nuist）
  function findCourseInDb(keyword) {
    for (const id of Object.keys(state.courseDb)) {
      if (state.courseDb[id].json.includes(keyword)) return state.courseDb[id];
    }
    return null;
  }
  // 页面卡片是服务端渲染的，不调列表接口；极速模式下脚本主动拉取各类别列表建立课程库
  const API_TYPES = ['TYKC', 'XGKC', 'FANKC', 'ALLKC', 'FAWKC', 'TJKC', 'CXKC'];
  const triedTypes = new Set();
  async function ensureCourseDb(keyword) {
    if (findCourseInDb(keyword)) return true;
    if (!Object.keys(state.headers).length) return false; // 还没有认证头
    for (const t of API_TYPES) {
      if (triedTypes.has(t)) continue;
      triedTypes.add(t);
      try {
        const res = await of.call(window, '/xsxk/elective/nuist/clazz/list', {
          method: 'POST',
          credentials: 'include',
          headers: { ...state.headers, 'Content-Type': 'application/json' },
          body: JSON.stringify({ teachingClassType: t, pageNumber: 1, pageSize: 200, orderBy: '', campus: '01', SFYX: '2' }),
        });
        const text = await res.text();
        if (!state.listType) state.listType = t;
        ingestList(JSON.stringify({ teachingClassType: t }), text);
        log(`极速模式：已拉取 ${t} 类别课程（累计 ${Object.keys(state.courseDb).length} 门）`);
        if (findCourseInDb(keyword)) { state.listType = t; return true; }
      } catch (e) { /* 某个类别失败就跳过 */ }
    }
    return !!findCourseInDb(keyword);
  }
  async function turboAttempt(keyword) {
    const hit = findCourseInDb(keyword);
    if (!hit) return null; // 课程库还没有这门课（列表未加载），回退点击模式
    const body = 'clazzType=' + encodeURIComponent(hit.type || state.listType || '') +
                 '&clazzId=' + encodeURIComponent(hit.JXBID) +
                 '&secretVal=' + encodeURIComponent(hit.secretVal);
    try {
      const res = await of.call(window, '/xsxk/elective/clazz/add', {
        method: 'POST',
        credentials: 'include',
        headers: { ...state.headers, 'Content-Type': 'application/x-www-form-urlencoded' },
        body,
      });
      return judge(await res.text());
    } catch (e) {
      return { type: 'fail', msg: '极速请求异常: ' + e.message };
    }
  }

  async function tryOnce() {
    const keyword = state.cfg.courses[state.courseIdx];
    if (!keyword) { stop(); log('⚠️ 请先在面板里填志愿关键词'); return; }

    let r = null;
    if (state.cfg.turbo) {
      if (!findCourseInDb(keyword)) await ensureCourseDb(keyword); // 页面不调列表接口，主动补拉
      r = await turboAttempt(keyword);
      if (r && (r.type === 'fail' || r.type === 'badHtml')) {
        // badHtml 说明请求被服务器拒绝，立即回退；fail 累计 3 次回退
        state.turboFail = r.type === 'badHtml' ? 3 : state.turboFail + 1;
        if (state.turboFail >= 3) {
          log('极速模式不可用（' + r.msg + '），回退到点击模式');
          state.cfg.turbo = false;
        }
      }
    }
    if (!r) { state.turboFail = 0; r = await clickAttempt(keyword); }

    switch (r.type) {
      case 'success':
        log(`✅ 选上「${keyword}」！`);
        stop();
        alert(`选课成功：${keyword}`);
        break;
      case 'full':
      case 'impossible':
        if (state.courseIdx < state.cfg.courses.length - 1) {
          state.courseIdx++;
          log(`⛔ 「${keyword}」${r.msg} → 切备选「${state.cfg.courses[state.courseIdx]}」`);
        } else {
          state.courseIdx = 0;
          log(`⛔ 「${keyword}」${r.msg}，已是最后志愿，从头轮询…`);
        }
        break;
      case 'notOpen':
        log(`⏳ ${r.msg}，继续等待…`);
        break;
      case 'auth':
        log(`🔒 ${r.msg} —— 登录态失效，脚本已停止，请重新登录后再开抢`);
        stop();
        alert('登录态失效，请刷新页面重新登录');
        break;
      case 'noEntry':
        log(`未找到「${keyword}」的选择按钮（请先搜索/翻页让卡片显示在页面上）`);
        break;
      default:
        log(`❌ 「${keyword}」${r.type}: ${r.msg}`);
    }
  }

  async function loop() {
    while (state.running) {
      try { await tryOnce(); } catch (e) { log('异常: ' + e.message); }
      if (state.running) await sleep(Number(state.cfg.retryInterval) || 400);
    }
  }
  const start = () => { if (!state.running) { state.running = true; state.autoStarted = true; state.courseIdx = 0; loop(); } };
  const stop = () => { state.running = false; };

  /******************* 控制面板 *******************/
  function buildPanel() {
    const p = document.createElement('div');
    p.style.cssText = 'position:fixed;top:60px;right:12px;z-index:999999;width:280px;background:#1e1e2e;color:#cdd6f4;border-radius:10px;padding:10px;font:12px/1.6 sans-serif;box-shadow:0 4px 16px rgba(0,0,0,.4)';
    p.innerHTML = `
      <div style="font-weight:bold;margin-bottom:6px">🎯 抢课助手 <span style="opacity:.6;font-weight:normal">v1.1</span></div>
      <textarea id="cg-courses" rows="3" placeholder="志愿关键词，一行一个（按优先级）" style="width:100%;box-sizing:border-box;background:#313244;color:#cdd6f4;border:1px solid #45475a;border-radius:5px;padding:4px"></textarea>
      <div style="display:flex;gap:6px;margin:6px 0">
        <input id="cg-startat" placeholder="开抢时间 2026-07-31 16:00:00" style="flex:1;background:#313244;color:#cdd6f4;border:1px solid #45475a;border-radius:5px;padding:3px 6px">
        <input id="cg-interval" title="重试间隔(ms)" style="width:64px;background:#313244;color:#cdd6f4;border:1px solid #45475a;border-radius:5px;padding:3px 6px">
      </div>
      <label style="display:block;margin-bottom:6px;cursor:pointer"><input type="checkbox" id="cg-turbo"> 极速模式（API 直发免点击，失效自动回退）</label>
      <div style="margin-bottom:6px">
        <button id="cg-start" style="background:#a6e3a1;border:none;border-radius:5px;padding:4px 14px;cursor:pointer">开抢</button>
        <button id="cg-stop" style="background:#f38ba8;border:none;border-radius:5px;padding:4px 14px;cursor:pointer;margin-left:6px">停止</button>
        <button id="cg-export" title="导出会话参数（token+课程参数），供无网页的 Python 抢课程序使用" style="background:#89b4fa;border:none;border-radius:5px;padding:4px 10px;cursor:pointer;margin-left:6px">导出会话</button>
      </div>
      <div id="cg-cd" style="color:#89b4fa"></div>
      <div id="cg-net" style="color:#a6e3a1"></div>
      <div id="cg-log" style="max-height:200px;overflow:auto;margin-top:6px;border-top:1px solid #45475a;padding-top:6px"></div>`;
    document.body.appendChild(p);
    logEl = p.querySelector('#cg-log');

    const $ = (id) => p.querySelector(id);
    $('#cg-courses').value = state.cfg.courses.join('\n');
    $('#cg-startat').value = state.cfg.startAt;
    $('#cg-interval').value = state.cfg.retryInterval;
    $('#cg-turbo').checked = state.cfg.turbo;

    const persist = () => {
      state.cfg.courses = $('#cg-courses').value.split('\n').map(s => s.trim()).filter(Boolean);
      state.cfg.startAt = $('#cg-startat').value.trim();
      state.cfg.retryInterval = Number($('#cg-interval').value) || 400;
      state.cfg.turbo = $('#cg-turbo').checked;
      saveCfg();
    };
    ['#cg-courses', '#cg-startat', '#cg-interval', '#cg-turbo'].forEach(s =>
      $(s).addEventListener('change', persist));

    $('#cg-start').onclick = () => { persist(); log('手动开抢'); start(); };
    $('#cg-stop').onclick = () => { stop(); log('已停止'); };
    $('#cg-export').onclick = () => {
      persist();
      if (!state.headers.Authorization && !state.headers.authorization) {
        log('⚠️ 尚未捕获到 Authorization，请先让页面和服务器通信（刷新列表）');
      }
      const data = {
        exportedAt: new Date().toISOString(),
        baseUrl: location.origin + '/xsxk',
        headers: state.headers,
        listType: state.listType,
        listBody: state.listBody,
        courses: state.cfg.courses,
        startAt: state.cfg.startAt,
        retryInterval: state.cfg.retryInterval,
        courseDb: state.courseDb,
      };
      const blob = new Blob([JSON.stringify(data, null, 2)], { type: 'application/json' });
      const a = document.createElement('a');
      a.href = URL.createObjectURL(blob);
      a.download = 'xsxk-session.json';
      a.click();
      URL.revokeObjectURL(a.href);
      log('已导出会话文件 xsxk-session.json（含 ' + Object.keys(state.courseDb).length + ' 门课参数）');
    };

    // 服务器校时：启动时一次，之后每 20s 校准（开抢倒计时以服务器时间为准）
    syncTime();
    setInterval(syncTime, 20000);

    // 倒计时定时开抢（autoStarted 置位后不再触发，防止"停止"后被复活）
    setInterval(() => {
      if (state.autoStarted || state.running || !state.cfg.startAt) { $('#cg-cd').textContent = ''; return; }
      const t = new Date(state.cfg.startAt.replace(/-/g, '/')).getTime();
      const diff = t - now(); // 用校时后的服务器时间
      if (isNaN(t)) { $('#cg-cd').textContent = '时间格式不正确'; return; }
      if (diff <= 0) { $('#cg-cd').textContent = '⏰ 到点开抢！'; start(); }
      else {
        $('#cg-cd').textContent = `距开抢：${(diff / 1000).toFixed(1)} 秒${state.timeSynced ? '（已校时）' : ''}`;
        // 临近开抢（60s 内）加密校时频率到 5s 一次（参考 xsxk-nuist：最后阶段用最新偏移）
        if (diff < 60000 && diff > 10000 && Date.now() - state.lastSync > 5000) syncTime();
      }
    }, 100);
  }

  if (document.body) buildPanel();
  else document.addEventListener('DOMContentLoaded', buildPanel);
})();
