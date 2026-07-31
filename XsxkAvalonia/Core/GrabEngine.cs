using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace XsxkAvalonia.Core;

public class BatchInfo
{
    public string Wid = "";
    public string Name = "";
    public string STime = "";
    public string ETime = "";
    public bool CanSelect = true;
    public string NoSelectReason = "";
    public override string ToString() => $"{Name}｜{STime} ~ {ETime}" + (CanSelect ? "" : " ⚠️不可选");
}

public class VolunteerItem
{
    public string Name { get; set; } = "";
    public string Id { get; set; } = "";
    public string Secret { get; set; } = "";
    public override string ToString() => Name;
}

/// <summary>
/// 抢课编排引擎：状态机 + 日志 + 双模式抢课循环 + WS 联动。
/// 语义一对一移植自 Python 版 Core。
/// </summary>
public class GrabEngine
{
    public string Base = "https://xsxk.nuist.edu.cn/xsxk";
    public readonly SessionClient Http;
    public readonly WsListener Ws;
    public readonly CacheStore Cache = new();

    // ---- 状态 ----
    public string Auth = "";
    public string Batch = "";
    public string Ctype = "";
    public string Campus = "";
    public int IntervalMs = 400;
    public DateTimeOffset? StartAt;
    public bool Volmode;
    public List<BatchInfo> Batches = new();
    public List<(string Code, string Name)> ClassTypes = new();
    public List<JsonObject> Rows = new();
    public List<VolunteerItem> Volunteers = new();
    // 每个轮次独立的志愿队列记忆（key=批次 code，空 key=未捕获批次时）
    private readonly Dictionary<string, List<VolunteerItem>> _volQueues = new();
    private string _volBatchKey = "";
    public double Offset;
    public int Online;
    public string NetText = "";
    public bool Grabbing { get; private set; }
    public bool WsConnected => Ws.Connected;
    public bool LoggedIn => !string.IsNullOrEmpty(Auth);

    private CancellationTokenSource? _grabCts;
    private CancellationTokenSource? _netCts;
    private string _navBatch = "";
    private string _ctBatch = "";
    private DateTime _lastBatchRefresh = DateTime.MinValue;
    private DateTime _lastNotOpenLog = DateTime.MinValue;
    private bool _volunteerTipShown;

    // WS 裁决状态
    private string _wsFull = "";
    private string _wsSuccess = "";
    private bool _wsAnySuccess;
    private readonly SemaphoreSlim _wsSignal = new(0, 256);

    private bool _authChanged, _batchChanged;

    public event Action<string>? Logged;
    public event Action? StateChanged;

    public GrabEngine()
    {
        Http = new SessionClient(this);
        Ws = new WsListener();
        Ws.Log += Log;
        Ws.StateChanged += () => StateChanged?.Invoke();
        Ws.Full += id =>
        {
            lock (this) _wsFull = id;
            try { _wsSignal.Release(); } catch { }
        };
        Ws.Success += id =>
        {
            lock (this) { _wsAnySuccess = true; _wsSuccess = id; }
            try { _wsSignal.Release(); } catch { }
        };
        Cache.Load();
        Batches = Cache.LoadBatches();
        Auth = Cache.LoadAuth();
        StartNetLoop();
    }

    /// <summary>UI 事件接线完成后调用，报告缓存恢复情况（构造时日志无人订阅会丢）</summary>
    public void LogCacheRestore()
    {
        if (Batches.Count > 0)
            Log($"📦 已从本地缓存恢复 {Batches.Count} 个轮次（切轮次先显缓存，后台自动同步）");
        if (string.IsNullOrEmpty(Auth)) return;
        var sid = Logic.StudentIdFromJwt(Auth);
        var exp = JwtExp(Auth);
        if (exp == 0)
        {
            Log($"🔑 已从缓存恢复登录凭据（学号 {sid}，有效期未知，开抢前可用「检查 token」验证）");
            return;
        }
        var remain = exp - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Log(remain > 0
            ? $"🔑 已从缓存恢复登录凭据（学号 {sid}，约 {remain / 3600} 小时 {remain % 3600 / 60} 分后到期）"
            : $"⚠️ 缓存的登录凭据已过期（学号 {sid}），请重新登录");
    }

    public void Log(string m) => Logged?.Invoke($"[{DateTime.Now:HH:mm:ss.fff}] {m}");
    public void NotifyState() => StateChanged?.Invoke();

    public double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 + Offset;

    private static string Tail(string s) => s.Length <= 8 ? s : "…" + s[^8..];

    private static DateTimeOffset? TryParseTime(string s)
    {
        try { return Logic.ParseStartTime(s); } catch { return null; }
    }

    public static string FmtRemain(double sec)
    {
        if (sec < 0) sec = 0;
        var ts = TimeSpan.FromSeconds(sec);
        return ts.TotalDays >= 1 ? $"{(int)ts.TotalDays}天 {ts:hh\\:mm\\:ss}" : ts.ToString(@"hh\:mm\:ss");
    }

    private static long JwtExp(string jwt)
    {
        try
        {
            var payload = jwt.Split('.')[1];
            payload = payload.Replace('-', '+').Replace('_', '/');
            payload += new string('=', (4 - payload.Length % 4) % 4);
            var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            return (JsonNode.Parse(json) as JsonObject)?["exp"]?.GetValue<long>() ?? 0;
        }
        catch { return 0; }
    }

    // ================= 登录（纯 HTTP，无需浏览器） =================

    private string _captchaUuid = "";

    /// <summary>拉取验证码图片（uuid 存在引擎里，登录时配对使用）</summary>
    public async Task<byte[]> FetchCaptchaAsync()
    {
        var (img, uuid) = await Http.GetCaptchaAsync(10, default);
        _captchaUuid = uuid;
        return img;
    }

    /// <summary>学号/密码/验证码登录。成功：拿 token、缓存、拉轮次；失败：返回 false（调用方负责刷新验证码）</summary>
    public async Task<bool> LoginAsync(string username, string password, string captcha)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        { Log("⚠️ 请输入学号和密码"); return false; }
        if (string.IsNullOrEmpty(_captchaUuid)) { Log("⚠️ 请先获取验证码"); return false; }
        if (string.IsNullOrWhiteSpace(captcha)) { Log("⚠️ 请输入图片中的验证码"); return false; }
        try
        {
            var data = await Http.LoginAsync(username.Trim(), password, captcha.Trim(), _captchaUuid, 12, default);
            var token = data["token"]?.ToString() ?? "";
            if (token == "")
            {
                var sample = data.ToJsonString();
                Log($"⚠️ 登录响应缺少 token（样本发给作者）: {sample[..Math.Min(200, sample.Length)]}");
                return false;
            }
            Auth = token;
            Cache.SaveAuth(token);
            var name = data["student"]?["XM"]?.ToString();
            Log(string.IsNullOrEmpty(name)
                ? $"🎉 登录成功（学号 {Logic.StudentIdFromJwt(token)}）"
                : $"🎉 登录成功！欢迎你，{name} 同学");
            NotifyState();
            MaybeStartWs();
            // airline233：登录后服务端轮次状态重置，需要重新引导 + 拉取轮次
            await RefreshBatchAsync(true);
            return true;
        }
        catch (Exception ex)
        {
            Log($"❌ 登录失败: {ex.Message}");
            return false;
        }
    }

    public void CheckToken()
    {
        if (string.IsNullOrEmpty(Auth)) { Log("尚未登录，请在左侧输入学号密码登录。"); return; }
        var sid = Logic.StudentIdFromJwt(Auth);
        var exp = JwtExp(Auth);
        if (exp == 0) { Log($"token 未包含过期时间（学号 {sid}）。"); return; }
        var remain = exp - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (remain > 0)
            Log($"token 有效（学号 {sid}），约 {remain / 3600} 小时 {remain % 3600 / 60} 分后到期。");
        else
            Log($"⚠️ token 已过期 {(-remain) / 60} 分钟（学号 {sid}）！请重新登录，否则开抢必败。");
    }

    // ================= 轮次 =================

    /// <summary>切换批次：保存当前轮次的志愿队列，恢复目标轮次的队列（每轮次独立记忆）</summary>
    private void SwitchBatchTo(string newWid)
    {
        if (string.IsNullOrEmpty(newWid) || newWid == Batch) return;
        var had = Volunteers.Count;
        if (had > 0 || _volQueues.ContainsKey(_volBatchKey))
            _volQueues[_volBatchKey] = Volunteers.ToList();
        Batch = newWid;
        Volunteers = _volQueues.TryGetValue(newWid, out var q) ? q.ToList() : new List<VolunteerItem>();
        _volBatchKey = newWid;
        if (had > 0) Log($"💾 上一轮次的 {had} 门志愿已保存（切回该轮次自动恢复）");
        if (Volunteers.Count > 0)
            Log($"♻️ 已恢复本轮次志愿队列（{Volunteers.Count} 门；若开抢报错请移除后重新添加）");
        NotifyState();
    }

    public async Task RefreshBatchAsync(bool manual)
    {
        if (string.IsNullOrEmpty(Auth)) { if (manual) Log("尚未登录，请先登录。"); return; }
        // 空批次：先 GET 个人主页解析 var batch.code 引导当前批次（airline233 同款：
        // "平台系统bug，需要先访问首页才能正常获取用户信息"），再重放切换确立会话
        if (string.IsNullOrEmpty(Batch))
        {
            try
            {
                var html = await Http.GetPageAsync("/profile/index.html", null, 15, default);
                var seed = Logic.ParseProfileBatchId(html);
                if (!string.IsNullOrEmpty(seed))
                {
                    SwitchBatchTo(seed);
                    _navBatch = seed;
                    Log($"🌱 已从个人主页引导批次: {Tail(seed)}");
                }
            }
            catch (Exception e) { Log($"⚠️ 批次引导失败: {e.Message}"); }
            if (string.IsNullOrEmpty(Batch))
            {
                if (manual) Log("尚未捕获批次：个人主页未返回批次信息。");
                return;
            }
        }
        try
        {
            // 拉轮次列表（头不带 batchid，与 airline233 get_user_info 一致，避免头/会话不一致被拒）
            var text = await Http.PostAsync("/elective/user", new Dictionary<string, string> { ["batchId"] = Batch }, 15, default, withBatch: false);
            var jo = Logic.ParseJsonText(text);
            var stu = jo["data"]?["student"] as JsonObject;
            if (stu == null) { if (manual) Log("未获取到学生信息（未登录或登录已过期）。"); return; }
            var arr = stu["electiveBatchList"] as JsonArray;
            if (arr == null || arr.Count == 0) { if (manual) Log("当前没有选课轮次。"); return; }
            var list = new List<BatchInfo>();
            foreach (var b in arr)
            {
                if (b is not JsonObject o) continue;
                // 轮次 ID 字段是 code（与 Python 版一致）；兼容旧的 WID/wid/batchId/id
                var wid = Logic.Pick(o, "code", "WID", "wid", "batchId", "id");
                if (wid == "") continue;
                var canSel = Logic.Pick(o, "canSelect") != "0";
                list.Add(new BatchInfo
                {
                    Wid = wid,
                    Name = Logic.Pick(o, "name", "batchName", "batchDesc", "title"),
                    STime = Logic.FmtTime(Logic.Pick(o, "beginTime", "startTime", "sTime", "begin")),
                    ETime = Logic.FmtTime(Logic.Pick(o, "endTime", "eTime", "end")),
                    CanSelect = canSel,
                    NoSelectReason = Logic.Pick(o, "noSelectReason"),
                });
            }
            if (list.Count == 0 && arr.Count > 0)
            {
                var sample = arr[0]?.ToJsonString() ?? "null";
                Log($"⚠️ 轮次响应 {arr.Count} 项但无一识别（首项样本，发给作者）: {sample[..Math.Min(300, sample.Length)]}");
            }
            Batches = list;
            Cache.SaveBatches(list);
            Log($"发现 {Batches.Count} 个选课轮次");
            var curWid = stu["currentElectiveBatch"] is JsonObject cb
                ? Logic.Pick(cb, "code", "WID", "wid", "batchId", "id") : "";
            if (!string.IsNullOrEmpty(curWid) && curWid != Batch)
            {
                SwitchBatchTo(curWid);
                Log($"🔄 批次已自动更新: {Tail(curWid)}（原 {Tail(_navBatch)}）");
            }
            else if (Batches.Count > 0 && !Batches.Any(b => b.Wid == Batch))
            {
                var now = Now();
                BatchInfo? hit = null;
                foreach (var b in Batches)
                {
                    var bt = TryParseTime(b.STime); var et = TryParseTime(b.ETime);
                    if (bt.HasValue && et.HasValue && now >= bt.Value.ToUnixTimeSeconds() && now <= et.Value.ToUnixTimeSeconds()) { hit = b; break; }
                }
                hit ??= Batches.Where(b => { var bt = TryParseTime(b.STime); return bt.HasValue && bt.Value.ToUnixTimeSeconds() >= now; })
                               .OrderBy(b => TryParseTime(b.STime)!.Value.ToUnixTimeSeconds()).FirstOrDefault();
                if (hit != null)
                {
                    var old = Batch; SwitchBatchTo(hit.Wid);
                    Log($"🔄 批次已自动更新: {Tail(hit.Wid)}（原 {(string.IsNullOrEmpty(old) ? "空" : Tail(old))}）");
                }
            }
            if (manual)
            {
                var cur = Batches.FirstOrDefault(b => b.Wid == Batch);
                if (cur != null) Log($"当前轮次: {cur.Name}");
            }
            // 类别缓存随批次变化失效（FetchClassTypesAsync 内部有节流，重复调用是安全的）
            await FetchClassTypesAsync();
            NotifyState();
        }
        catch (Exception ex) { if (manual) Log($"⚠️ 获取轮次列表失败: {ex.Message}"); }
    }

    public async Task SelectBatchAsync(BatchInfo? b)
    {
        if (b == null || b.Wid == Batch) return;
        SwitchBatchTo(b.Wid);
        Log($"🎯 已选择轮次: {b}");
        if (!b.CanSelect)
            Log($"⚠️ 该轮次当前不可选: {(string.IsNullOrEmpty(b.NoSelectReason) ? "未到开始时间" : b.NoSelectReason)}");
        var bt = TryParseTime(b.STime);
        if (bt.HasValue && bt.Value.ToUnixTimeSeconds() > Now())
        {
            StartAt = bt.Value;
            Log("已自动填入开抢时间 = 轮次开始时间");
        }
        // 先显示本地缓存（轮次未开始/页面拉不动时照样能看课排志愿），再后台同步真实数据
        var cachedTypes = Cache.LoadClassTypes(b.Wid);
        if (cachedTypes.Count > 0)
        {
            ClassTypes = cachedTypes;
            _ctBatch = b.Wid;
            if (!ClassTypes.Any(t => t.Code == Ctype))
            {
                Ctype = ClassTypes[0].Code;
                Volmode = Ctype == "XGKC";
            }
            Rows = Cache.LoadCourses(b.Wid, Ctype);
            Log($"📦 已从本地缓存载入「{b.Name}」: {ClassTypes.Count} 个类别 / {Rows.Count} 门课程");
            NotifyState();
        }
        await FetchClassTypesAsync(force: true);
        NotifyState();
    }

    // ================= 类别 =================

    public async Task FetchClassTypesAsync(bool force = false)
    {
        if (string.IsNullOrEmpty(Batch)) return;
        if (!force && _ctBatch == Batch && ClassTypes.Count > 0) return;
        string html = "";
        try
        {
            if (Batch != _navBatch)
            {
                // 先 POST 切轮次（airline233 同款，轻量），再取 grablessons 页解析类别
                await Http.SwitchBatchAsync(Batch, 10, default);
                html = await Http.NavAsync(new Dictionary<string, string> { ["batchId"] = Batch }, 25, default);
                _navBatch = Batch;
                Log($"🔀 服务器轮次已切换: {Tail(Batch)}");
            }
            else
            {
                html = await Http.GetPageAsync("/elective/grablessons",
                    new Dictionary<string, string> { ["batchId"] = Batch }, 25, default);
            }
        }
        catch (Exception ex) { Log($"⚠️ 切换服务器轮次异常: {ex.Message}"); return; }
        var types = Logic.ParseClassTypes(html);
        if (types.Count == 0) types = Logic.ParseClassTypesLoose(html);
        if (types.Count == 0)
        {
            var title = Logic.HtmlTitle(html);
            var hint = ClassTypes.Count > 0 ? "，已保留本地缓存显示" : "";
            var reason = title == "学生选课"
                ? "服务器未提供该轮次选课页（轮次未开始，或登录态已过期需重新登录）"
                : "该轮次可能未开始，服务器不提供选课页";
            Log($"⚠️ 类别解析失败（页面 {html.Length} 字符{(title != "" ? $"，标题「{title}」" : "")}）{hint}——{reason}");
            return;
        }
        _ctBatch = Batch;
        ClassTypes = types;
        Cache.SaveClassTypes(Batch, types);
        Rows = new();   // 批次已变，旧轮次的课程列表作废
        Log("本批次课程类别: " + string.Join(" / ", ClassTypes.Select(t => $"{t.Name}({t.Code})")));
        // 与 Python 版 _load_types 一致：当前类别不在本批次列表中时自动切到第一个类别
        // （SelectClassType 内部会自动拉取课程列表，实现"切轮次→类别→课程"一条龙）
        if (!ClassTypes.Any(t => t.Code == Ctype))
        {
            Log($"ℹ️ 类别自动切换: {(string.IsNullOrEmpty(Ctype) ? "(未选择)" : Ctype)} → {ClassTypes[0].Code}");
            SelectClassType(ClassTypes[0].Code);
            return;
        }
        // 类别没变但批次变了：也要重拉课程列表（旧轮次数据已失效）
        if (!string.IsNullOrEmpty(Ctype)) _ = FetchCoursesAsync();
        NotifyState();
    }

    public void SelectClassType(string code)
    {
        if (string.IsNullOrEmpty(code)) return;
        var vm = code == "XGKC";
        if (code == Ctype && vm == Volmode) return;
        Ctype = code;
        Volmode = vm;
        Log(vm ? "志愿模式: 开（XGKC 通识选修课，自动触发）" : $"志愿模式: 关（{code} 直接抢占）");
        _ = FetchCoursesAsync();
        MaybeStartWs();
        NotifyState();
    }

    // ================= 课程列表 =================

    public async Task FetchCoursesAsync()
    {
        if (string.IsNullOrEmpty(Ctype)) { Log("请先选择类别。"); return; }
        Log($"拉取课程列表（{Ctype}）…");
        try
        {
            var body = new JsonObject
            {
                ["teachingClassType"] = Ctype,
                ["pageNumber"] = 1,
                ["pageSize"] = 200,
                ["orderBy"] = "",
                ["campus"] = Campus,
            };
            if (Ctype != "ALLKC") body["SFYX"] = "2";
            var text = await Http.PostJsonAsync("/elective/nuist/clazz/list", body);
            var jo = Logic.ParseJsonText(text);
            var dict = new Dictionary<string, JsonObject>();
            Logic.WalkRows(jo, dict);
            if (dict.Count > 0)
            {
                Rows = dict.Values.ToList();
                Cache.SaveCourses(Batch, Ctype, Rows);
                Log($"获取到 {Rows.Count} 门课程");
            }
            else
            {
                var code = jo["code"]?.ToString() ?? "?";
                var msg = jo["msg"]?.ToString() ?? "";
                if (code == "500") Log($"❌ 获取课程列表失败: 服务器 {code} {msg}（该轮次可能未开始/已结束，或类别不对）");
                else Log($"❌ 获取课程列表失败: 响应中未找到课程行（code={code} {msg}）");
            }
        }
        catch (Exception ex) { Log($"❌ 获取课程列表失败: {ex.Message}"); }
        NotifyState();
    }

    // ================= 志愿队列 =================

    public void AddVolunteer(string name, string id, string secret)
    {
        if (Volunteers.Any(v => v.Id == id)) { Log($"ℹ️ 「{name}」已在志愿队列中"); return; }
        Volunteers.Add(new VolunteerItem { Name = name, Id = id, Secret = secret });
        Log($"➕ 已加入志愿: 「{name}」");
        if (Volmode && !_volunteerTipShown)
        {
            _volunteerTipShown = true;
            Log("ℹ️ 志愿模式：按列表顺序提交，成功一门自动提交下一门，可随时增减。");
        }
        MaybeStartWs();
        NotifyState();
    }

    public void RemoveVolunteer(VolunteerItem? v)
    {
        if (v == null) return;
        Volunteers.Remove(v);
        Log($"➖ 已移除志愿: 「{v.Name}」");
        NotifyState();
    }

    public void ClearVolunteers()
    {
        Volunteers.Clear();
        Log("🗑️ 已清空志愿队列");
        NotifyState();
    }

    public void MoveVolunteer(VolunteerItem? v, int dir)
    {
        if (v == null) return;
        var i = Volunteers.IndexOf(v);
        var j = i + dir;
        if (i < 0 || j < 0 || j >= Volunteers.Count) return;
        (Volunteers[i], Volunteers[j]) = (Volunteers[j], Volunteers[i]);
        NotifyState();
    }

    // ================= WS =================

    public void MaybeStartWs()
    {
        // 会话 Cookie 罐 + Authorization 直接握手（airline233：登录后同会话连校方 WS）
        var should = !string.IsNullOrEmpty(Auth) && (Grabbing || Volunteers.Count > 0);
        if (!should || Ws.Connected || Ws.Running) return;
        var sid = Logic.StudentIdFromJwt(Auth);
        if (string.IsNullOrEmpty(sid)) return;
        var url = $"wss://xsxk.nuist.edu.cn/xsxk/websocket/{sid}";
        var cookie = Http.CookieHeader();
        _ = Task.Run(async () =>
        {
            try { await Ws.StartAsync(url, cookie); }
            catch { }
        });
    }

    private bool WsFullFor(string id)
    {
        lock (this) return _wsFull != "" && (id == "" || _wsFull == id);
    }

    private bool WsSuccessFor(string id)
    {
        lock (this) return _wsAnySuccess && (_wsSuccess == "" || id == "" || _wsSuccess == id);
    }

    private async Task<bool> WsWaitSuccessAsync(string id, double timeoutSec, CancellationToken ct)
    {
        var deadline = Now() + timeoutSec;
        while (Now() < deadline && !ct.IsCancellationRequested)
        {
            if (WsSuccessFor(id)) return true;
            if (WsFullFor(id)) return false;
            try { await _wsSignal.WaitAsync(TimeSpan.FromSeconds(1), ct); }
            catch (OperationCanceledException) { return false; }
            catch { }
        }
        return WsSuccessFor(id);
    }

    // ================= 校时循环 =================

    public void StartNetLoop()
    {
        if (_netCts != null) return;
        _netCts = new CancellationTokenSource();
        var ct = _netCts.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var t0 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
                    var text = await Http.PostAsync("/web/now", new Dictionary<string, string>(), 10, ct, withBatch: false);
                    var jo = Logic.ParseJsonText(text);
                    var cur = jo["data"]?["currentTime"];
                    var online = jo["data"]?["onlineCount"];
                    if (cur != null && long.TryParse(cur.ToString(), out var ctMs) && ctMs > 0)
                    {
                        var t1 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
                        var server = ctMs / 1000.0 + (t1 - t0) / 2;
                        Offset = server - t1;
                        Online = online != null && int.TryParse(online.ToString(), out var oc) ? oc : 0;
                        var rtt = (int)((t1 - t0) * 1000);
                        NetText = $"🌐 服务器 延迟 {rtt} ms ｜ 在线 {Online} 人 ｜ 偏差 {(Offset >= 0 ? "+" : "")}{Offset:0.000} s";
                        NotifyState();
                    }
                }
                catch { }
                try { await Task.Delay(3000, ct); } catch { }
            }
        }, ct);
    }

    // ================= 抢课 =================

    public void StartGrab()
    {
        if (Grabbing) return;
        if (Volunteers.Count == 0) { Log("⚠️ 志愿队列为空，请先在课程列表中双击课程加入志愿。"); return; }
        if (string.IsNullOrEmpty(Auth)) { Log("⚠️ 尚未登录，请先在左侧登录。"); return; }
        if (string.IsNullOrEmpty(Batch)) { Log("⚠️ 尚未捕获 batchId，请先刷新轮次。"); return; }
        var exp = JwtExp(Auth);
        if (exp > 0 && DateTimeOffset.UtcNow.ToUnixTimeSeconds() > exp - 120)
        {
            Log("⚠️ token 已过期或即将过期，请重新登录后再开抢！");
            return;
        }
        lock (this) { _wsFull = ""; _wsSuccess = ""; _wsAnySuccess = false; }
        Grabbing = true;
        _grabCts = new CancellationTokenSource();
        Log("🚀 抢课任务已启动");
        MaybeStartWs();
        NotifyState();
        _ = Task.Run(() => GrabMainAsync(_grabCts.Token));
    }

    public void StopGrab()
    {
        if (!Grabbing) return;
        _grabCts?.Cancel();
        Grabbing = false;
        Log("⏹ 已停止抢课。");
        NotifyState();
    }

    private static Task DelayMs(int ms, CancellationToken ct)
        => Task.Delay(ms, ct).ContinueWith(_ => { }, TaskContinuationOptions.None);

    private async Task GrabMainAsync(CancellationToken ct)
    {
        // 等待开抢时间
        if (StartAt.HasValue)
        {
            var ts = StartAt.Value.ToUnixTimeSeconds();
            var lastLog = 0.0;
            while (Now() < ts && !ct.IsCancellationRequested)
            {
                var remain = ts - Now();
                if (Now() - lastLog >= 1)
                {
                    lastLog = Now();
                    Log($"⏳ 距开抢 {FmtRemain(remain)}（校时偏移 {(Offset >= 0 ? "+" : "")}{Offset:0.000}s，间隔 {IntervalMs}ms，{Volunteers.Count} 个志愿）");
                }
                await DelayMs(50, ct);
            }
        }
        if (ct.IsCancellationRequested) return;
        var client = Http;
        // 开抢窗口内先把服务器会话切到目标轮次（airline233：POST /elective/user，
        // 头不带 batchid）。轮次刚开始的几秒内服务器可能还没放行，重试数次；
        // 失败也继续投递（add 请求自带 batchId）
        for (var i = 0; !ct.IsCancellationRequested; i++)
        {
            if (await client.SwitchBatchAsync(Batch, 10, ct))
            {
                _navBatch = Batch;
                Log($"🔀 服务器轮次已确认: {Tail(Batch)}");
                break;
            }
            if (ct.IsCancellationRequested) return;
            if (i == 0) Log("⏳ 正在切换服务器轮次…");
            if (i >= 9)
            {
                Log("⚠️ 轮次切换未获服务器确认，仍继续投递（add 请求自带 batchId）");
                break;
            }
            await DelayMs(800, ct);
        }
        // 开抢前刷新一次课程列表
        try { await FetchCoursesAsync(); } catch { }
        if (Rows.Count == 0)
            Log("⚠️ 未能获取课程列表，仍按志愿队列中的 secretVal 直接投递。");
        if (ct.IsCancellationRequested) return;
        if (Volmode) await VolunteerLoopAsync(client, ct);
        else await NormalLoopAsync(client, ct);
        if (!ct.IsCancellationRequested)
        {
            Grabbing = false;
            NotifyState();
        }
    }

    private (string Code, string Msg) JudgeAdd(string text)
    {
        var (type, msg) = Logic.Judge(text);
        return (type, msg);
    }

    /// <summary>普通模式：投递一门课并等待 WS 裁决（最多 8 秒）。</summary>
    private async Task<string> EnqueueAndWaitAsync(SessionClient client, VolunteerItem v, CancellationToken ct)
    {
        var body = new Dictionary<string, string>
        {
            ["clazzType"] = Ctype,
            ["clazzId"] = v.Id,
            ["secretVal"] = v.Secret,
            ["batchId"] = Batch,
            ["needBook"] = "",
        };
        string text;
        try { text = await client.PostAsync("/elective/clazz/add", body, 15, ct); }
        catch (Exception ex) { Log($"⛔ 「{v.Name}」请求异常: {ex.Message}"); return "fail"; }
        var (code, msg) = JudgeAdd(text);
        if (code == "success")
        {
            Log($"📨 「{v.Name}」已入队（{msg}），等待服务器裁决…");
            if (Ws.Connected)
            {
                if (await WsWaitSuccessAsync(v.Id, 8, ct)) return "success";
                if (WsFullFor(v.Id)) return "full";
                Log($"⏳ 「{v.Name}」8 秒内未收到 WS 裁决，继续投递");
                return "wait";
            }
            return "success"; // 无 WS 时按成功处理
        }
        if (code == "notOpen")
        {
            if ((DateTime.Now - _lastNotOpenLog).TotalSeconds >= 10)
            {
                _lastNotOpenLog = DateTime.Now;
                Log($"⏳ 「{v.Name}」{msg}");
            }
            return "notOpen";
        }
        if (code == "full") Log($"🈵 「{v.Name}」已满（{msg}），切换下一志愿");
        else if (code == "impossible") Log($"⛔ 「{v.Name}」{msg}，切换下一志愿");
        else if (code == "auth") Log("🔒 登录态已失效，请重新登录！抢课已暂停。");
        else if (code == "badHtml") Log("🔒 会话异常（服务器返回HTML），请重新登录！");
        else Log($"⛔ 「{v.Name}」{msg}");
        return code;
    }

    private async Task NormalLoopAsync(SessionClient client, CancellationToken ct)
    {
        Log("🏁 开抢！（普通模式：成功即停）");
        var idx = 0;
        var skipCount = 0;
        while (!ct.IsCancellationRequested && Volunteers.Count > 0)
        {
            var v = Volunteers[idx % Volunteers.Count];
            if (WsFullFor(v.Id))
            {
                Log($"⏭ 「{v.Name}」WS 通报已满，跳过");
                Volunteers.Remove(v);
                NotifyState();
                if (++skipCount >= Volunteers.Count + 1 && Volunteers.Count == 0)
                {
                    Log("⚠️ 所有志愿均满员，任务结束。");
                    return;
                }
                continue;
            }
            var r = await EnqueueAndWaitAsync(client, v, ct);
            switch (r)
            {
                case "success":
                    Log($"🎉🎉🎉 选课成功：「{v.Name}」！请尽快到选课系统确认。");
                    return;
                case "full":
                case "impossible":
                    Volunteers.Remove(v);
                    NotifyState();
                    if (Volunteers.Count == 0) { Log("⚠️ 所有志愿均不可选，任务结束。"); return; }
                    break;
                case "auth":
                case "badHtml":
                    StopGrab();
                    return;
                case "fail":
                    idx++;
                    if (idx >= Volunteers.Count * 3) { Log("⚠️ 连续失败次数过多，任务结束。请检查网络后重试。"); return; }
                    break;
                default: // wait / notOpen
                    await DelayMs(500, ct);
                    break;
            }
            if (IntervalMs > 0) await DelayMs(IntervalMs, ct);
        }
    }

    private async Task VolunteerLoopAsync(SessionClient client, CancellationToken ct)
    {
        Log("🏁 开抢！（志愿模式：按志愿顺序提交，成功一门继续下一门）");
        var notOpenSince = new Dictionary<string, double>();
        while (!ct.IsCancellationRequested)
        {
            if (Volunteers.Count == 0)
            {
                Log("🎉 所有志愿均已提交完毕，任务结束。请到选课系统确认最终结果。");
                return;
            }
            if (Volunteers.All(v => WsFullFor(v.Id)))
            {
                Log("⚠️ 所有志愿均被 WS 通报满员，任务结束。");
                return;
            }
            var v = Volunteers.First(v => !WsFullFor(v.Id));
            var attempts = 0;
            var submitted = false;
            var removeIt = false;
            while (attempts < 3 && !ct.IsCancellationRequested)
            {
                if (WsFullFor(v.Id)) { removeIt = true; break; }
                // 查志愿级别
                int grade = 1;
                try
                {
                    var gtext = await client.PostAsync("/volunteer/list/choose",
                        new Dictionary<string, string> { ["clazzType"] = "XGKC", ["clazzId"] = v.Id }, 15, ct);
                    grade = Logic.PickFreeGrade(gtext) ?? 1;
                }
                catch { }
                var body = new Dictionary<string, string>
                {
                    ["clazzType"] = "XGKC",
                    ["clazzId"] = v.Id,
                    ["secretVal"] = v.Secret,
                    ["batchId"] = Batch,
                    ["needBook"] = "",
                    ["chooseVolunteer"] = grade.ToString(),
                };
                string text;
                try { text = await client.PostAsync("/elective/clazz/add", body, 15, ct); }
                catch (Exception ex) { Log($"⛔ 「{v.Name}」请求异常: {ex.Message}"); attempts++; continue; }
                var (code, msg) = JudgeAdd(text);
                if (code == "success")
                {
                    submitted = true;
                    Log($"📨 「{v.Name}」已提交第 {grade} 志愿（{msg}），等待服务器裁决…");
                    break;
                }
                if (code == "notOpen")
                {
                    if (!notOpenSince.ContainsKey(v.Id)) notOpenSince[v.Id] = Now();
                    if (Now() - notOpenSince[v.Id] > 30)
                    {
                        Log($"⏳ 「{v.Name}」持续未开放，挂起转下一志愿");
                        break;
                    }
                    if ((DateTime.Now - _lastNotOpenLog).TotalSeconds >= 10)
                    {
                        _lastNotOpenLog = DateTime.Now;
                        Log($"⏳ 「{v.Name}」{msg}（等待开放，已挂起 {Now() - notOpenSince[v.Id]:0} 秒）");
                    }
                    await DelayMs(1000, ct);
                    continue;
                }
                notOpenSince.Remove(v.Id);
                if (code == "full" || code == "impossible")
                {
                    Log($"⛔ 「{v.Name}」{msg}，从队列移除");
                    removeIt = true;
                    break;
                }
                if (code == "auth" || code == "badHtml")
                {
                    Log("🔒 登录态已失效，请在内置浏览器里刷新选课页面！抢课已暂停。");
                    StopGrab();
                    return;
                }
                Log($"⛔ 「{v.Name}」提交失败({attempts + 1}/3)：{msg}");
                attempts++;
                await DelayMs(IntervalMs, ct);
            }
            if (ct.IsCancellationRequested) return;
            if (removeIt)
            {
                var cur = Volunteers.FirstOrDefault(x => x.Id == v.Id);
                if (cur != null) Volunteers.Remove(cur);
                NotifyState();
                continue;
            }
            if (submitted)
            {
                var ok = Ws.Connected && await WsWaitSuccessAsync(v.Id, 5, ct);
                var cur = Volunteers.FirstOrDefault(x => x.Id == v.Id);
                if (cur != null) Volunteers.Remove(cur);
                if (ok)
                    Log($"🎉🎉🎉 「{v.Name}」选课成功（WS 确认）！继续下一志愿。");
                else
                    Log($"📨 「{v.Name}」已提交为志愿（入队待确认），继续下一志愿。");
                NotifyState();
            }
            else
            {
                // 未提交也未移除（notOpen 挂起或三次失败）：移到队尾避免卡死
                var cur = Volunteers.FirstOrDefault(x => x.Id == v.Id);
                if (cur != null)
                {
                    Volunteers.Remove(cur);
                    Volunteers.Add(cur);
                }
                NotifyState();
            }
        }
    }
}
