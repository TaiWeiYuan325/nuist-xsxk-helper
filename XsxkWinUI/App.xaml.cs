using System;
using System.IO;
using Microsoft.UI.Xaml;

namespace XsxkWinUI;

public partial class App : Application
{
    private Window? _window;
    private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "startup.log");

    public static void Trace(string msg)
    {
        try { File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {msg}\n"); } catch { }
    }

    public App()
    {
        Trace("App ctor begin");
        // 引导器由 WASDK 自动模块初始化完成（WindowsPackageType=None + 框架依赖模式）
        try
        {
            InitializeComponent();
            Trace("InitializeComponent ok");
        }
        catch (Exception e)
        {
            Trace("InitializeComponent FAIL: " + e);
            throw;
        }
        UnhandledException += (s, e) =>
        {
            Trace("UNHANDLED: " + e.Exception);
            try { e.Handled = true; } catch { }
        };
        Trace("App ctor end");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Trace("OnLaunched begin");
        try
        {
            _window = new MainWindow();
            Trace("MainWindow created");
            _window.Activate();
            Trace("window activated");
        }
        catch (Exception e)
        {
            Trace("OnLaunched FAIL: " + e);
            throw;
        }
    }
}
