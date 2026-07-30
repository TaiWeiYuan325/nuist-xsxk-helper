using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace XsxkWinUI;

public sealed partial class MainWindow : Window
{
    private readonly ObservableCollection<CourseVm> _courses = new();
    private readonly ObservableCollection<VolunteerVm> _vols = new();
    private readonly ObservableCollection<LogLine> _logs = new();
    private readonly List<CourseVm> _allCourses = new();

    private readonly DispatcherTimer _stateTimer = new() { Interval = TimeSpan.FromMilliseconds(1000) };
    private readonly DispatcherTimer _logTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private int _logNext;
    private bool _suppress;          // 从快照回填控件时抑制事件，避免回写循环
    private bool _backendTried;
    private string _lastResult = "";
    private ScrollViewer? _logSv;
    private bool _logAtBottom = true;   // 用户在底部才自动跟随滚动

    public MainWindow()
    {
        InitializeComponent();
        this.AppWindow.Resize(new Windows.Graphics.SizeInt32(1440, 920));
        LstCourses.ItemsSource = _courses;
        LstVols.ItemsSource = _vols;
        LstLog.ItemsSource = _logs;

        // 日志自动跟随：仅当用户停留在底部时才滚到底，向上翻历史不被打断
        LstLog.Loaded += (_, _) =>
        {
            _logSv = FindScroll(LstLog);
            if (_logSv != null)
                _logSv.ViewChanged += (_, _) =>
                    _logAtBottom = _logSv.VerticalOffset >= _logSv.ScrollableHeight - 4;
        };

        _stateTimer.Tick += async (_, _) => await PollState();
        _logTimer.Tick += async (_, _) => await PollLogs();
        _stateTimer.Start();
        _logTimer.Start();
    }

    // ---------------- 轮询 ----------------
    private async Task PollState()
    {
        var s = await Backend.GetAsync("/api/state");
        if (s is null)
        {
            DotBackend.Fill = new SolidColorBrush(Microsoft.UI.Colors.IndianRed);
            TxtBackend.Text = "后端未连接";
            if (!_backendTried)
            {
                _backendTried = true;
                if (Backend.TryStartBackend())
                    TxtBackend.Text = "后端未连接（正在自动启动…）";
                else
                    TxtBackend.Text = "后端未连接（请先运行 python xsxk_backend.py）";
            }
            return;
        }
        var st = s.Value;
        DotBackend.Fill = new SolidColorBrush(Microsoft.UI.Colors.ForestGreen);
        TxtBackend.Text = "后端已连接";

        bool auth = st.GetProperty("auth").GetBoolean();
        bool browser = st.GetProperty("browser_open").GetBoolean();
        DotLogin.Fill = new SolidColorBrush(auth ? Microsoft.UI.Colors.ForestGreen : Microsoft.UI.Colors.Gray);
        TxtLogin.Text = auth ? "已捕获登录态" : (browser ? "浏览器已开，待登录" : "未登录");

        var net = st.GetProperty("net");
        if (net.GetProperty("ok").ValueKind == JsonValueKind.True)
        {
            var rtt = net.GetProperty("rtt").GetInt32();
            var online = net.GetProperty("online");
            TxtNet.Text = $"🌐 在线 {online} 人 · {rtt}ms";
            TxtNet.Foreground = new SolidColorBrush(rtt > 500 ? Microsoft.UI.Colors.IndianRed : Microsoft.UI.Colors.ForestGreen);
        }
        else
        {
            TxtNet.Text = "🌐 校时失败";
            TxtNet.Foreground = new SolidColorBrush(Microsoft.UI.Colors.IndianRed);
        }

        string batch = st.GetProperty("batch").GetString() ?? "";
        TxtBatch.Text = batch.Length > 8 ? $"批次 …{batch[^8..]}" : "";

        string cd = st.GetProperty("countdown").GetString() ?? "";
        TxtCountdown.Text = string.IsNullOrEmpty(cd) ? "" : $"⏳ 距开抢 {cd} 秒";

        bool grabbing = st.GetProperty("grabbing").GetBoolean();
        BtnGo.Content = grabbing ? "⏹ 停止" : "🚀 开始抢课";

        string result = st.GetProperty("last_result").GetString() ?? "";
        if (result != _lastResult)
        {
            _lastResult = result;
            BarResult.Message = result;
            BarResult.Severity = result.Contains("选上") || result.Contains("成功")
                ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
            BarResult.IsOpen = !string.IsNullOrEmpty(result);
        }

        _suppress = true;
        try
        {
            // 轮次下拉
            var labels = st.GetProperty("batch_labels").EnumerateArray().Select(x => x.GetString() ?? "").ToList();
            if (CmbBatch.Items.Count != labels.Count + 1 ||
                labels.Where((l, i) => (string?)CmbBatch.Items[i + 1] != l).Any())
            {
                CmbBatch.Items.Clear();
                CmbBatch.Items.Add("自动（当前轮次）");
                foreach (var l in labels) CmbBatch.Items.Add(l);
            }
            int sel = st.GetProperty("batch_sel").GetInt32();
            if (CmbBatch.SelectedIndex != sel && sel < CmbBatch.Items.Count)
                CmbBatch.SelectedIndex = sel;

            // 类别下拉
            var types = st.GetProperty("types").EnumerateArray()
                .Select(t => $"{t.GetProperty("code").GetString()} {t.GetProperty("name").GetString()}").ToList();
            string ctype = st.GetProperty("ctype").GetString() ?? "";
            if (!types.Any() && !string.IsNullOrEmpty(ctype)) types.Add(ctype);
            if (CmbType.Items.Count != types.Count ||
                types.Where((t, i) => (string?)CmbType.Items[i] != t).Any())
            {
                CmbType.Items.Clear();
                foreach (var t in types) CmbType.Items.Add(t);
            }
            int typeIdx = types.FindIndex(t => t.Split(' ')[0] == ctype.Split(' ')[0]);
            if (typeIdx >= 0 && CmbType.SelectedIndex != typeIdx)
                CmbType.SelectedIndex = typeIdx;

            // 设置项（仅在控件没有焦点时回填，避免打断输入）
            var focused = FocusManager.GetFocusedElement();
            string startAt = st.GetProperty("start_at").GetString() ?? "";
            if (!ReferenceEquals(focused, TxtStartAt) && TxtStartAt.Text != startAt)
                TxtStartAt.Text = startAt;
            double iv = st.GetProperty("interval_ms").GetInt32();
            if (!ReferenceEquals(focused, NumInterval) && Math.Abs(NumInterval.Value - iv) > 0.5)
                NumInterval.Value = iv;

            // 高级页
            string baseUrl = st.GetProperty("base").GetString() ?? "";
            if (!ReferenceEquals(focused, TxtBase) && TxtBase.Text != baseUrl)
                TxtBase.Text = baseUrl;
            if (!ReferenceEquals(focused, TxtBatchId) && TxtBatchId.Text != batch)
                TxtBatchId.Text = batch;
            string campus = st.GetProperty("campus").GetString() ?? "";
            if (!ReferenceEquals(focused, TxtCampus) && TxtCampus.Text != campus)
                TxtCampus.Text = campus;
            bool dbg = st.GetProperty("debug").GetBoolean();
            if (ChkDebug.IsChecked != dbg) ChkDebug.IsChecked = dbg;
        }
        finally { _suppress = false; }

        // 课程列表
        _allCourses.Clear();
        foreach (var c in st.GetProperty("courses").EnumerateArray())
        {
            _allCourses.Add(new CourseVm
            {
                Cid = c.GetProperty("cid").GetString() ?? "",
                Name = c.GetProperty("name").GetString() ?? "",
                Teacher = c.GetProperty("teacher").GetString() ?? "",
                Time = c.GetProperty("time").GetString() ?? "",
                Cap = c.GetProperty("cap").GetString() ?? "",
                Chosen = c.GetProperty("chosen").GetString() ?? "",
            });
        }
        ApplyCourseFilter();

        // 志愿队列
        var vols = st.GetProperty("volunteers").EnumerateArray().Select((v, i) => new VolunteerVm
        {
            Index = i,
            Cid = v.GetProperty("cid").GetString() ?? "",
            Label = v.GetProperty("label").GetString() ?? "",
            Type = v.GetProperty("type").GetString() ?? "",
        }).ToList();
        if (_vols.Count != vols.Count || vols.Where((v, i) => _vols[i].Cid != v.Cid).Any())
        {
            var keepSel = LstVols.SelectedIndex;
            _vols.Clear();
            foreach (var v in vols) _vols.Add(v);
            if (keepSel >= 0 && keepSel < _vols.Count) LstVols.SelectedIndex = keepSel;
        }
        else
        {
            for (int i = 0; i < vols.Count; i++) _vols[i].Index = vols[i].Index;
        }
    }

    private async Task PollLogs()
    {
        var j = await Backend.GetAsync($"/api/logs?after={_logNext}");
        if (j is null) return;
        var el = j.Value;
        foreach (var line in el.GetProperty("logs").EnumerateArray())
        {
            string text = $"[{line.GetProperty("time").GetString()}] {line.GetProperty("text").GetString()}";
            _logs.Add(new LogLine { Text = text, Color = LogColor.Of(line.GetProperty("text").GetString() ?? "") });
            _logNext = line.GetProperty("n").GetInt32();
        }
        while (_logs.Count > 800) _logs.RemoveAt(0);
        if (_logs.Count > 0 && _logAtBottom)
            LstLog.ScrollIntoView(_logs[^1]);
    }

    private static ScrollViewer? FindScroll(DependencyObject root)
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            var r = FindScroll(child);
            if (r != null) return r;
        }
        return null;
    }

    private void ApplyCourseFilter()
    {
        string kw = (TxtSearch.Text ?? "").Trim();
        IEnumerable<CourseVm> q = _allCourses;
        if (!string.IsNullOrEmpty(kw))
            q = q.Where(c => (c.Name + c.Teacher + c.Time + c.Cid).Contains(kw));
        var list = q.ToList();
        if (_courses.Count == list.Count && !_courses.Where((c, i) => c.Cid != list[i].Cid).Any())
            return;
        _courses.Clear();
        foreach (var c in list) _courses.Add(c);
    }

    // ---------------- 事件 ----------------
    private async void BtnBrowser_Click(object sender, RoutedEventArgs e)
        => await Backend.ActionAsync(new { action = "open_browser" });

    private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
        => await Backend.ActionAsync(new { action = "refresh" });

    private async void BtnFetch_Click(object sender, RoutedEventArgs e)
        => await Backend.ActionAsync(new { action = "fetch_courses" });

    private async void BtnCheckToken_Click(object sender, RoutedEventArgs e)
        => await Backend.ActionAsync(new { action = "check_token" });

    private async void CmbBatch_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || CmbBatch.SelectedIndex < 0) return;
        await Backend.ActionAsync(new { action = "select_batch", index = CmbBatch.SelectedIndex });
    }

    private async void CmbType_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || CmbType.SelectedIndex < 0) return;
        string code = ((string)CmbType.Items[CmbType.SelectedIndex]).Split(' ')[0];
        // 志愿模式自动跟随类别：通识课(XGKC)开，其余关
        await Backend.ActionAsync(new { action = "set", settings = new { ctype = code, volmode = code == "XGKC" } });
        await Backend.ActionAsync(new { action = "fetch_courses" });
    }

    private async void Setting_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        await Backend.ActionAsync(new
        {
            action = "set",
            settings = new
            {
                start_at = TxtStartAt.Text.Trim(),
                baseUrl = (string?)null,
                @base = TxtBase.Text.Trim(),
                batch = TxtBatchId.Text.Trim(),
                campus = TxtCampus.Text.Trim(),
            }
        });
    }

    private async void Setting_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        await Backend.ActionAsync(new { action = "set", settings = new { auth = TxtAuth.Password.Trim() } });
    }

    private async void NumInterval_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_suppress || double.IsNaN(args.NewValue)) return;
        await Backend.ActionAsync(new { action = "set", settings = new { interval_ms = (int)args.NewValue } });
    }

    private async void ChkDebug_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        await Backend.ActionAsync(new { action = "set", settings = new { debug = ChkDebug.IsChecked == true } });
    }

    private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e) => ApplyCourseFilter();

    private async void LstCourses_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (LstCourses.SelectedItem is CourseVm c)
            await Backend.ActionAsync(new { action = "add_volunteer", cid = c.Cid });
    }

    private async void LstCourses_ItemClick(object sender, ItemClickEventArgs e)
    {
        // 单击仅选中；双击加入志愿（DoubleTapped 处理）
    }

    private async void BtnVolUp_Click(object sender, RoutedEventArgs e)
    {
        if (LstVols.SelectedIndex > 0)
            await Backend.ActionAsync(new { action = "move_volunteer", index = LstVols.SelectedIndex, dir = -1 });
    }

    private async void BtnVolDown_Click(object sender, RoutedEventArgs e)
    {
        if (LstVols.SelectedIndex >= 0)
            await Backend.ActionAsync(new { action = "move_volunteer", index = LstVols.SelectedIndex, dir = 1 });
    }

    private async void BtnVolDel_Click(object sender, RoutedEventArgs e)
    {
        if (LstVols.SelectedIndex >= 0)
            await Backend.ActionAsync(new { action = "del_volunteer", index = LstVols.SelectedIndex });
    }

    private async void BtnVolClear_Click(object sender, RoutedEventArgs e)
        => await Backend.ActionAsync(new { action = "clear_volunteers" });

    private async void BtnGo_Click(object sender, RoutedEventArgs e)
    {
        bool grabbing = BtnGo.Content?.ToString()?.Contains("停止") == true;
        await Backend.ActionAsync(new { action = grabbing ? "stop_grab" : "start_grab" });
    }
}
