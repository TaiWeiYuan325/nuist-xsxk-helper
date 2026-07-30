# 南信大选课助手（nuist-xsxk-helper）

南信大（NUIST）选课系统的自动化抢课工具。WinUI 3 图形界面 + Python 无界面后端，支持多备选志愿队列、定时开抢、满员自动切备选、通识课志愿填报，抢课结果以 **WebSocket 服务器推送** 为最终裁决。

> 作者：太微垣，使用 Kimi K3 制作

## 下载

👉 到 [**Releases**](../../releases) 下载 `南信大选课助手-vX.X.zip`，解压后双击 **南信大选课助手** 快捷方式即可。

- 运行环境：Windows 10 1809+，64 位
- **零安装**：.NET 8、Python、Chromium 全部内置
- 首次启动 Windows 可能提示"未知发布者"，点【仍要运行】

## 功能

- 🚀 **定时开抢**：按服务器校时倒计时，到点自动发起
- 📋 **志愿队列**：多门备选，第 1 志愿满员自动切第 2 志愿，可手动调整顺序
- 📡 **WebSocket 结果推送**：`/add` 只是进队列，真正"选课成功/课容量已满"由服务器 WS 推送，收到满员立刻切备选，不做无效等待
- 🏫 **通识选修课（XGKC）**：自动开启志愿模式，从第 1 志愿起自动寻找可用志愿级别
- 🌐 **内置便携 Chromium**：登录态自动捕获与持久化，抢课请求由后端直连服务器 API——**高峰期网页打不开也照样抢**
- 📊 **实时状态**：在线人数、网络延迟、服务器校时一目了然
- 🔁 **轮次/类别自动切换**：选轮次自动切换服务器批次并加载类别，选类别自动加载课程列表

## 使用

1. 双击【南信大选课助手】（后端自动后台启动）
2. 点【🌐 内置浏览器登录】，在弹出的浏览器里登录选课系统，选择完轮次后一定要点一下“选课”
   - 然后就可以返回软件进行操作了
   - ⚠️ 抢课期间请勿关闭该浏览器窗口
4. 状态点变绿后，选【轮次】→ 选【类别】，课程列表自动加载
5. **双击**课程行加入右侧志愿队列（可加多个备选）
6. 确认开抢时间与重试间隔，点【🚀 开始抢课】
7. 看日志：`🎉 选课成功` 才是真的选上

## 原理速览

```
WinUI3 界面 ──localhost HTTP──> Python 后端 ──> 内置 Chromium（登录态/会话）
                                     │
                                     ├─ POST /elective/clazz/add   （进选课队列）
                                     ├─ WSS  /xsxk/websocket/{学号} （服务器推送最终裁决）
                                     └─ GET  /web/now              （服务器校时）
```

- 切换轮次必须通过浏览器**顶层页面导航**（`grablessons?batchId=…`），普通 XHR 会被服务器踢回登录页
- 学号从 JWT 的 `login_user_key` 字段自动解析，无需手填

## 从源码构建

**后端**（Python 3.12+）：

```bash
pip install playwright websocket-client pyinstaller
playwright install chromium
python xsxk_backend.py                 # 开发运行（端口 18765）

# 打包单文件 exe
pyinstaller --noconfirm --clean -F -n xsxk_backend --collect-all playwright xsxk_backend.py
```

**前端**（.NET 8 SDK + Windows App SDK 1.8）：

```bash
cd XsxkWinUI
dotnet build -c Debug -p:Platform=x64  # 开发运行

# 自包含发布（免安装 .NET）
dotnet publish -c Release -r win-x64 -p:Platform=x64 \
  --self-contained true -p:WindowsAppSDKSelfContained=true -p:EnableMsixTooling=true
```

## 历史版本

- `course-grab.user.js` — 最初的油猴连点器脚本
- `headless_grabber.py` + `mock_server.py` — 无界面抢课原型与本地测试服务器
- `backups/v1.0-20260730/` — v1.0（tkinter 界面）完整备份

## 致谢

- [YoungUsing/xsxk-nuist](https://github.com/YoungUsing/xsxk-nuist)
- [airline233/nuist-xsxk](https://github.com/airline233/nuist-xsxk)（WebSocket 推送机制的启发）

## 免责声明

本工具仅供学习交流使用，请遵守学校选课相关规定，合理控制请求频率，由此产生的一切后果由使用者自行承担。
