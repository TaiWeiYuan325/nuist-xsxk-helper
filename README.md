# 南信大选课助手（nuist-xsxk-helper）

南信大（NUIST）选课系统的自动化抢课工具。**当前版本 v3.0（纯 HTTP 会话版）**：Avalonia 单进程应用（C#），无边框窗口 + 终末地工业风 UI（亮黄 `#FEFE2C` × 重黑 `#212121`），鸿蒙黑体已内嵌。**v3.0 起彻底移除浏览器依赖**——登录、切轮次、拉列表、抢课全部走软件内置的 HTTP 会话，体积小、稳定性高、不会被"浏览器被关掉"背刺。支持多备选志愿队列、定时开抢、满员自动切备选、通识课志愿填报，抢课结果以 **WebSocket 服务器推送** 为最终裁决。

> 作者：太微垣，使用 Kimi K3 制作

![界面截图](docs/screenshot-v2.1.png)

## 下载

👉 到 [**Releases**](../../releases) 下载最新压缩包，解压后双击 **南信大选课助手.exe** 即可。

- 运行环境：Windows 10 1809+，64 位
- **零安装**：.NET 8 运行时、鸿蒙黑体全部内置
- 首次启动 Windows 可能提示"未知发布者"，点【仍要运行】
- 登录 token 与课程缓存保存在 exe 旁的 `xsxk_cache.json`（分享压缩包时请删除）

## 功能

- 🔑 **软件内登录**：输入学号/密码，验证码图片直接显示在界面里，手输 4 位即可；密码用校方前端同款 AES-128-ECB 加密提交，**只存内存不落盘**；token 自动缓存，重启免登录
- 🚀 **定时开抢**：按服务器校时倒计时，到点自动发起；开抢瞬间自动把服务器会话切到目标轮次（未放行就每秒重试）
- 📋 **志愿队列**：多门备选，第 1 志愿满员自动切第 2 志愿，可手动调整顺序；**按轮次独立记忆**，切回来自动恢复
- 📡 **WebSocket 结果推送**：`/add` 只是进队列，真正"选课成功/课容量已满"由服务器 WS 推送，收到满员立刻切备选，不做无效等待
- 🏫 **通识选修课（XGKC）**：选 XGKC 类别自动进入志愿模式，自动寻找可用志愿级别
- 📦 **本地缓存**：轮次/类别/课程列表拉成功一次就缓存，轮次未开始或网络拥堵时照样能看课、排志愿、定时开抢——**高峰期网页打不开也照样抢**
- 🔁 **轮次/类别全自动**：选轮次自动切换服务器批次并加载类别，选类别自动加载课程列表
- 📊 **实时状态**：登录 / 凭证 / WS / 任务四指示灯，在线人数、网络延迟、服务器校时一目了然
- 🎨 **工业风界面**：无边框自绘标题栏、悬浮「加入志愿队列」按钮、半透明卡片 + 等高线纹理背景、内嵌鸿蒙黑体

## 使用

1. 双击【南信大选课助手.exe】
2. 左侧【连接】卡片输入**学号、密码、验证码**，点【登录选课系统】（验证码看不清点右侧刷新按钮）
   - 登录成功后表单自动折叠，要重新登录点「已登录，点这里重新登录」即可
3. 选【轮次】→ 选【类别】，课程列表自动加载（轮次未开始会显示本地缓存）
4. 单击选中课程行，点课程列表与志愿队列之间的**黄色悬浮按钮**加入志愿（也可双击课程行直接加入；可加多个备选，↑↓ 调整顺序）
5. 确认开抢时间与重试间隔，点【开始抢课】
6. 看日志：`🎉 选课成功` 才是真的选上

## 原理速览

```
Avalonia 单进程（C#，零浏览器）
 ├─ SessionClient ：进程级 Cookie 罐（≈ requests.Session），登录后所有请求同会话
 │    ├─ POST /auth/captcha       （拉验证码图片 + uuid）
 │    ├─ POST /auth/login         （AES-128-ECB 加密密码 → 拿 token）
 │    ├─ POST /elective/user      （切轮次：body 带 batchId、头不带 batchid）
 │    ├─ GET  grablessons?batchId=（导航风格头取页，解析类别）
 │    └─ POST /elective/clazz/add （进选课队列）
 ├─ WsListener   ：WSS  /xsxk/websocket/{学号} （服务器推送最终裁决）
 ├─ CacheStore   ：xsxk_cache.json（轮次/类别/课程/token 本地缓存）
 └─ 校时循环     ：POST /web/now              （服务器校时 + 在线人数）
```

- 服务器会话由登录响应种下的会话 Cookie 维持（CookieContainer 自动累积回放），这就是 v3.0 不需要浏览器的根本原因
- 切轮次时 `POST /elective/user` 若带 `batchid` **请求头**会被服务器按"头与会话不一致"拒绝——body 带目标 batchId 即可（v2.x 直连失败的具体原因）
- 学号从 token（JWT 的 `login_user_key`）自动解析，无需手填

## 从源码构建

需要 **.NET 8 SDK**：

```bash
cd XsxkAvalonia
dotnet build                       # 开发运行（输出 bin/Debug/net8.0/）

# 单文件自包含发布（免安装 .NET）
dotnet publish -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true
```

v3.0 起无任何浏览器组件，发布体积相比 v2.x 大幅缩小。

## 历史版本

- `v2.2`（[Release](../../releases/tag/v2.2)）— 内置 Chromium 自动捕获版（Playwright），已被 v3.0 取代
- `v2.1` — 工业风 UI 改版
- `v2.0` — Avalonia 重构首版
- `v1.1`（[Release](../../releases/tag/v1.1)）— WinUI3 前端 + Python 后端双进程版，已停止维护
- `course-grab.user.js` — 最初的油猴连点器脚本
- `headless_grabber.py` + `mock_server.py` — 无界面抢课原型与本地测试服务器
- `backups/v1.0-20260730/` — v1.0（tkinter 界面）完整备份

## 致谢

- [YoungUsing/xsxk-nuist](https://github.com/YoungUsing/xsxk-nuist)（纯 HTTP 会话、导航风格头切轮次、OCR 验证码思路）
- [airline233/nuist-xsxk](https://github.com/airline233/nuist-xsxk)（登录/验证码 API、`POST /elective/user` 切轮次、WebSocket 推送机制的启发）

## 免责声明

本工具仅供学习交流使用，请遵守学校选课相关规定，合理控制请求频率，由此产生的一切后果由使用者自行承担。
