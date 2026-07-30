using System;
using Avalonia;

namespace XsxkAvalonia;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // 便携 Chromium：exe 旁存在 pw-browsers 时优先使用
        var dir = AppContext.BaseDirectory;
        var portable = System.IO.Path.Combine(dir, "pw-browsers");
        if (System.IO.Directory.Exists(portable))
            Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", portable);

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
