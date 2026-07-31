using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media;

namespace XsxkWinUI;

// ---------- 后端 HTTP 客户端 ----------
public static class Backend
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    public const string Base = "http://127.0.0.1:18765";
    public static bool Online;

    public static async Task<JsonElement?> GetAsync(string path)
    {
        try
        {
            var s = await Http.GetStringAsync(Base + path);
            Online = true;
            return JsonDocument.Parse(s).RootElement;
        }
        catch
        {
            Online = false;
            return null;
        }
    }

    public static async Task ActionAsync(object payload)
    {
        try
        {
            var body = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            await Http.PostAsync(Base + "/api/action", body);
            Online = true;
        }
        catch
        {
            Online = false;
        }
    }

    /// <summary>后端不在线时尝试启动：优先同目录/上级目录的 xsxk_backend.exe（打包版），
    /// 其次 xsxk_backend.py（开发版）</summary>
    public static bool TryStartBackend()
    {
        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 7 && dir != null; i++, dir = dir.Parent)
            {
                var exe = Path.Combine(dir.FullName, "xsxk_backend.exe");
                if (File.Exists(exe))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = exe,
                        WorkingDirectory = dir.FullName,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden,
                    });
                    return true;
                }
                var py = Path.Combine(dir.FullName, "xsxk_backend.py");
                if (File.Exists(py))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = "/c start \"xsxk-backend\" /min python xsxk_backend.py",
                        WorkingDirectory = dir.FullName,
                        CreateNoWindow = true,
                    });
                    return true;
                }
            }
        }
        catch { }
        return false;
    }
}

// ---------- 视图模型 ----------
public class CourseVm
{
    public string Cid { get; set; } = "";
    public string Name { get; set; } = "";
    public string Teacher { get; set; } = "";
    public string Time { get; set; } = "";
    public string Cap { get; set; } = "";
    public string Chosen { get; set; } = "";
}

public class VolunteerVm
{
    public int Index { get; set; }
    public string Cid { get; set; } = "";
    public string Label { get; set; } = "";
    public string Type { get; set; } = "";
    public string Display => $"{Index + 1}. {Label}";
}

public class LogLine
{
    public string Text { get; set; } = "";
    public SolidColorBrush Color { get; set; } = new(Microsoft.UI.Colors.Gray);
}

public static class LogColor
{
    public static SolidColorBrush Of(string text)
    {
        if (text.StartsWith("✅") || text.StartsWith("🔑") || text.StartsWith("📚") || text.StartsWith("➕"))
            return new SolidColorBrush(Microsoft.UI.Colors.ForestGreen);
        if (text.StartsWith("❌") || text.StartsWith("⛔") || text.StartsWith("🔒"))
            return new SolidColorBrush(Microsoft.UI.Colors.IndianRed);
        if (text.StartsWith("⚠️") || text.StartsWith("🔁"))
            return new SolidColorBrush(Microsoft.UI.Colors.DarkOrange);
        if (text.StartsWith("⏰") || text.StartsWith("🔀") || text.StartsWith("🎯") || text.StartsWith("🔄"))
            return new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue);
        if (text.StartsWith("🌐"))
            return new SolidColorBrush(Microsoft.UI.Colors.Gray);
        return new SolidColorBrush(Microsoft.UI.Colors.WhiteSmoke);
    }
}
