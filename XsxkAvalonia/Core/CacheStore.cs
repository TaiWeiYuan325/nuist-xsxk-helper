using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace XsxkAvalonia.Core;

/// <summary>
/// 本地缓存：轮次列表 + 每轮次的类别/课程列表，存 exe 同目录 xsxk_cache.json。
/// 轮次未开始时服务器不提供选课页，靠缓存照样能看课、排志愿、定时开抢。
/// </summary>
public class CacheStore
{
    private readonly string _path = Path.Combine(AppContext.BaseDirectory, "xsxk_cache.json");
    private JsonObject _root = new();
    private int _flushPending;

    public void Load()
    {
        try
        {
            if (File.Exists(_path))
                _root = Logic.ParseJsonText(File.ReadAllText(_path));
            // 登录态不再记忆：旧缓存文件里残留的 auth 字段（token）直接清除并落盘
            if (_root.Remove("auth")) Flush();
        }
        catch { _root = new JsonObject(); }
    }

    // ---------- 轮次 ----------

    public List<BatchInfo> LoadBatches()
    {
        var list = new List<BatchInfo>();
        if (_root["batches"] is not JsonArray arr) return list;
        foreach (var item in arr)
        {
            if (item is not JsonObject o) continue;
            var b = new BatchInfo
            {
                Wid = o["wid"]?.ToString() ?? "",
                Name = o["name"]?.ToString() ?? "",
                STime = o["sTime"]?.ToString() ?? "",
                ETime = o["eTime"]?.ToString() ?? "",
                CanSelect = o["canSelect"]?.GetValue<bool>() ?? true,
                NoSelectReason = o["noSelectReason"]?.ToString() ?? "",
            };
            if (b.Wid != "") list.Add(b);
        }
        return list;
    }

    public void SaveBatches(List<BatchInfo> batches)
    {
        var arr = new JsonArray();
        foreach (var b in batches)
            arr.Add(new JsonObject
            {
                ["wid"] = b.Wid,
                ["name"] = b.Name,
                ["sTime"] = b.STime,
                ["eTime"] = b.ETime,
                ["canSelect"] = b.CanSelect,
                ["noSelectReason"] = b.NoSelectReason,
            });
        lock (this) { _root["batches"] = arr; }
        Flush();
    }

    // ---------- 每轮次数据 ----------

    private JsonObject BatchNode(string wid, bool create)
    {
        if (_root["perBatch"] is not JsonObject per)
        {
            if (!create) return new JsonObject();
            per = new JsonObject();
            _root["perBatch"] = per;
        }
        if (per[wid] is not JsonObject node)
        {
            if (!create) return new JsonObject();
            node = new JsonObject();
            per[wid] = node;
        }
        return node;
    }

    public List<(string Code, string Name)> LoadClassTypes(string wid)
    {
        var output = new List<(string, string)>();
        JsonObject node;
        lock (this) node = BatchNode(wid, false);
        if (node["classTypes"] is not JsonArray arr) return output;
        foreach (var item in arr)
            if (item is JsonObject o && o["code"]?.ToString() is string c && c != "")
                output.Add((c, o["name"]?.ToString() ?? c));
        return output;
    }

    public void SaveClassTypes(string wid, List<(string Code, string Name)> types)
    {
        var arr = new JsonArray();
        foreach (var (c, n) in types) arr.Add(new JsonObject { ["code"] = c, ["name"] = n });
        lock (this) BatchNode(wid, true)["classTypes"] = arr;
        Flush();
    }

    public List<JsonObject> LoadCourses(string wid, string ctype)
    {
        var output = new List<JsonObject>();
        JsonObject node;
        lock (this) node = BatchNode(wid, false);
        if (node["courses"] is not JsonObject courses || courses[ctype] is not JsonArray arr) return output;
        foreach (var item in arr)
            if (item is JsonObject o) output.Add((JsonObject)o.DeepClone());
        return output;
    }

    public void SaveCourses(string wid, string ctype, List<JsonObject> rows)
    {
        var arr = new JsonArray();
        foreach (var r in rows) arr.Add(r.DeepClone());
        lock (this)
        {
            var node = BatchNode(wid, true);
            if (node["courses"] is not JsonObject courses)
            {
                courses = new JsonObject();
                node["courses"] = courses;
            }
            courses[ctype] = arr;
        }
        Flush();
    }

    // ---------- 落盘（去抖 500ms） ----------

    private void Flush()
    {
        if (Interlocked.Exchange(ref _flushPending, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(500);
            string text;
            lock (this) { _flushPending = 0; text = _root.ToJsonString(); }
            try { File.WriteAllText(_path, text); } catch { }
        });
    }
}
