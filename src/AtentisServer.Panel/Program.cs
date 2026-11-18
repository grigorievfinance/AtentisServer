using Avalonia;

namespace AtentisServer.Panel;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Одно окно на пользователя: повторный запуск ничего не делает (первое окно уже открыто).
        using var mutex = new Mutex(true, "Local\\AtentisServerPanel", out var isFirst);
        if (!isFirst) return;

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // Программный рендеринг: надёжно работает на серверах без видеокарты и по RDP.
            .With(new Win32PlatformOptions { RenderingMode = new[] { Win32RenderingMode.Software } })
            .WithInterFont()
            .LogToTrace();
}
