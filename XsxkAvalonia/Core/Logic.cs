using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace XsxkAvalonia.Core;

/// <summary>纯函数移植：xsxk_app.py 的解析/判定逻辑，语义逐条对齐。</summary>
public static partial class Logic
{
    public static readonly string[] Retryable = { "暂未开始", "未开始", "未开放", "系统繁忙", "稍后", "队列拥堵", "请求频繁", "超时", "结束" };
    public static readonly string[] AuthMsgs = { "未登录", "登录失效", "认证失败", "token失效", "token过期" };

    public static readonly string[] F_NAME = { "KCM", "KCMC", "courseName", "kcm", "name" };
    public static readonly string[] F_TEACHER = { "SKJS", "JSM", "teacherName", "teacher", "js" };
    public static readonly string[] F_CAP = { "KRL", "capacity", "krl", "KXRL" };
    public static readonly string[] F_CHOSEN = { "YXS", "chosen", "YXRS", "yxrs", "selected" };
    public static readonly string[] F_TIME = { "SKDD", "YPSJDD", "time", "SKSJ" };

    public static string Pick(JsonObject row, params string[] keys)
    {
        foreach (var k in keys)
        {
            var v = row[k];
            if (v is not null)
            {
                var s = v.ToString();
                if (s != "") return s;
            }
        }
        return "";
    }

    [GeneratedRegex(@"^[0-9a-zA-Z+/=_-]{20,}$")]
    private static partial Regex TokenishRe();

    [GeneratedRegex(@"\d+-\d+周.*节")]
    private static partial Regex TimeStrRe();

    /// <summary>课程名：优先含"板块/课"特征的字段；排除班级列表、token、时间串</summary>
    public static string RowName(JsonObject row)
    {
        var n = Pick(row, F_NAME);
        var candidates = new List<string>();
        foreach (var (k, v) in row)
        {
            if (k is "JXBID" or "secretVal") continue;
            var s = v?.ToString();
            if (string.IsNullOrEmpty(s) || v is JsonObject or JsonArray) continue;
            if (TokenishRe().IsMatch(s) || TimeStrRe().IsMatch(s)) continue;
            if (CountOccurrences(s, "班") >= 2) continue;
            candidates.Add(s);
        }
        var featured = candidates.FindAll(v => v.Contains('板') || v.Contains("课"));
        var pool = featured.Count > 0 ? featured : candidates;
        var longest = "";
        foreach (var c in pool) if (c.Length > longest.Length) longest = c;
        if (n != "" && (longest == "" || longest.Length <= n.Length)) return n;
        return longest != "" ? longest : n;
    }

    public static string RowTime(JsonObject row)
    {
        var t = Pick(row, F_TIME);
        if (t != "") return t;
        foreach (var (_, v) in row)
        {
            var s = v?.ToString();
            if (!string.IsNullOrEmpty(s) && v is not JsonObject and not JsonArray && TimeStrRe().IsMatch(s))
                return s;
        }
        return "";
    }

    private static int CountOccurrences(string s, string sub)
    {
        int count = 0, i = 0;
        while ((i = s.IndexOf(sub, i, StringComparison.Ordinal)) >= 0) { count++; i += sub.Length; }
        return count;
    }

    /// <summary>判定 /add 响应：success/full/impossible/auth/notOpen/fail/badHtml</summary>
    public static (string Type, string Msg) Judge(string text)
    {
        if (string.IsNullOrEmpty(text) || text.TrimStart().StartsWith('<'))
            return ("badHtml", "服务器返回了HTML而非JSON");
        JsonObject j;
        try { j = JsonNode.Parse(text) as JsonObject ?? new JsonObject(); }
        catch { return ("fail", text[..Math.Min(120, text.Length)]); }
        var msg = (j["msg"]?.ToString() ?? "");
        if (msg.Length > 120) msg = msg[..120];
        var code = j["code"]?.GetValue<int?>() ?? (j["code"]?.ToString() == "200" ? 200 : -1);
        if (code == 200 || msg.Contains("成功")) return ("success", msg);
        if (msg.Contains('满') || msg.Contains("容量")) return ("full", msg);
        if (msg.Contains("冲突") || msg.Contains("已选") || msg.Contains("重复") || msg.Contains("超过"))
            return ("impossible", msg);
        foreach (var k in AuthMsgs)
            if (msg.Contains(k, StringComparison.OrdinalIgnoreCase)) return ("auth", msg);
        foreach (var k in Retryable)
            if (msg.Contains(k)) return ("notOpen", msg);
        return ("fail", msg == "" ? "未知响应" : msg);
    }

    /// <summary>从 /volunteer/list/choose 响应中挑最低的未使用志愿级别；无可用返回 null</summary>
    public static int? PickFreeGrade(string respText)
    {
        JsonObject j;
        try { j = JsonNode.Parse(respText) as JsonObject ?? new JsonObject(); }
        catch { return null; }
        if (j["code"]?.GetValue<int?>() != 200) return null;
        var data = j["data"] as JsonArray;
        if (data is null) return null;
        int? best = null;
        foreach (var g in data)
        {
            if (g is not JsonObject o) continue;
            var isUse = o["isUse"];
            bool used = isUse is not null && isUse.ToString() == "True" || isUse?.ToString() == "true";
            if (used) continue;
            if (int.TryParse(o["grade"]?.ToString(), out var grade))
                if (best is null || grade < best) best = grade;
        }
        return best;
    }

    /// <summary>宽松解析：容忍对象重复键（后者覆盖前者），非 JSON 时抛带片段的异常</summary>
    public static JsonObject ParseJsonText(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return FromElement(doc.RootElement) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            var snippet = string.IsNullOrEmpty(text) ? "(空响应)" : text[..Math.Min(200, text.Length)].Replace("\n", " ");
            throw new InvalidOperationException($"响应非JSON: {snippet}");
        }
    }

    /// <summary>JsonElement → JsonNode。服务器响应偶发重复字段（如 KCXZ），
    /// JsonNode.Parse 会直接抛异常，这里手动重建并去重（后者覆盖前者）。</summary>
    public static JsonNode? FromElement(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                var o = new JsonObject();
                foreach (var p in el.EnumerateObject())
                {
                    o.Remove(p.Name);
                    o[p.Name] = FromElement(p.Value);
                }
                return o;
            case JsonValueKind.Array:
                var a = new JsonArray();
                foreach (var item in el.EnumerateArray()) a.Add(FromElement(item));
                return a;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return null;
            default:
                return JsonNode.Parse(el.GetRawText());
        }
    }

    /// <summary>递归收集含 JXBID+secretVal 的行</summary>
    public static void WalkRows(JsonNode? node, Dictionary<string, JsonObject> output)
    {
        switch (node)
        {
            case JsonObject o:
                if (o["JXBID"] is not null && o["secretVal"] is not null)
                {
                    var id = o["JXBID"]!.ToString();
                    output[id] = o;
                }
                foreach (var (_, v) in o) WalkRows(v, output);
                break;
            case JsonArray a:
                foreach (var v in a) WalkRows(v, output);
                break;
        }
    }

    /// <summary>从 grablessons 页面解析 grablessonsVue.lcParam.currentBatch.menuList</summary>
    public static List<(string Code, string Name)> ParseClassTypes(string html)
    {
        const string marker = "grablessonsVue.lcParam.currentBatch";
        var i = (html ?? "").IndexOf(marker, StringComparison.Ordinal);
        if (i < 0) return new();
        var j = html.IndexOf('{', i + marker.Length);
        if (j < 0) return new();
        try
        {
            var node = JsonNode.Parse(ExtractJsonObject(html, j)) as JsonObject;
            var menu = node?["menuList"] as JsonArray;
            var output = new List<(string, string)>();
            if (menu is null) return output;
            foreach (var item in menu)
            {
                if (item is JsonObject o && o["teachingClassType"] is not null && o["displayName"] is not null)
                    output.Add((o["teachingClassType"]!.ToString(), o["displayName"]!.ToString()));
            }
            return output;
        }
        catch { return new(); }
    }

    /// <summary>宽松兜底：正则找 teachingClassType/displayName 键值对</summary>
    public static List<(string Code, string Name)> ParseClassTypesLoose(string html)
    {
        var output = new List<(string, string)>();
        var seen = new HashSet<string>();
        var pats = new[]
        {
            (new Regex(@"[""']?teachingClassType[""']?\s*:\s*[""']([A-Za-z0-9]+)[""'][^{}]{0,300}?[""']?displayName[""']?\s*:\s*[""']([^""']+)[""']"), 1, 2),
            (new Regex(@"[""']?displayName[""']?\s*:\s*[""']([^""']+)[""'][^{}]{0,300}?[""']?teachingClassType[""']?\s*:\s*[""']([A-Za-z0-9]+)[""']"), 2, 1),
        };
        foreach (var (pat, ci, ni) in pats)
            foreach (Match m in pat.Matches(html ?? ""))
            {
                var code = m.Groups[ci].Value;
                var name = m.Groups[ni].Value;
                if (code != "" && seen.Add(code)) output.Add((code, name));
            }
        return output;
    }

    /// <summary>从 grablessons 页面解析 grablessonsVue.lcParam.currentBatch.code（验证会话轮次用），失败返回 ""。</summary>
    public static string PageBatchCode(string html)
    {
        const string marker = "grablessonsVue.lcParam.currentBatch";
        var i = (html ?? "").IndexOf(marker, StringComparison.Ordinal);
        if (i < 0) return "";
        var j = html!.IndexOf('{', i + marker.Length);
        if (j < 0) return "";
        try
        {
            return (JsonNode.Parse(ExtractJsonObject(html, j)) as JsonObject)?["code"]?.ToString() ?? "";
        }
        catch { return ""; }
    }

    /// <summary>提取 HTML 页面标题（诊断用），无标题返回 ""。</summary>
    public static string HtmlTitle(string html)
    {
        var m = Regex.Match(html ?? "", @"<title>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return m.Success ? m.Groups[1].Value.Trim() : "";
    }

    /// <summary>从个人主页 /profile/index.html 的 var batch = {...} 中解析当前批次 code。
    /// 无浏览器时用它引导出批次 ID（YoungUsing 的 login_with_token 同款做法），失败返回 ""。</summary>
    public static string ParseProfileBatchId(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var m = Regex.Match(html, @"var\s+batch\s*=");
        if (!m.Success) return "";
        var j = html.IndexOf('{', m.Index + m.Length);
        if (j < 0) return "";
        try
        {
            var node = JsonNode.Parse(ExtractJsonObject(html, j)) as JsonObject;
            return node?["code"]?.ToString() ?? "";
        }
        catch { return ""; }
    }

    /// <summary>从 html[pos]（应为'{'）起提取平衡括号 JSON 子串</summary>
    private static string ExtractJsonObject(string s, int pos)
    {
        int depth = 0; bool inStr = false; bool esc = false;
        for (int i = pos; i < s.Length; i++)
        {
            var c = s[i];
            if (esc) { esc = false; continue; }
            if (c == '\\') { esc = true; continue; }
            if (c == '"') { inStr = !inStr; continue; }
            if (inStr) continue;
            if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return s[pos..(i + 1)];
        }
        return s[pos..];
    }

    public static string FmtTime(object? v)
    {
        if (v is null) return "";
        var s = v.ToString() ?? "";
        if (s == "") return "";
        if (long.TryParse(s, out var ms))
            return DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
        return s;
    }

    public static long? ToEpochMs(object? v)
    {
        if (v is null) return null;
        var s = v.ToString() ?? "";
        if (s == "") return null;
        if (long.TryParse(s, out var ms)) return ms;
        foreach (var f in new[] { "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm" })
            if (DateTime.TryParseExact(s, f, null, System.Globalization.DateTimeStyles.AssumeLocal, out var dt))
                return new DateTimeOffset(dt).ToUnixTimeMilliseconds();
        return null;
    }

    /// <summary>兼容 '2026-07-31 16:00:00' / '2026-7-30 11:00'</summary>
    public static DateTimeOffset ParseStartTime(string s)
    {
        s = (s ?? "").Trim().Replace('/', '-').Replace('T', ' ');
        foreach (var f in new[] { "yyyy-M-d H:m:s", "yyyy-M-d H:m" })
            if (DateTime.TryParseExact(s, f, null, System.Globalization.DateTimeStyles.AssumeLocal, out var dt))
                return new DateTimeOffset(dt);
        throw new FormatException($"无法解析时间: {s}");
    }

    /// <summary>从 JWT payload 解学号 login_user_key</summary>
    public static string StudentIdFromJwt(string jwt)
    {
        try
        {
            var payload = jwt.Split('.')[1];
            payload = payload.Replace('-', '+').Replace('_', '/');
            payload += new string('=', (4 - payload.Length % 4) % 4);
            var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            return (JsonNode.Parse(json) as JsonObject)?["login_user_key"]?.ToString() ?? "";
        }
        catch { return ""; }
    }
}
