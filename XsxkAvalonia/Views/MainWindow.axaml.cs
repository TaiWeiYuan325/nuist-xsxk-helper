using System;
using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using XsxkAvalonia.ViewModels;

namespace XsxkAvalonia.Views;

public partial class MainWindow : Window
{
    private MainViewModel? _vm;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _vm = new MainViewModel();
        DataContext = _vm;
        _vm.Logs.CollectionChanged += LogsChanged;

        // 自绘标题栏：拖动移动 / 双击最大化 / 三个窗口按钮
        var titleBar = this.FindControl<Grid>("TitleBar");
        if (titleBar is not null)
        {
            titleBar.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                    BeginMoveDrag(e);
            };
            titleBar.DoubleTapped += (_, _) => ToggleMax();
        }
        var btnMin = this.FindControl<Button>("BtnMin");
        var btnMax = this.FindControl<Button>("BtnMax");
        var btnClose = this.FindControl<Button>("BtnClose");
        if (btnMin is not null) btnMin.Click += (_, _) => WindowState = WindowState.Minimized;
        if (btnMax is not null) btnMax.Click += (_, _) => ToggleMax();
        if (btnClose is not null) btnClose.Click += (_, _) => Close();
    }

    private void ToggleMax()
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void LogsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add) return;
        var sv = this.FindControl<ScrollViewer>("LogScroll");
        if (sv is null) return;
        // 仅当已在底部附近时自动跟随
        var atBottom = sv.Offset.Y >= sv.Extent.Height - sv.Viewport.Height - 40;
        if (atBottom) sv.ScrollToEnd();
    }

    private void CourseGrid_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_vm?.SelectedCourse is { } row)
            _vm.AddCourseCommand.Execute(row);
    }
}
