using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AtentisServer.Contracts;
using AtentisServer.Panel;
using AtentisServer.Panel.ViewModels;
using AtentisServer.Panel.Views;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(AtentisServer.Panel.Tests.TestAppBuilder))]

namespace AtentisServer.Panel.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>Окно целиком в памяти: проверяем, что оно строится и рисуется, и сохраняем снимки (PANEL_SCREENSHOT_DIR) для визуальной проверки.</summary>
public class ScreenshotTests
{
    private static string? Dir => Environment.GetEnvironmentVariable("PANEL_SCREENSHOT_DIR");

    private static async Task<MainWindow> Open(FakeControlClient svc, Action<MainViewModel>? prepare = null, int width = 1020, int height = 860)
    {
        var vm = new MainViewModel(svc);
        await vm.RefreshAsync();
        prepare?.Invoke(vm);
        var w = new MainWindow { DataContext = vm, Width = width, Height = height };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        return w;
    }

    private static void Snap(Window w, string name)
    {
        Dispatcher.UIThread.RunJobs();
        var frame = w.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Assert.True(frame!.PixelSize.Width > 800);
        if (Dir is null) return;
        Directory.CreateDirectory(Dir);
        frame.Save(Path.Combine(Dir, name + ".png"));
    }

    private static void Fill(MainViewModel vm)
    {
        vm.DbHost = "db.example.local"; vm.DbUser = "atentis_app";
        vm.Slots[0].Server = "10.0.0.1"; vm.Slots[0].PortText = "7800"; vm.Slots[0].Login = "ivanov";
        vm.Slots[1].Server = "10.0.0.2"; vm.Slots[1].PortText = "7800"; vm.Slots[1].Login = "ivanov-f";
    }

    [AvaloniaFact]
    public async Task First_launch()
    {
        var w = await Open(new FakeControlClient());
        Assert.True(w.GetVisualDescendants().OfType<TextBox>().Count() >= 11);   // 4 поля × 2 рынка + 6 полей БД = 14
        Snap(w, "1-first-launch");
    }

    [AvaloniaFact]
    public async Task Waiting_for_passwords_after_a_reboot()
    {
        var svc = new FakeControlClient { DatabaseReady = true };
        svc.Settings.Database = new DatabaseSettingsDto { Host = "db.example.local", Port = 5432, Database = "atentis", Username = "atentis_app", HasPassword = true, SslMode = "Require" };
        foreach (var s in svc.Settings.Slots) { s.Server = "10.0.0.1"; s.Port = 7800; s.Login = "ivanov"; }
        var w = await Open(svc);
        Snap(w, "2-waiting-for-passwords");
    }

    [AvaloniaFact]
    public async Task Everything_connected()
    {
        var svc = new FakeControlClient();
        var w = await Open(svc, vm =>
        {
            Fill(vm);
            vm.Slots[0].Password = "p1"; vm.Slots[1].Password = "p2"; vm.DbPassword = "x";
        });
        var vm = (MainViewModel)w.DataContext!;
        await vm.SaveAndConnectCommand.ExecuteAsync(null);
        Snap(w, "3-connected");
    }

    [AvaloniaFact]
    public async Task Problems_are_shown_in_plain_language()
    {
        var svc = new FakeControlClient { DatabaseReady = false, DatabaseMessage = "PostgreSQL отклонил имя пользователя или пароль." };
        svc.Settings.Database = new DatabaseSettingsDto { Host = "db.example.local", Port = 5432, Database = "atentis", Username = "atentis_app", HasPassword = true };
        foreach (var s in svc.Settings.Slots) { s.Server = "10.0.0.1"; s.Port = 7800; s.Login = "ivanov"; }
        svc.Passwords["MICEX"] = "x"; svc.Passwords["FORTS"] = "y";
        svc.SlotStateOverride["MICEX"] = SlotStates.Reconnecting;
        svc.SlotStateOverride["FORTS"] = SlotStates.AuthFailed;
        svc.Passwords.Remove("FORTS");
        var w = await Open(svc);
        await ((MainViewModel)w.DataContext!).SaveAndConnectCommand.ExecuteAsync(null);   // покажет «введите пароль»
        Snap(w, "4-problems");
    }

    [AvaloniaFact]
    public async Task Service_not_running()
    {
        var w = await Open(new FakeControlClient { Reachable = false });
        Snap(w, "5-service-down");
    }

    [AvaloniaFact]
    public async Task Small_window_still_usable()
    {
        var w = await Open(new FakeControlClient(), width: 900, height: 640);
        Snap(w, "6-small");
    }
}
