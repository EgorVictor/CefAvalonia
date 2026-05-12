using Avalonia;
using Avalonia.Platform;
using System;

namespace MemoryTest;

class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new Win32PlatformOptions
            {
                RenderingMode = new[] { Win32RenderingMode.Software },
            })
            .WithInterFont()
            .LogToTrace();
}
