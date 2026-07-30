using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Playwright;

namespace XsxkAvalonia.Core;

/// <summary>
/// 内置浏览器（Playwright 持久化上下文）：登录态捕获、API 页面代理、顶层导航。
/// 对应 Python 版 _browser_thread + _pw_fetch/_pw_nav。
/// </summary>
public sealed class BrowserPump : IDisposable
{
    private readonly GrabEngine _engine;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IPlaywright? _pw;
    private IBrowserContext? _ctx;
    private IPage? _page;

    public bool Alive { get; private set; }
    public string UserAgent { get; private set; } = "";
    public bool DebugCapture { get; set; }

    public BrowserPump(GrabEngine engine) => _engine = engine;

    public async Task StartAsync()
    {
        if (Alive) { _engine.Log("内置浏览器已在运行"); return; }
        var profile = Path.Combine(AppContext.BaseDirectory, ".browser-profile");
        _pw = await Playwright.CreateAsync();
        _ctx = await _pw.Chromium.LaunchPersistentContextAsync(profile, new()
        {
            Headless = false,
            ViewportSize = new ViewportSize { Width = 1024, Height = 768 },
            Args = new[] { "--window-size=1024,768" },
        });
        _ctx.Request += (_, req) => OnRequest(req);
        _ctx.Response += (_, resp) => OnResponse(resp);
        var pages = _ctx.Pages;
        _page = pages.Count > 0 ? pages[0] : await _ctx.NewPageAsync();
        if (string.IsNullOrEmpty(_page.Url) || _page.Url == "about:blank")
        {
            var url = _engine.Base.TrimEnd('/') + "/profile/index.html";
            try { await _page.GotoAsync(url, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 }); }
            catch (Exception e) { _engine.Log($"⚠️ 打开登录页失败: {e.Message[..Math.Min(80, e.Message.Length)]}"); }
        }
        try { UserAgent = await _page.EvaluateAsync<string>("navigator.userAgent") ?? ""; }
        catch { UserAgent = ""; }
        Alive = true;
        _engine.Log("内置 Chromium 已打开，请登录选课系统（抢课期间请勿关闭此浏览器窗口）");
        _engine.NotifyState();
        // 浏览器被手动关闭时更新状态
        _ = Task.Run(async () =>
        {
            while (Alive)
            {
                await Task.Delay(1000);
                try
                {
                    if (_ctx is null || _ctx.Pages.Count == 0) break;
                }
                catch { break; }
            }
            Alive = false;
            _engine.Log("内置浏览器已关闭");
            _engine.NotifyState();
        });
    }

    private void OnRequest(IRequest req)
    {
        try
        {
            var u = req.Url;
            if (!u.Contains("/elective/") && !u.Contains("/volunteer/") && !u.Contains("/auth/")) return;
            if (DebugCapture)
                _engine.Log($"🌐 REQ {req.Method} {u.Split("/xsxk")[^1]} | body: {(req.PostData ?? "")[..Math.Min(200, (req.PostData ?? "").Length)]}");
            var headers = req.Headers;
            bool captured = false;
            foreach (var key in new[] { "authorization", "batchid" })
                if (headers.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v))
                { _engine.OnHeaderCaptured(key, v); captured = true; }
            if (captured) _engine.OnHeadersCaptured();
        }
        catch { }
    }

    private void OnResponse(IResponse resp)
    {
        try
        {
            var u = resp.Url;
            if (!u.Contains("/elective/") && !u.Contains("/volunteer/") && !u.Contains("/auth/")) return;
            if (DebugCapture)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var body = (await resp.TextAsync())[..Math.Min(300, (await resp.TextAsync()).Length)];
                        _engine.Log($"🌐 RESP {resp.Status} {u.Split("/xsxk")[^1]} | {body.Replace("\n", " ")}");
                    }
                    catch { }
                });
            }
            if (!u.Contains("/elective/clazz/list")) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    var json = await resp.JsonAsync();
                    var rows = new Dictionary<string, JsonObject>();
                    Logic.WalkRows(JsonNode.Parse(json?.ToString() ?? "null"), rows);
                    string ctype = "";
                    try
                    {
                        var pd = resp.Request.PostData;
                        if (pd is not null)
                            ctype = (JsonNode.Parse(pd) as JsonObject)?["teachingClassType"]?.ToString() ?? "";
                    }
                    catch { }
                    if (rows.Count > 0)
                        _engine.OnRowsCaptured(rows.Values.ToList(), ctype);
                }
                catch { }
            });
        }
        catch { }
    }

    /// <summary>页面内 fetch（credentials: include），页面挂掉时抛异常由调用方降级</summary>
    public async Task<string> PageFetchAsync(string method, string url, string? query,
        Dictionary<string, string> headers, Dictionary<string, string>? form,
        JsonNode? jsonBody, int timeoutSec, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_page is null) throw new InvalidOperationException("无可用页面");
            var fullUrl = query is null ? url : $"{url}?{query}";
            string? body = null;
            var h = new Dictionary<string, string>(headers);
            if (jsonBody is not null)
            {
                h["Content-Type"] = "application/json";
                body = jsonBody.ToJsonString();
            }
            else if (method == "POST")
            {
                h["Content-Type"] = "application/x-www-form-urlencoded";
                body = string.Join("&", (form ?? new()).Select(kv =>
                    $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
            }
            var args = new Dictionary<string, object?>
            {
                ["url"] = fullUrl, ["method"] = method, ["headers"] = h, ["body"] = body,
            };
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
            var task = _page.EvaluateAsync<string>(
                "async (a) => { const r = await fetch(a.url, {method: a.method, headers: a.headers, " +
                "body: a.body, credentials: 'include'}); return r.text(); }", args);
            return await task.WaitAsync(TimeSpan.FromSeconds(timeoutSec), cts.Token);
        }
        finally { _gate.Release(); }
    }

    /// <summary>顶层页面导航到 grablessons</summary>
    public async Task<string> NavAsync(string url, string? query, int timeoutSec, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_page is null) throw new InvalidOperationException("无可用页面");
            var fullUrl = query is null ? url : $"{url}?{query}";
            await _page.GotoAsync(fullUrl, new()
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = timeoutSec * 1000,
            });
            return await _page.ContentAsync();
        }
        finally { _gate.Release(); }
    }

    /// <summary>取会话 Cookie（WS 认证用）</summary>
    public async Task<string> CookiesAsync()
    {
        if (_ctx is null) throw new InvalidOperationException("内置浏览器未打开");
        var cookies = await _ctx.CookiesAsync();
        return string.Join("; ", cookies.Where(c => c.Domain.Contains("nuist.edu.cn"))
                                        .Select(c => $"{c.Name}={c.Value}"));
    }

    public void Dispose()
    {
        Alive = false;
        try { _ctx?.CloseAsync().GetAwaiter().GetResult(); } catch { }
        try { _pw?.Dispose(); } catch { }
    }
}
