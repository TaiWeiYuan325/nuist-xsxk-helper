using System;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace XsxkAvalonia.Core;

/// <summary>
/// 选课系统 HTTP 客户端。浏览器在线时请求经 BrowserPump 代理（带页面会话），
/// 否则直接 HttpClient + 模拟页面 axios 请求头。
/// </summary>
public sealed class XsxkClient
{
    private readonly GrabEngine _engine;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public XsxkClient(GrabEngine engine) => _engine = engine;

    private string Base => _engine.Base.TrimEnd('/');
    private string Origin => Base.Split("/xsxk")[0];

    private async Task<string> SendAsync(string method, string path, Dictionary<string, string>? form,
        JsonNode? jsonBody, Dictionary<string, string>? query, int timeoutSec, CancellationToken ct)
    {
        // 浏览器在线：走页面内 fetch（带 credentials）
        if (_engine.Browser.Alive)
        {
            try
            {
                return await _engine.Browser.PageFetchAsync(method, Base + path, BuildQuery(query),
                    BuildHeaders(), form, jsonBody, timeoutSec, ct);
            }
            catch (Exception e)
            {
                _engine.Log($"⚠️ 页面内请求失败，改用直连: {e.Message[..Math.Min(80, e.Message.Length)]}");
            }
        }
        return await DirectAsync(method, path, form, jsonBody, query, timeoutSec, ct);
    }

    private string? BuildQuery(Dictionary<string, string>? query)
        => query is null or { Count: 0 } ? null
           : string.Join("&", System.Linq.Enumerable.Select(query, kv =>
               $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

    private Dictionary<string, string> BuildHeaders()
    {
        var h = new Dictionary<string, string>
        {
            ["User-Agent"] = string.IsNullOrEmpty(_engine.Browser.UserAgent)
                ? "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36"
                : _engine.Browser.UserAgent,
            ["Accept"] = "application/json, text/plain, */*",
            ["Accept-Language"] = "zh-CN,zh;q=0.9,en;q=0.8",
            ["Origin"] = Origin,
            ["Referer"] = Base + "/elective/grablessons",
        };
        if (!string.IsNullOrEmpty(_engine.Auth)) h["Authorization"] = _engine.Auth;
        if (!string.IsNullOrEmpty(_engine.Batch)) h["batchid"] = _engine.Batch;
        return h;
    }

    private async Task<string> DirectAsync(string method, string path, Dictionary<string, string>? form,
        JsonNode? jsonBody, Dictionary<string, string>? query, int timeoutSec, CancellationToken ct)
    {
        var url = Base + path;
        var qs = BuildQuery(query);
        if (qs is not null) url += "?" + qs;
        using var req = new HttpRequestMessage(new HttpMethod(method), url);
        foreach (var (k, v) in BuildHeaders()) req.Headers.TryAddWithoutValidation(k, v);
        if (jsonBody is not null)
            req.Content = new StringContent(jsonBody.ToJsonString(), Encoding.UTF8, "application/json");
        else if (form is not null)
            req.Content = new FormUrlEncodedContent(form);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
        var resp = await _http.SendAsync(req, cts.Token);
        return await resp.Content.ReadAsStringAsync(cts.Token);
    }

    public Task<string> PostAsync(string path, Dictionary<string, string>? form = null, int timeoutSec = 15, CancellationToken ct = default)
        => SendAsync("POST", path, form ?? new(), null, null, timeoutSec, ct);

    public Task<string> PostJsonAsync(string path, JsonNode body, int timeoutSec = 15, CancellationToken ct = default)
        => SendAsync("POST", path, null, body, null, timeoutSec, ct);

    public Task<string> GetAsync(string path, Dictionary<string, string>? query = null, int timeoutSec = 15, CancellationToken ct = default)
        => SendAsync("GET", path, null, null, query, timeoutSec, ct);

    /// <summary>顶层页面导航——服务器只认真实导航来确立会话当前轮次</summary>
    public async Task<string> NavAsync(Dictionary<string, string>? query, int timeoutSec, CancellationToken ct)
    {
        if (!_engine.Browser.Alive)
            return await GetAsync("/elective/grablessons", query, timeoutSec, ct);
        return await _engine.Browser.NavAsync(Base + "/elective/grablessons", BuildQuery(query), timeoutSec, ct);
    }
}
