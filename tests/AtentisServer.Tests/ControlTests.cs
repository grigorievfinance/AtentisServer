using AtentisServer.Collection;
using AtentisServer.Configuration;
using AtentisServer.Contracts;
using AtentisServer.Control;
using AtentisServer.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AtentisServer.Tests;

public class CredentialStoreTests
{
    [Fact]
    public void Passwords_live_in_memory_and_versions_protect_against_stale_failures()
    {
        var c = new CredentialStore();
        var changes = 0;
        c.Changed += () => changes++;

        var v1 = c.Set("MICEX", "a");
        Assert.True(c.TryGet("micex", out var p, out var ver));       // имя слота регистронезависимо
        Assert.Equal(("a", v1), (p, ver));

        var v2 = c.Set("MICEX", "b");
        Assert.False(c.ClearIfVersion("MICEX", v1));                    // отказ по старому паролю не стирает новый
        Assert.True(c.Has("MICEX"));
        Assert.True(c.ClearIfVersion("MICEX", v2));
        Assert.False(c.Has("MICEX"));
        Assert.True(changes >= 3);
    }
}

public class SettingsValidatorTests
{
    private static SettingsDto Good() => new()
    {
        Database = new DatabaseSettingsDto { Host = "db", Port = 5432, Database = "atentis", Username = "u" },
        Slots =
        {
            new SlotSettingsDto { Name = "MICEX", Market = "SPOT", Enabled = true, Server = "s", Port = 7800, Login = "l" },
            new SlotSettingsDto { Name = "FORTS", Market = "FUT", Enabled = false }
        }
    };

    [Fact] public void Good_settings_pass_and_disabled_slots_are_not_checked() => Assert.Empty(SettingsValidator.Validate(Good()));

    [Fact]
    public void Messages_are_in_plain_russian_and_name_the_field()
    {
        var s = Good();
        s.Database.Host = "";
        s.Database.Port = 0;
        s.Slots[0].Login = "";
        s.Slots[0].Port = 99999;
        var errors = SettingsValidator.Validate(s);
        Assert.Contains(errors, e => e.Contains("адрес сервера PostgreSQL"));
        Assert.Contains(errors, e => e.Contains("Фондовый рынок") && e.Contains("логин"));
        Assert.Contains(errors, e => e.Contains("порт"));
    }

    [Fact]
    public void Passwords_are_required_for_enabled_markets_unless_the_service_already_has_one()
    {
        var s = Good();
        Assert.Single(SettingsValidator.ValidatePasswords(s, new Dictionary<string, string>(), _ => false));
        Assert.Empty(SettingsValidator.ValidatePasswords(s, new Dictionary<string, string> { ["MICEX"] = "x" }, _ => false));
        Assert.Empty(SettingsValidator.ValidatePasswords(s, new Dictionary<string, string>(), n => n == "MICEX"));
    }
}

public sealed class ControlTestsBase : IDisposable
{
    public readonly string Dir = Path.Combine(Path.GetTempPath(), "atentis-ctl-" + Guid.NewGuid().ToString("N"));
    public readonly string File;
    public readonly IConfigurationRoot Config;
    public readonly IOptionsMonitor<CollectorOptions> Options;
    public readonly LocalSettingsStore Store;
    public readonly CredentialStore Cred = new();
    public readonly CollectorState State = new();
    public readonly StubDbTester Tester = new();
    public readonly ControlCommandHandler Handler;

    public ControlTestsBase(string? initialJson = null)
    {
        Directory.CreateDirectory(Dir);
        File = AppConfiguration.LocalFilePath(Dir);
        var baseFile = Path.Combine(Dir, "appsettings.json");
        System.IO.File.WriteAllText(baseFile, """
            { "Collector": { "Slots": [
                { "Name": "MICEX", "Market": "SPOT", "Enabled": true, "Server": "", "Port": 0, "Login": "", "PublicKeyFile": "" },
                { "Name": "FORTS", "Market": "FUT",  "Enabled": true, "Server": "", "Port": 0, "Login": "", "PublicKeyFile": "" } ] } }
            """);
        if (initialJson is not null) System.IO.File.WriteAllText(File, initialJson);
        var b = new ConfigurationBuilder().AddJsonFile(baseFile);
        AppConfiguration.AddLocalSources(b, Dir, Array.Empty<string>());
        Config = b.Build();
        Options = new ConfigOptionsMonitor(Config);
        Store = new LocalSettingsStore(Config, Options, File);
        var status = new StatusService(State, Options, Cred, Store);
        Handler = new ControlCommandHandler(Store, Cred, status, Tester, NullLogger<ControlCommandHandler>.Instance);
    }

    public void Dispose() { try { Directory.Delete(Dir, true); } catch { } }

    public sealed class StubDbTester : IDatabaseTester
    {
        public (bool Ok, string Message) Result = (true, "ok");
        public DatabaseSettingsDto? Last;
        public string? LastExisting;
        public Task<(bool Ok, string Message)> TestAsync(DatabaseSettingsDto db, string? existing, CancellationToken ct)
        { Last = db; LastExisting = existing; return Task.FromResult(Result); }
    }

    /// <summary>Как IOptionsMonitor в службе: заново биндится после IConfigurationRoot.Reload().</summary>
    private sealed class ConfigOptionsMonitor : IOptionsMonitor<CollectorOptions>
    {
        private readonly IConfigurationRoot _cfg;
        public ConfigOptionsMonitor(IConfigurationRoot cfg) => _cfg = cfg;
        public CollectorOptions CurrentValue => _cfg.GetSection("Collector").Get<CollectorOptions>() ?? new CollectorOptions();
        public CollectorOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<CollectorOptions, string?> listener) => null;
    }

    public static SettingsDto Settings(string host = "db.local") => new()
    {
        Database = new DatabaseSettingsDto { Host = host, Port = 5432, Database = "atentis", Username = "atentis_app", Password = "db-secret;with=chars", SslMode = "Require" },
        Slots =
        {
            new SlotSettingsDto { Name = "MICEX", Market = "SPOT", Enabled = true, Server = "10.0.0.1", Port = 7800, Login = "user1" },
            new SlotSettingsDto { Name = "FORTS", Market = "FUT", Enabled = true, Server = "10.0.0.2", Port = 7801, Login = "user2" }
        }
    };
}

public class ControlCommandHandlerTests
{
    private static Task<ControlResponse> Send(ControlTestsBase t, ControlRequest r) => t.Handler.HandleAsync(r, default);

    [Fact]
    public async Task First_run_reports_nothing_configured_and_everything_waits()
    {
        using var t = new ControlTestsBase();
        var r = await Send(t, new ControlRequest { Cmd = Commands.Status });
        Assert.True(r.Ok);
        Assert.All(r.Status!.Slots, s => Assert.Equal(SlotStates.NotConfigured, s.State));
    }

    [Fact]
    public async Task Apply_saves_everything_except_atentis_passwords_and_remembers_passwords_only_in_memory()
    {
        using var t = new ControlTestsBase();
        var r = await Send(t, new ControlRequest
        {
            Cmd = Commands.Apply,
            Settings = ControlTestsBase.Settings(),
            Passwords = new() { ["MICEX"] = "atentis-pass-1", ["FORTS"] = "atentis-pass-2" }
        });

        Assert.True(r.Ok, r.Error);
        var file = await System.IO.File.ReadAllTextAsync(t.File);
        Assert.Contains("10.0.0.1", file);
        Assert.Contains("user2", file);
        Assert.Contains("db.local", file);
        Assert.Contains("db-secret", file);                      // пароль БД хранится в файле настроек (так задумано)
        Assert.DoesNotContain("atentis-pass", file);             // пароли Atentis в файле НЕТ
        Assert.True(t.Cred.TryGet("MICEX", out var p, out _) && p == "atentis-pass-1");
        Assert.All(r.Status!.Slots, s => { Assert.True(s.HasPassword); Assert.NotEqual(SlotStates.WaitingForPassword, s.State); });
    }

    [Fact]
    public async Task Database_password_is_never_sent_back_and_empty_password_keeps_the_old_one_for_the_same_target()
    {
        using var t = new ControlTestsBase();
        await Send(t, new ControlRequest { Cmd = Commands.Apply, Settings = ControlTestsBase.Settings() });

        var got = (await Send(t, new ControlRequest { Cmd = Commands.GetSettings })).Settings!;
        Assert.True(got.Database.HasPassword);
        Assert.True(string.IsNullOrEmpty(got.Database.Password));
        Assert.Equal("Require", got.Database.SslMode);
        Assert.Equal("db.local", got.Database.Host);

        got.Database.SslMode = "VerifyFull";                                // меняем только шифрование, пароль оставляем пустым
        var r = await Send(t, new ControlRequest { Cmd = Commands.Apply, Settings = got });
        Assert.True(r.Ok, r.Error);
        var b = new Npgsql.NpgsqlConnectionStringBuilder(t.Store.CurrentConnectionString!);
        Assert.Equal("db-secret;with=chars", b.Password);
        Assert.Equal(Npgsql.SslMode.VerifyFull, b.SslMode);
    }

    [Theory]
    [InlineData("host")]
    [InlineData("port")]
    [InlineData("database")]
    [InlineData("user")]
    public async Task Saved_database_password_cannot_be_redirected_to_another_target_without_retyping_it(string what)
    {
        // Панель доступна ВСЕМ пользователям сервера: никто не должен суметь отправить сохранённый пароль БД на чужой адрес.
        using var t = new ControlTestsBase();
        await Send(t, new ControlRequest { Cmd = Commands.Apply, Settings = ControlTestsBase.Settings() });
        var before = await System.IO.File.ReadAllTextAsync(t.File);

        var got = (await Send(t, new ControlRequest { Cmd = Commands.GetSettings })).Settings!;
        switch (what)
        {
            case "host": got.Database.Host = "evil.example.com"; break;
            case "port": got.Database.Port = 6543; break;
            case "database": got.Database.Database = "other"; break;
            default: got.Database.Username = "other_user"; break;
        }

        var apply = await Send(t, new ControlRequest { Cmd = Commands.Apply, Settings = got });
        Assert.False(apply.Ok);
        Assert.Contains(apply.Errors!, e => e.Contains("введите пароль базы данных заново"));
        Assert.Equal(before, await System.IO.File.ReadAllTextAsync(t.File));       // файл не изменён

        var test = await Send(t, new ControlRequest { Cmd = Commands.TestDatabase, Settings = new SettingsDto { Database = got.Database } });
        Assert.False(test.Ok);                                                      // и «проверить подключение» старый пароль на новый адрес не отправит
        Assert.Null(t.Tester.Last);

        got.Database.Password = "typed-again";                                     // с повторно введённым паролем смена разрешена
        Assert.True((await Send(t, new ControlRequest { Cmd = Commands.Apply, Settings = got })).Ok);
    }

    [Fact]
    public async Task Invalid_input_is_rejected_with_plain_messages_and_nothing_is_saved()
    {
        using var t = new ControlTestsBase();
        var s = ControlTestsBase.Settings();
        s.Slots[0].Login = "";
        var r = await Send(t, new ControlRequest { Cmd = Commands.Apply, Settings = s });
        Assert.False(r.Ok);
        Assert.Equal(ErrorCodes.Invalid, r.ErrorCode);
        Assert.Contains(r.Errors!, e => e.Contains("логин"));
        Assert.False(System.IO.File.Exists(t.File));
    }

    [Fact]
    public async Task Changing_server_without_a_new_password_forgets_the_old_password()
    {
        using var t = new ControlTestsBase();
        await Send(t, new ControlRequest { Cmd = Commands.Apply, Settings = ControlTestsBase.Settings(),
            Passwords = new() { ["MICEX"] = "p1", ["FORTS"] = "p2" } });

        var s = ControlTestsBase.Settings();
        s.Slots[0].Server = "10.9.9.9";                                   // сменили адрес MICEX
        await Send(t, new ControlRequest { Cmd = Commands.Apply, Settings = s });

        Assert.False(t.Cred.Has("MICEX"));                                // пароль относился к другому серверу
        Assert.True(t.Cred.Has("FORTS"));                                 // остальные не затронуты
    }

    [Fact]
    public async Task Disconnect_wipes_the_password_from_memory()
    {
        using var t = new ControlTestsBase();
        await Send(t, new ControlRequest { Cmd = Commands.Apply, Settings = ControlTestsBase.Settings(), Passwords = new() { ["MICEX"] = "p1" } });
        var r = await Send(t, new ControlRequest { Cmd = Commands.Disconnect, Slot = "MICEX" });
        Assert.True(r.Ok);
        Assert.False(t.Cred.Has("MICEX"));
        Assert.Equal(SlotStates.WaitingForPassword, r.Status!.Slots.Single(s => s.Name == "MICEX").State);
    }

    [Fact]
    public async Task Existing_hand_written_keys_and_comments_do_not_break_saving()
    {
        using var t = new ControlTestsBase("""
            // файл, отредактированный вручную
            { "Collector": { "BatchSize": 77, "Slots": [ { "Name": "MICEX", "Market": "SPOT", "RefreshPeriodMs": 2500, "Password": "old-plain" } ] },
              "Custom": { "Keep": "me" }, }
            """);
        var r = await Send(t, new ControlRequest { Cmd = Commands.Apply, Settings = ControlTestsBase.Settings() });
        Assert.True(r.Ok, r.Error);
        var file = await System.IO.File.ReadAllTextAsync(t.File);
        Assert.Contains("\"Keep\": \"me\"", file);                       // чужие ключи сохранены
        Assert.Contains("2500", file);                                   // расширенные параметры слота сохранены
        Assert.Contains("77", file);
        Assert.DoesNotContain("old-plain", file);                        // старый пароль из файла удалён
    }

    [Fact]
    public async Task Test_database_command_uses_the_stored_password_when_the_field_is_empty()
    {
        using var t = new ControlTestsBase();
        await Send(t, new ControlRequest { Cmd = Commands.Apply, Settings = ControlTestsBase.Settings() });
        t.Tester.Result = (false, "PostgreSQL отклонил имя пользователя или пароль.");

        var db = ControlTestsBase.Settings().Database;
        db.Password = null;
        var r = await Send(t, new ControlRequest { Cmd = Commands.TestDatabase, Settings = new SettingsDto { Database = db } });

        Assert.False(r.Ok);
        Assert.Contains("пароль", r.Error);
        Assert.Contains("db-secret", t.Tester.LastExisting);
    }

    [Fact]
    public async Task Unknown_command_and_unknown_slot_are_rejected()
    {
        using var t = new ControlTestsBase();
        Assert.Equal(ErrorCodes.Protocol, (await Send(t, new ControlRequest { Cmd = "format_c" })).ErrorCode);
        var r = await Send(t, new ControlRequest { Cmd = Commands.Apply, Passwords = new() { ["NOPE"] = "x" } });
        Assert.False(r.Ok);
    }
}

public class SlotStateResolverTests
{
    private static SlotOptions Cfg() => new() { Name = "MICEX", Market = "SPOT", Server = "h", Port = 1, Login = "l" };

    [Fact]
    public void States_for_each_stage_of_the_connection()
    {
        var cfg = Cfg();
        var rt = new SlotRuntime("MICEX", "SPOT");
        Assert.Equal(SlotStates.WaitingForPassword, SlotStateResolver.Resolve(cfg, rt, false).State);
        Assert.Equal(SlotStates.Connecting, SlotStateResolver.Resolve(cfg, rt, true).State);

        var src = new FakeSource("MICEX", "SPOT") { Connected = true };
        rt.Source = src;
        Assert.Equal(SlotStates.Connected, SlotStateResolver.Resolve(cfg, rt, true).State);

        rt.EverConnected = true;
        src.Connected = false;
        Assert.Equal(SlotStates.Reconnecting, SlotStateResolver.Resolve(cfg, rt, true).State);
    }
}
