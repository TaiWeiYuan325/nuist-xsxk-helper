using System;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace XsxkAvalonia.Core;

/// <summary>
/// WebSocket 监听：wss://xsxk.nuist.edu.cn/xsxk/websocket/{学号}
/// 每 5 秒发 "hi" 心跳；code=200+data=="heart" 心跳；code=200+msg 含"选课成功"→成功；
/// code=500+msg 含"满"→满员。断线 2 秒重连。
/// </summary>
public sealed class WsListener : IDisposable
{
    public bool Connected { get; private set; }
    public bool Running { get; private set; }

    public event Action<string>? Log;
    public event Action? StateChanged;
    public event Action<string>? Full;      // clazzId（可能为空串）
    public event Action<string>? Success;   // clazzId（可能为空串）

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;

    public async Task StartAsync(string url, string cookie)
    {
        if (Running) return;
        Running = true;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    _ws = new ClientWebSocket();
                    _ws.Options.SetRequestHeader("Cookie", cookie);
                    _ws.Options.SetRequestHeader("Origin", "https://xsxk.nuist.edu.cn");
                    _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
                    await _ws.ConnectAsync(new Uri(url), ct);
                    Connected = true;
                    StateChanged?.Invoke();
                    Log?.Invoke("🔌 WS 已连接（实时接收选课结果推送）");

                    using var hbCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var hb = Task.Run(async () =>
                    {
                        while (!hbCts.IsCancellationRequested)
                        {
                            try
                            {
                                await Task.Delay(5000, hbCts.Token);
                                if (_ws.State == WebSocketState.Open)
                                    await _ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes("hi")),
                                        WebSocketMessageType.Text, true, hbCts.Token);
                            }
                            catch { }
                        }
                    });

                    var buf = new byte[8192];
                    var sb = new StringBuilder();
                    while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
                    {
                        var res = await _ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
                        if (res.MessageType == WebSocketMessageType.Close) break;
                        sb.Append(Encoding.UTF8.GetString(buf, 0, res.Count));
                        if (res.EndOfMessage)
                        {
                            OnMessage(sb.ToString());
                            sb.Clear();
                        }
                    }
                    hbCts.Cancel();
                    try { await hb; } catch { }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception e)
                {
                    var m = e.Message;
                    Log?.Invoke($"⚠️ WS 连接断开: {m[..Math.Min(80, m.Length)]}，2 秒后重连");
                }
                finally
                {
                    Connected = false;
                    StateChanged?.Invoke();
                    try { _ws?.Dispose(); } catch { }
                    _ws = null;
                }
                try { await Task.Delay(2000, ct); } catch { break; }
            }
        }
        finally { Running = false; }
    }

    private void OnMessage(string text)
    {
        JsonObject? j;
        try { j = JsonNode.Parse(text) as JsonObject; }
        catch { return; }
        if (j is null) return;
        var code = j["code"]?.ToString() ?? "";
        var msg = j["msg"]?.ToString() ?? "";
        var data = j["data"];
        var dataStr = data?.ToString() ?? "";
        if (code == "200" && dataStr == "heart") return; // 心跳
        var clazzId = data is JsonObject d ? (d["clazzId"]?.ToString() ?? "") : "";
        if (code == "200" && msg.Contains("选课成功"))
        {
            Log?.Invoke($"📩 WS: {msg}");
            Success?.Invoke(clazzId);
            return;
        }
        if (code == "500" && msg.Contains('满'))
        {
            Log?.Invoke($"📩 WS: {msg}");
            Full?.Invoke(clazzId);
            return;
        }
        if (!string.IsNullOrEmpty(msg)) Log?.Invoke($"📩 WS: {msg}");
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _ws?.Abort(); } catch { }
        Connected = false;
    }

    public void Dispose() => Stop();
}
