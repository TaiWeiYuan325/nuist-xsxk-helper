using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json.Nodes;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XsxkAvalonia.Core;

namespace XsxkAvalonia.ViewModels;

public class ClassTypeItem
{
    public string Code = "";
    public string Name = "";
    public override string ToString() => $"{Name}（{Code}）";
}

public class CourseRow
{
    public string Name { get; set; } = "";
    public string Teacher { get; set; } = "";
    public string Time { get; set; } = "";
    public string Cap { get; set; } = "";
    public string Chosen { get; set; } = "";
    public string Id { get; set; } = "";
    public string Secret { get; set; } = "";
}

public class LogLine
{
    public string Text { get; set; } = "";
    public IBrush Brush { get; set; } = Brushes.LightGray;
}

public class VolunteerRow
{
    public int Num { get; set; }
    public VolunteerItem Item { get; set; } = new();
    public string Name => Item.Name;
}

public partial class MainViewModel : ObservableObject
{
    public GrabEngine Engine { get; } = new();

    public ObservableCollection<LogLine> Logs { get; } = new();
    public ObservableCollection<BatchInfo> Batches { get; } = new();
    public ObservableCollection<ClassTypeItem> ClassTypes { get; } = new();
    public ObservableCollection<VolunteerRow> Volunteers { get; } = new();
    public ObservableCollection<CourseRow> FilteredCourses { get; } = new();

    private List<CourseRow> _allCourses = new();
    private bool _syncing;

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private BatchInfo? _selectedBatch;
    [ObservableProperty] private ClassTypeItem? _selectedClassType;
    [ObservableProperty] private VolunteerRow? _selectedVolunteer;
    [ObservableProperty] private CourseRow? _selectedCourse;
    [ObservableProperty] private string _startAtText = "";
    [ObservableProperty] private string _intervalText = "300";
    [ObservableProperty] private bool _debugCapture;
    [ObservableProperty] private string _batchTail = "批次 未捕获";

    // 状态区
    [ObservableProperty] private string _browserStatus = "未打开";
    [ObservableProperty] private IBrush _browserBrush = Brushes.Gray;
    [ObservableProperty] private string _tokenStatus = "未捕获";
    [ObservableProperty] private IBrush _tokenBrush = Brushes.Gray;
    [ObservableProperty] private string _wsStatus = "未连接";
    [ObservableProperty] private IBrush _wsBrush = Brushes.Gray;
    [ObservableProperty] private string _grabStatus = "待命";
    [ObservableProperty] private IBrush _grabBrush = Brushes.Gray;
    [ObservableProperty] private string _netText = "校时未开始";
    [ObservableProperty] private string _startButtonText = "开始抢课";
    [ObservableProperty] private bool _isGrabbing;
    [ObservableProperty] private string _courseCountText = "0 门课程";

    public MainViewModel()
    {
        Engine.Logged += m => Post(() => AddLog(m));
        Engine.StateChanged += () => Post(SyncFromEngine);
        AddLog($"[{DateTime.Now:HH:mm:ss.fff}] 南信大选课助手（Avalonia 版）已启动，点「打开内置浏览器」登录选课系统。");
    }

    private static void Post(Action a) => Dispatcher.UIThread.Post(a);

    private void AddLog(string m)
    {
        IBrush brush = Brushes.LightGray;
        if (m.Contains("🎉")) brush = new SolidColorBrush(Color.Parse("#4ADE80"));
        else if (m.Contains("❌") || m.Contains("⛔") || m.Contains("🔒")) brush = new SolidColorBrush(Color.Parse("#F87171"));
        else if (m.Contains("⚠️") || m.Contains("🈵")) brush = new SolidColorBrush(Color.Parse("#FBBF24"));
        else if (m.Contains("🔑") || m.Contains("🔄") || m.Contains("🔀") || m.Contains("🔌")) brush = new SolidColorBrush(Color.Parse("#60A5FA"));
        else if (m.Contains("➕") || m.Contains("📨") || m.Contains("📩")) brush = new SolidColorBrush(Color.Parse("#2DD4BF"));
        Logs.Add(new LogLine { Text = m, Brush = brush });
        while (Logs.Count > 2000) Logs.RemoveAt(0);
    }

    private void SyncFromEngine()
    {
        var e = Engine;
        _syncing = true;
        try
        {
            // 轮次
            if (!Batches.Select(b => b.Wid).SequenceEqual(e.Batches.Select(b => b.Wid)))
            {
                Batches.Clear();
                foreach (var b in e.Batches) Batches.Add(b);
            }
            SelectedBatch = Batches.FirstOrDefault(b => b.Wid == e.Batch) ?? SelectedBatch;
            // 类别
            if (!ClassTypes.Select(c => c.Code).SequenceEqual(e.ClassTypes.Select(c => c.Code)))
            {
                ClassTypes.Clear();
                foreach (var (code, name) in e.ClassTypes) ClassTypes.Add(new ClassTypeItem { Code = code, Name = name });
            }
            if (!string.IsNullOrEmpty(e.Ctype))
                SelectedClassType = ClassTypes.FirstOrDefault(c => c.Code == e.Ctype) ?? SelectedClassType;
            // 志愿（带编号重建）
            if (!Volunteers.Select(v => v.Item.Id).SequenceEqual(e.Volunteers.Select(v => v.Id)))
            {
                Volunteers.Clear();
                var n = 1;
                foreach (var v in e.Volunteers) Volunteers.Add(new VolunteerRow { Num = n++, Item = v });
            }
            // 批次
            BatchTail = string.IsNullOrEmpty(e.Batch) ? "批次 未捕获"
                : $"批次 …{(e.Batch.Length <= 8 ? e.Batch : e.Batch[^8..])}";
            // 开抢时间
            var sat = e.StartAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss") ?? "";
            if (sat != "" && sat != StartAtText) StartAtText = sat;
            // 课程
            RebuildCourses(e.Rows);
            // 状态
            BrowserStatus = e.BrowserOnline ? "已打开" : "未打开";
            BrowserBrush = e.BrowserOnline ? Brushes.ForestGreen : Brushes.Gray;
            TokenStatus = string.IsNullOrEmpty(e.Auth) ? "未捕获" : "已捕获";
            TokenBrush = string.IsNullOrEmpty(e.Auth) ? Brushes.Gray : Brushes.ForestGreen;
            WsStatus = e.WsConnected ? "已连接" : "未连接";
            WsBrush = e.WsConnected ? Brushes.ForestGreen : Brushes.Gray;
            GrabStatus = e.Grabbing ? "抢课中" : "待命";
            GrabBrush = e.Grabbing ? new SolidColorBrush(Color.Parse("#E11D48")) : Brushes.Gray;
            StartButtonText = e.Grabbing ? "停止抢课" : "开始抢课";
            IsGrabbing = e.Grabbing;
            if (!string.IsNullOrEmpty(e.NetText)) NetText = e.NetText;
            DebugCapture = e.Browser.DebugCapture;
        }
        finally { _syncing = false; }
    }

    private static string Prefer(string a, string b) => a != "" ? a : b;

    private void RebuildCourses(List<JsonObject> rows)
    {
        var list = rows.Select(r => new CourseRow
        {
            Name = Logic.RowName(r),
            Teacher = Logic.Pick(r, Logic.F_TEACHER),
            Time = Logic.RowTime(r),
            Cap = Prefer(Logic.Pick(r, Logic.F_CAP), Logic.Pick(r, "NSKRL")),
            Chosen = Prefer(Logic.Pick(r, Logic.F_CHOSEN), Logic.Pick(r, "NSXKRS")),
            Id = Logic.Pick(r, "JXBID"),
            Secret = Logic.Pick(r, "secretVal"),
        }).ToList();
        // 无变化不重刷，避免打断选择
        if (list.Count == _allCourses.Count && list.Select(c => c.Id).SequenceEqual(_allCourses.Select(c => c.Id))) return;
        _allCourses = list;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var q = SearchText.Trim();
        IEnumerable<CourseRow> src = _allCourses;
        if (q != "")
            src = src.Where(c => c.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                              || c.Teacher.Contains(q, StringComparison.OrdinalIgnoreCase)
                              || c.Time.Contains(q, StringComparison.OrdinalIgnoreCase));
        FilteredCourses.Clear();
        foreach (var c in src) FilteredCourses.Add(c);
        CourseCountText = q == "" ? $"{_allCourses.Count} 门课程" : $"筛选 {FilteredCourses.Count()} / {_allCourses.Count} 门";
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnDebugCaptureChanged(bool value) => Engine.Browser.DebugCapture = value;

    partial void OnSelectedBatchChanged(BatchInfo? value)
    {
        if (_syncing || value == null) return;
        _ = Engine.SelectBatchAsync(value);
    }

    partial void OnSelectedClassTypeChanged(ClassTypeItem? value)
    {
        if (_syncing || value == null) return;
        Engine.SelectClassType(value.Code);
    }

    // ================= 命令 =================

    [RelayCommand]
    private void OpenBrowser() => _ = Engine.OpenBrowserAsync();

    [RelayCommand]
    private void RefreshBatch() => _ = Engine.RefreshBatchAsync(true);

    [RelayCommand]
    private void CheckToken() => Engine.CheckToken();

    [RelayCommand]
    private void FetchCourses() => _ = Engine.FetchCoursesAsync();

    [RelayCommand]
    private void AddCourse(CourseRow? row)
    {
        row ??= SelectedCourse;
        if (row == null) return;
        Engine.AddVolunteer(row.Name, row.Id, row.Secret);
    }

    [RelayCommand]
    private void RemoveVolunteer() => Engine.RemoveVolunteer(SelectedVolunteer?.Item);

    [RelayCommand]
    private void ClearVolunteers() => Engine.ClearVolunteers();

    [RelayCommand]
    private void MoveUp() => Engine.MoveVolunteer(SelectedVolunteer?.Item, -1);

    [RelayCommand]
    private void MoveDown() => Engine.MoveVolunteer(SelectedVolunteer?.Item, +1);

    [RelayCommand]
    private void StartStop()
    {
        if (Engine.Grabbing) { Engine.StopGrab(); return; }
        // 推入设置
        if (int.TryParse(IntervalText.Trim(), out var ms) && ms >= 0) Engine.IntervalMs = ms;
        var t = StartAtText.Trim();
        if (t == "") Engine.StartAt = null;
        else
        {
            try { Engine.StartAt = Logic.ParseStartTime(t); }
            catch { AddLog($"[{DateTime.Now:HH:mm:ss.fff}] ⚠️ 开抢时间格式无法识别：{t}（示例 2026-07-30 11:00:00）"); return; }
        }
        Engine.StartGrab();
    }
}
