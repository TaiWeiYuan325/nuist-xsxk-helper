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
    }

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
