using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace XsxkAvalonia.Core;

/// <summary>
/// 选课系统会话客户端——airline233 的 requests.Session 等价物。
/// 一个进程级共享 CookieContainer：登录响应种下的会话 Cookie 自动累积、随后续请求回放，
/// 服务器认这个"罐"，登录/切轮次/拉列表/抢课全部落在同一个会话里，不再需要任何浏览器。
/// </summary>
public sealed class SessionClient
{
    private readonly GrabEngine _engine;
    private readonly CookieContainer _jar = new();
    private readonly HttpClient _http;

    /// <summary>来自校方前端代码的密码加密密钥（airline233 同款）</summary>
    private const string AesKey = "MWMqg2tPcDkxcm11";

    public SessionClient(GrabEngine engine)
    {
        _engine = engine;
        _http = new HttpClient(new HttpClientHandler
        {
            UseCookies = true,
            CookieContainer = _jar,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        })
        { Timeout = TimeSpan.FromSeconds(15) };
    }

    private string Base => _engine.Base.TrimEnd('/');
    private string Origin => Base.Split("/xsxk")[0];

    private static string? BuildQuery(Dictionary<string, string>? query)
        => query is null or { Count: 0 } ? null
           : string.Join("&", query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

    /// <summary>axios 风格 API 头（airline233 COMMON_HEADERS 同款）。
    /// withBatch=false 用于 POST /elective/user 切换轮次——头里带 batchid 会被服务器
    /// 按"头与会话不一致"拒绝（我们早期直连失败的具体原因之一）。</summary>
    private void ApplyApiHeaders(HttpRequestMessage req, bool withBatch)
    {
        req.Headers.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
        req.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        req.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
        req.Headers.TryAddWithoutValidation("Origin", Origin);
        req.Headers.TryAddWithoutValidation("Referer", Base + "/elective/grablessons");
        if (!string.IsNullOrEmpty(_engine.Auth))
            req.Headers.TryAddWithoutValidation("Authorization", _engine.Auth);
        if (withBatch && !string.IsNullOrEmpty(_engine.Batch))
            req.Headers.TryAddWithoutValidation("batchid", _engine.Batch);
    }

    public async Task<string> SendAsync(string method, string path, Dictionary<string, string>? form,
        JsonNode? jsonBody, Dictionary<string, string>? query, int timeoutSec, CancellationToken ct, bool withBatch = true)
    {
        var url = Base + path;
        var qs = BuildQuery(query);
        if (qs is not null) url += "?" + qs;
        using var req = new HttpRequestMessage(new HttpMethod(method), url);
        ApplyApiHeaders(req, withBatch);
        if (jsonBody is not null)
            req.Content = new StringContent(jsonBody.ToJsonString(), Encoding.UTF8, "application/json");
        else if (form is not null)
            req.Content = new FormUrlEncodedContent(form);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
        var resp = await _http.SendAsync(req, cts.Token);
        return await resp.Content.ReadAsStringAsync(cts.Token);
    }

    public Task<string> PostAsync(string path, Dictionary<string, string>? form = null, int timeoutSec = 15,
        CancellationToken ct = default, bool withBatch = true)
        => SendAsync("POST", path, form ?? new(), null, null, timeoutSec, ct, withBatch);

    public Task<string> PostJsonAsync(string path, JsonNode body, int timeoutSec = 15, CancellationToken ct = default)
        => SendAsync("POST", path, null, body, null, timeoutSec, ct);

    public Task<string> GetAsync(string path, Dictionary<string, string>? query = null, int timeoutSec = 15, CancellationToken ct = default)
        => SendAsync("GET", path, null, null, query, timeoutSec, ct);

    /// <summary>切换服务器会话轮次（airline233 switch_batch 同款）：
    /// body 带目标 batchId、头不带 batchid；重登后服务器轮次状态丢失，需要重放本调用。</summary>
    public async Task<bool> SwitchBatchAsync(string batchId, int timeoutSec, CancellationToken ct)
    {
        try
        {
            var text = await PostAsync("/elective/user",
                new Dictionary<string, string> { ["batchId"] = batchId }, timeoutSec, ct, withBatch: false);
            return Logic.ParseJsonText(text)["code"]?.ToString() == "200";
        }
        catch { return false; }
    }

    /// <summary>页面 GET（浏览器导航风格头）。有了真会话 Cookie 后服务器会返回真实页面，
    /// 导航到 ?batchId= 也是服务器认可的轮次切换方式之一。</summary>
    public async Task<string> GetPageAsync(string path, Dictionary<string, string>? query, int timeoutSec, CancellationToken ct)
    {
        var url = Base + path;
        var qs = BuildQuery(query);
        if (qs is not null) url += "?" + qs;
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
        req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        req.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
        req.Headers.TryAddWithoutValidation("Referer", Base + "/elective/index.html");
        req.Headers.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
        if (!string.IsNullOrEmpty(_engine.Auth))
            req.Headers.TryAddWithoutValidation("Authorization", _engine.Auth);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
        var resp = await _http.SendAsync(req, cts.Token);
        return await resp.Content.ReadAsStringAsync(cts.Token);
    }

    /// <summary>顶层页面导航（切轮次 + 取 grablessons 页）</summary>
    public Task<string> NavAsync(Dictionary<string, string>? query, int timeoutSec, CancellationToken ct)
        => GetPageAsync("/elective/grablessons", query, timeoutSec, ct);

    // ================= 登录 =================

    /// <summary>AES-128-ECB + PKCS7 + base64（airline233 encrypt_password 同款）</summary>
    public static string EncryptPassword(string password)
    {
        using var aes = Aes.Create();
        aes.Key = Encoding.UTF8.GetBytes(AesKey);
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.PKCS7;
        using var enc = aes.CreateEncryptor();
        var plain = Encoding.UTF8.GetBytes(password);
        return Convert.ToBase64String(enc.TransformFinalBlock(plain, 0, plain.Length));
    }

    /// <summary>POST /auth/captcha → (验证码图片字节, uuid)</summary>
    public async Task<(byte[] Image, string Uuid)> GetCaptchaAsync(int timeoutSec, CancellationToken ct)
    {
        var text = await PostAsync("/auth/captcha", new Dictionary<string, string>(), timeoutSec, ct, withBatch: false);
        var jo = Logic.ParseJsonText(text);
        var b64 = jo["data"]?["captcha"]?.ToString() ?? "";
        var uuid = jo["data"]?["uuid"]?.ToString() ?? "";
        var idx = b64.IndexOf(',');
        if (idx >= 0) b64 = b64[(idx + 1)..];
        if (uuid == "" || b64 == "")
            throw new InvalidOperationException("验证码响应格式异常: " + text[..Math.Min(120, text.Length)]);
        return (Convert.FromBase64String(b64), uuid);
    }

    /// <summary>POST /auth/login → data（含 token/student）。code!=200 抛带服务器 msg 的异常</summary>
    public async Task<JsonObject> LoginAsync(string username, string password, string captcha, string uuid,
        int timeoutSec, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["loginname"] = username,
            ["password"] = EncryptPassword(password),
            ["captcha"] = captcha,
            ["uuid"] = uuid,
        };
        var text = await PostAsync("/auth/login", form, timeoutSec, ct, withBatch: false);
        var jo = Logic.ParseJsonText(text);
        if (jo["code"]?.ToString() != "200")
            throw new InvalidOperationException(jo["msg"]?.ToString() ?? $"登录失败（{text[..Math.Min(120, text.Length)]}）");
        return jo["data"] as JsonObject ?? new JsonObject();
    }

    /// <summary>WS 握手用的 Cookie 字符串：罐内全部 Cookie + Authorization</summary>
    public string CookieHeader()
    {
        var parts = _jar.GetCookies(new Uri(Base)).Cast<Cookie>()
            .Select(c => $"{c.Name}={c.Value}").ToList();
        if (!string.IsNullOrEmpty(_engine.Auth) && parts.All(p => !p.StartsWith("Authorization=")))
            parts.Insert(0, $"Authorization={_engine.Auth}");
        return string.Join("; ", parts);
    }
}
