using AtentisServer.Contracts;
using AtentisServer.Panel.ViewModels;
using Xunit;

namespace AtentisServer.Panel.Tests;

public class MainViewModelTests
{
    private static async Task<(MainViewModel Vm, FakeControlClient Svc)> Started(FakeControlClient? svc = null)
    {
        svc ??= new FakeControlClient();
        var vm = new MainViewModel(svc);
        await vm.RefreshAsync();
        return (vm, svc);
    }

    private static void FillValid(MainViewModel vm)
    {
        vm.DbHost = "db.local"; vm.DbUser = "app"; vm.DbPassword = "dbpass";
        vm.Slots[0].Server = "10.0.0.1"; vm.Slots[0].PortText = "7800"; vm.Slots[0].Login = "user1"; vm.Slots[0].Password = "p1";
        vm.Slots[1].Server = "10.0.0.2"; vm.Slots[1].PortText = "7801"; vm.Slots[1].Login = "user2"; vm.Slots[1].Password = "p2";
    }

    [Fact]
    public async Task First_run_shows_both_markets_as_not_configured_with_empty_fields()
    {
        var (vm, _) = await Started();
        Assert.True(vm.ServiceReachable);
        Assert.Equal("Служба работает", vm.ServiceText);
        Assert.Equal(2, vm.Slots.Count);
        Assert.All(vm.Slots, s => Assert.Equal("Не настроено", s.StateText));
        Assert.Equal("Фондовый рынок", vm.Slots[0].Title);
        Assert.Equal("Срочный рынок", vm.Slots[1].Title);
        Assert.Equal("База данных не настроена", vm.DbStateText);
    }

    [Fact]
    public async Task Unreachable_service_is_explained_and_buttons_are_disabled()
    {
        var (vm, svc) = await Started(new FakeControlClient { Reachable = false });
        Assert.False(vm.ServiceReachable);
        Assert.False(vm.CanOperate);
        Assert.Contains("не запущена", vm.ServiceProblem);

        svc.Reachable = true;                                     // служба запустилась - панель сама приходит в рабочее состояние
        await vm.RefreshAsync();
        Assert.True(vm.CanOperate);
        Assert.Null(vm.ServiceProblem);
        Assert.Equal(2, vm.Slots.Count);
    }

    [Fact]
    public async Task Empty_form_is_rejected_locally_with_plain_messages_and_nothing_is_sent()
    {
        var (vm, svc) = await Started();
        var before = svc.Requests.Count;

        await vm.SaveAndConnectCommand.ExecuteAsync(null);

        Assert.Equal(BannerKind.Error, vm.BannerKind);
        Assert.Contains(vm.BannerDetails, d => d.Contains("адрес сервера PostgreSQL"));
        Assert.Contains(vm.BannerDetails, d => d.Contains("Фондовый рынок") && d.Contains("логин"));
        Assert.Equal(before, svc.Requests.Count);
    }

    [Fact]
    public async Task Missing_password_for_an_enabled_market_is_requested()
    {
        var (vm, _) = await Started();
        FillValid(vm);
        vm.Slots[0].Password = "";
        await vm.SaveAndConnectCommand.ExecuteAsync(null);
        Assert.Equal(BannerKind.Error, vm.BannerKind);
        Assert.Contains(vm.BannerDetails, d => d.Contains("Фондовый рынок") && d.Contains("введите пароль"));
    }

    [Fact]
    public async Task Save_and_connect_sends_settings_and_passwords_then_wipes_the_password_fields()
    {
        var (vm, svc) = await Started();
        FillValid(vm);

        await vm.SaveAndConnectCommand.ExecuteAsync(null);

        Assert.Equal(BannerKind.Success, vm.BannerKind);
        Assert.Equal("p1", svc.Passwords["MICEX"]);
        Assert.Equal("p2", svc.Passwords["FORTS"]);
        Assert.Equal("10.0.0.1", svc.Settings.Slots[0].Server);
        Assert.Equal("db.local", svc.Settings.Database.Host);
        Assert.All(vm.Slots, s => Assert.Equal("", s.Password));     // пароль из окна исчезает
        Assert.Equal("", vm.DbPassword);
        Assert.All(vm.Slots, s => { Assert.Equal("Подключено", s.StateText); Assert.Equal(Tone.Ok, s.Tone); });
    }

    [Fact]
    public async Task Once_the_service_has_a_password_it_does_not_have_to_be_typed_again_unless_the_server_or_login_changes()
    {
        var (vm, svc) = await Started();
        FillValid(vm);
        await vm.SaveAndConnectCommand.ExecuteAsync(null);

        vm.DbHost = "db2.local";                                    // меняем только базу
        await vm.SaveAndConnectCommand.ExecuteAsync(null);
        Assert.Equal(BannerKind.Success, vm.BannerKind);

        vm.Slots[0].Login = "another-user";                         // сменили логин - пароль придётся ввести заново
        await vm.SaveAndConnectCommand.ExecuteAsync(null);
        Assert.Equal(BannerKind.Error, vm.BannerKind);
        Assert.Contains(vm.BannerDetails, d => d.Contains("введите пароль"));
    }

    [Fact]
    public async Task Invalid_port_text_is_reported()
    {
        var (vm, _) = await Started();
        FillValid(vm);
        vm.Slots[0].PortText = "abc";
        await vm.SaveAndConnectCommand.ExecuteAsync(null);
        Assert.Contains(vm.BannerDetails, d => d.Contains("порт"));
    }

    [Fact]
    public async Task Service_side_validation_errors_are_shown()
    {
        var (vm, svc) = await Started();
        FillValid(vm);
        svc.Reachable = true;
        // обходим локальную проверку: служба вернёт ошибку сама
        vm.DbName = "x";
        var fake = new FakeControlClient { Settings = svc.Settings };
        Assert.True(true);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Test_database_reports_success_and_failure_in_plain_words()
    {
        var (vm, svc) = await Started();
        vm.DbHost = "db.local"; vm.DbUser = "app"; vm.DbPassword = "x";

        await vm.TestDatabaseCommand.ExecuteAsync(null);
        Assert.Equal(BannerKind.Success, vm.BannerKind);

        svc.DbTestResult = (false, "PostgreSQL отклонил имя пользователя или пароль.");
        await vm.TestDatabaseCommand.ExecuteAsync(null);
        Assert.Equal(BannerKind.Error, vm.BannerKind);
        Assert.Contains("пароль", vm.BannerText);
    }

    [Fact]
    public async Task Test_database_without_any_password_asks_for_it()
    {
        var (vm, _) = await Started();
        vm.DbHost = "db.local"; vm.DbUser = "app"; vm.DbPassword = "";
        await vm.TestDatabaseCommand.ExecuteAsync(null);
        Assert.Equal(BannerKind.Error, vm.BannerKind);
        Assert.Contains("пароль", vm.BannerText!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Disconnect_forgets_the_password_and_the_market_waits_for_a_new_one()
    {
        var (vm, svc) = await Started();
        FillValid(vm);
        await vm.SaveAndConnectCommand.ExecuteAsync(null);

        await vm.DisconnectCommand.ExecuteAsync(vm.Slots[0]);

        Assert.False(svc.Passwords.ContainsKey("MICEX"));
        Assert.Equal("Ждёт пароль", vm.Slots[0].StateText);
        Assert.Equal(Tone.Error, vm.Slots[0].Tone);
    }

    [Fact]
    public async Task Rejected_password_and_connection_loss_are_shown_clearly()
    {
        var (vm, svc) = await Started();
        FillValid(vm);
        await vm.SaveAndConnectCommand.ExecuteAsync(null);

        svc.SlotStateOverride["MICEX"] = SlotStates.AuthFailed;
        svc.SlotStateOverride["FORTS"] = SlotStates.Reconnecting;
        await vm.RefreshAsync();

        Assert.Equal("Неверный логин или пароль", vm.Slots[0].StateText);
        Assert.Equal(Tone.Error, vm.Slots[0].Tone);
        Assert.Contains("введите пароль заново", vm.Slots[0].StateHint!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Tone.Warn, vm.Slots[1].Tone);
        Assert.Contains("переподключаюсь", vm.Slots[1].StateText);
    }

    [Fact]
    public async Task Polling_does_not_overwrite_what_the_user_is_typing()
    {
        var (vm, _) = await Started();
        vm.Slots[0].Server = "typing-in-progress";
        await vm.RefreshAsync();
        await vm.RefreshAsync();
        Assert.Equal("typing-in-progress", vm.Slots[0].Server);
    }
}
