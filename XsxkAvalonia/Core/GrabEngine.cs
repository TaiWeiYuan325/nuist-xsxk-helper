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
    public override string ToString() => $"{Name}｜{STime} ~ {ETime}";
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
    public readonly BrowserPump Browser;
    public readonly WsListener Ws;

    // ---- 状态 ----
    public string Auth = "";
    public string Batch = "";
    public string Ctype = "";
    public string Campus = "";
    public int IntervalMs = 300;
    public DateTimeOffset? StartAt;
    public bool Volmode;
    public List<BatchInfo> Batches = new();
    public List<(string Code, string Name)> ClassTypes = new();
    public List<JsonObject> Rows = new();
    public List<VolunteerItem> Volunteers = new();
    public double Offset;
    public int Online;
    public string NetText = "";
    public bool Grabbing { get; private set; }
    public bool WsConnected => Ws.Connected;
    public bool BrowserOnline => Browser.Alive;

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
        Browser = new BrowserPump(this);
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

    // ================= 浏览器 =================

    public async Task OpenBrowserAsync()
    {
        await Browser.StartAsync();
        if (Browser.Alive) StartNetLoop();
        MaybeStartWs();
        NotifyState();
    }

    public void CheckToken()
    {
        if (string.IsNullOrEmpty(Auth)) { Log("尚未捕获到 Authorization，请先点「打开内置浏览器」登录。"); return; }
        var sid = Logic.StudentIdFromJwt(Auth);
        var exp = JwtExp(Auth);
        if (exp == 0) { Log($"token 未包含过期时间（学号 {sid}），抓到新的会自动覆盖。"); return; }
        var remain = exp - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (remain > 0)
            Log($"token 有效（学号 {sid}），约 {remain / 3600} 小时 {remain % 3600 / 60} 分后到期。");
        else
            Log($"⚠️ token 已过期 {(-remain) / 60} 分钟（学号 {sid}）！请在内置浏览器里刷新选课页面重新捕获，否则开抢必败。");
    }

    // ================= 头捕获（BrowserPump 回调） =================

    public void OnHeaderCaptured(string key, string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        if (key == "authorization" && value != Auth) { Auth = value; _authChanged = true; }
        if (key == "batchid" && value != Batch) { Batch = value; _batchChanged = true; }
    }

    public void OnHeadersCaptured()
    {
        if (_authChanged)
        {
            _authChanged = false;
            Log("🔑 已自动捕获: Authorization");
            TryAutoRefreshBatch();
        }
        if (_batchChanged)
        {
            _batchChanged = false;
            var old = _navBatch;
            Log($"🔄 批次已自动更新: {Tail(Batch)}（原 {(string.IsNullOrEmpty(old) ? "空" : Tail(old))}）");
            _ = FetchClassTypesAsync();
        }
        NotifyState();
        MaybeStartWs();
    }

    public void OnRowsCaptured(List<JsonObject> rows, string ctype)
    {
        if (Rows.Count == 0 && rows.Count > 0 && (string.IsNullOrEmpty(Ctype) || ctype == Ctype))
        {
            Rows = rows;
            Log($"📡 已从浏览器流量捕获课程列表（{rows.Count} 行）");
            NotifyState();
        }
    }

    // ================= 轮次 =================

    private void TryAutoRefreshBatch()
    {
        if ((DateTime.Now - _lastBatchRefresh).TotalSeconds < 3) return;
        _lastBatchRefresh = DateTime.Now;
        _ = RefreshBatchAsync(false);
    }

    public async Task RefreshBatchAsync(bool manual)
    {
        var client = new XsxkClient(this);
        try
        {
            var text = await client.PostAsync("/elective/user", new Dictionary<string, string> { ["batchId"] = Batch });
            var jo = Logic.ParseJsonText(text);
            var stu = jo["data"]?["student"] as JsonObject;
            if (stu == null) { if (manual) Log("未获取到学生信息（未登录或登录已过期）。"); return; }
            var arr = stu["electiveBatchList"] as JsonArray;
            if (arr == null || arr.Count == 0) { if (manual) Log("当前没有选课轮次。"); return; }
            var list = new List<BatchInfo>();
            foreach (var b in arr)
            {
                if (b is not JsonObject o) continue;
                var wid = Logic.Pick(o, "WID", "wid", "batchId", "id");
                if (wid == "") continue;
                list.Add(new BatchInfo
                {
                    Wid = wid,
                    Name = Logic.Pick(o, "batchName", "name", "batchDesc", "title"),
                    STime = Logic.FmtTime(Logic.Pick(o, "beginTime", "startTime", "sTime", "begin")),
                    ETime = Logic.FmtTime(Logic.Pick(o, "endTime", "eTime", "end")),
                });
            }
            Batches = list;
            Log($"发现 {Batches.Count} 个选课轮次");
            var curWid = stu["currentElectiveBatch"] is JsonObject cb
                ? Logic.Pick(cb, "WID", "wid", "batchId", "id") : "";
            if (!string.IsNullOrEmpty(curWid) && curWid != Batch)
            {
                Batch = curWid;
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
                    var old = Batch; Batch = hit.Wid;
                    Log($"🔄 批次已自动更新: {Tail(hit.Wid)}（原 {(string.IsNullOrEmpty(old) ? "空" : Tail(old))}）");
                }
            }
            if (manual)
            {
                var cur = Batches.FirstOrDefault(b => b.Wid == Batch);
                if (cur != null) Log($"当前轮次: {cur.Name}");
                await FetchClassTypesAsync();
            }
            NotifyState();
        }
        catch (Exception ex) { if (manual) Log($"⚠️ 获取轮次列表失败: {ex.Message}"); }
    }

    public async Task SelectBatchAsync(BatchInfo? b)
    {
        if (b == null || b.Wid == Batch) return;
        Batch = b.Wid;
        Log($"🎯 已选择轮次: {b}");
        var bt = TryParseTime(b.STime);
        if (bt.HasValue && bt.Value.ToUnixTimeSeconds() > Now())
        {
            StartAt = bt.Value;
            Log("已自动填入开抢时间 = 轮次开始时间");
        }
        await FetchClassTypesAsync();
        NotifyState();
    }

    // ================= 类别 =================

    public async Task FetchClassTypesAsync()
    {
        if (string.IsNullOrEmpty(Batch)) return;
        if (_ctBatch == Batch && ClassTypes.Count > 0) return;
        var client = new XsxkClient(this);
        string html = "";
        try
        {
            if (Browser.Alive && Batch != _navBatch)
            {
                html = await client.NavAsync(new Dictionary<string, string> { ["batchId"] = Batch }, 25, default);
                _navBatch = Batch;
                Log($"🔀 服务器轮次已切换（页面导航）: {Tail(Batch)}");
            }
            else
            {
                html = await client.GetAsync("/elective/grablessons",
                    new Dictionary<string, string> { ["batchId"] = Batch });
            }
        }
        catch (Exception ex) { Log($"⚠️ 切换服务器轮次异常: {ex.Message}"); return; }
        var types = Logic.ParseClassTypes(html);
        if (types.Count == 0) types = Logic.ParseClassTypesLoose(html);
        if (types.Count == 0)
        {
            Log($"⚠️ 类别解析失败（页面 {html.Length} 字符）");
            return;
        }
        _ctBatch = Batch;
        ClassTypes = types;
        Log("本批次课程类别: " + string.Join(" / ", ClassTypes.Select(t => $"{t.Name}({t.Code})")));
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
        var client = new XsxkClient(this);
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
            var text = await client.PostJsonAsync("/elective/nuist/clazz/list", body);
            var jo = Logic.ParseJsonText(text);
            var dict = new Dictionary<string, JsonObject>();
            Logic.WalkRows(jo, dict);
            if (dict.Count > 0)
            {
                Rows = dict.Values.ToList();
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
        var should = Browser.Alive && !string.IsNullOrEmpty(Auth) && (Grabbing || Volunteers.Count > 0);
        if (!should || Ws.Connected || Ws.Running) return;
        var sid = Logic.StudentIdFromJwt(Auth);
        if (string.IsNullOrEmpty(sid)) return;
        var url = $"wss://xsxk.nuist.edu.cn/xsxk/websocket/{sid}";
        _ = Task.Run(async () =>
        {
            try
            {
                var cookies = await Browser.CookiesAsync();
                await Ws.StartAsync(url, cookies);
            }
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
                    var client = new XsxkClient(this);
                    var text = await client.PostAsync("/web/now", new Dictionary<string, string>(), 10, ct);
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
        if (string.IsNullOrEmpty(Auth)) { Log("⚠️ 尚未捕获 Authorization，请先打开内置浏览器登录。"); return; }
        if (string.IsNullOrEmpty(Batch)) { Log("⚠️ 尚未捕获 batchId，请先刷新轮次。"); return; }
        var exp = JwtExp(Auth);
        if (exp > 0 && DateTimeOffset.UtcNow.ToUnixTimeSeconds() > exp - 120)
        {
            Log("⚠️ token 已过期或即将过期，请在内置浏览器里刷新选课页面重新捕获后再开抢！");
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
        // 开抢前刷新一次课程列表
        try { await FetchCoursesAsync(); } catch { }
        if (Rows.Count == 0)
            Log("⚠️ 未能获取课程列表，仍按志愿队列中的 secretVal 直接投递。");
        if (ct.IsCancellationRequested) return;
        var client = new XsxkClient(this);
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
    private async Task<string> EnqueueAndWaitAsync(XsxkClient client, VolunteerItem v, CancellationToken ct)
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
        else if (code == "auth") Log("🔒 登录态已失效，请在内置浏览器里刷新选课页面！抢课已暂停。");
        else if (code == "badHtml") Log("🔒 会话异常（服务器返回HTML），请在内置浏览器里刷新选课页面！");
        else Log($"⛔ 「{v.Name}」{msg}");
        return code;
    }

    private async Task NormalLoopAsync(XsxkClient client, CancellationToken ct)
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

    private async Task VolunteerLoopAsync(XsxkClient client, CancellationToken ct)
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
