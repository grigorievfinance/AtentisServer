using AtentisServer.Collection;
using AtentisServer.Configuration;
using AtentisServer.Contracts;
using AtentisServer.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AtentisServer.Tests;

public class CollectorWorkerTests
{
    private static CollectorOptions Options(params TableOptions[] tables) => new()
    {
        BatchSize = 2,
        ReconnectAfterSeconds = 10,
        FullSyncIntervalSeconds = 0,
        Slots = { new SlotOptions { Name = "MICEX", Market = "SPOT", Server = "h", Port = 1, Login = "l" } },
        Tables = tables.Length > 0 ? tables.ToList() : new() { new TableOptions { Name = "TRADES" } }
    };

    private sealed record Rig(CollectorWorker W, FakeFactory F, InMemoryStore S, CollectorState St, List<int> Exits,
        CollectorOptions O, CredentialStore Cred, FakeDatabaseGate Db, TestOptionsMonitor<CollectorOptions> Monitor)
    {
        public SlotRuntime Rt => St.Slots[0];
        public SlotOptions Cfg => O.Slots[0];
    }

    private static Rig Make(CollectorOptions? o = null, bool withPassword = true)
    {
        o ??= Options();
        var f = new FakeFactory();
        var s = new InMemoryStore();
        var st = new CollectorState();
        var cred = new CredentialStore();
        var db = new FakeDatabaseGate();
        var exits = new List<int>();
        var monitor = new TestOptionsMonitor<CollectorOptions>(o);
        var w = new CollectorWorker(monitor, f, s, db, cred, st, NullLogger<CollectorWorker>.Instance, fatalExit: exits.Add);
        st.Slots.Add(new SlotRuntime("MICEX", "SPOT"));
        if (withPassword) cred.Set("MICEX", "secret-1");
        return new Rig(w, f, s, st, exits, o, cred, db, monitor);
    }

    [Fact]
    public async Task Trades_are_stored_once_even_if_the_same_snapshot_arrives_again()
    {
        var r = Make();
        await r.W.SuperviseSlotAsync(r.Cfg, r.Rt, default);
        var src = r.F.Created.Single();
        var t = TestData.Trades();
        for (var i = 1; i <= 5; i++) TestData.AddTrade(t, i);
        src.Tables["TRADES"] = t;

        await r.W.ProcessTableAsync(r.Rt, "TRADES", default);
        var callsAfterFirst = r.S.InsertTradeCalls;
        await r.W.ProcessTableAsync(r.Rt, "TRADES", default);     // тот же снимок

        Assert.Equal(5, r.S.Trades.Count);
        Assert.Equal(3, callsAfterFirst);                          // 5 строк / батч 2 = 3 вставки
        Assert.Equal(callsAfterFirst, r.S.InsertTradeCalls);       // повторный снимок ничего не отправил в БД
    }

    [Fact]
    public async Task New_trades_in_a_later_snapshot_are_added_incrementally()
    {
        var r = Make();
        await r.W.SuperviseSlotAsync(r.Cfg, r.Rt, default);
        var src = r.F.Created.Single();
        var t = TestData.Trades();
        TestData.AddTrade(t, 1); src.Tables["TRADES"] = t;
        await r.W.ProcessTableAsync(r.Rt, "TRADES", default);

        TestData.AddTrade(t, 2); src.Tables["TRADES"] = t;
        await r.W.ProcessTableAsync(r.Rt, "TRADES", default);

        Assert.Equal(new long[] { 1, 2 }, r.S.Trades.Keys.Select(k => k.Real).OrderBy(x => x).ToArray());
    }

    [Fact]
    public async Task Failed_write_is_not_marked_as_seen_so_the_next_snapshot_retries_it()
    {
        var r = Make();
        await r.W.SuperviseSlotAsync(r.Cfg, r.Rt, default);
        var t = TestData.Trades(); TestData.AddTrade(t, 1);
        r.F.Created.Single().Tables["TRADES"] = t;

        r.S.FailNextInserts = 1;
        await Assert.ThrowsAsync<Npgsql.NpgsqlException>(() => r.W.ProcessTableAsync(r.Rt, "TRADES", default));
        Assert.Empty(r.S.Trades);

        await r.W.ProcessTableAsync(r.Rt, "TRADES", default);      // БД «вернулась»
        Assert.Single(r.S.Trades);
    }

    [Fact]
    public async Task Orders_are_history_rows_written_only_when_content_changes()
    {
        var r = Make(Options(new TableOptions { Name = "ORDERS", KeyFields = { "OrderNo" } }));
        await r.W.SuperviseSlotAsync(r.Cfg, r.Rt, default);
        var src = r.F.Created.Single();
        var t = new System.Data.DataTable();
        t.Columns.Add("OrderNo", typeof(long)); t.Columns.Add("Status", typeof(string));
        t.Rows.Add(1L, "O"); src.Tables["ORDERS"] = t;

        await r.W.ProcessTableAsync(r.Rt, "ORDERS", default);
        await r.W.ProcessTableAsync(r.Rt, "ORDERS", default);       // без изменений
        Assert.Single(r.S.Rows);

        t.Rows[0]["Status"] = "M"; src.Tables["ORDERS"] = t;        // заявка исполнена
        await r.W.ProcessTableAsync(r.Rt, "ORDERS", default);
        Assert.Equal(2, r.S.Rows.Count);
    }

    [Fact]
    public async Task Tables_that_are_not_configured_are_ignored()
    {
        var r = Make();
        await r.W.SuperviseSlotAsync(r.Cfg, r.Rt, default);
        var t = new System.Data.DataTable(); t.Columns.Add("X"); t.Rows.Add("1");
        r.F.Created.Single().Tables["ALL_TRADES"] = t;
        await r.W.ProcessTableAsync(r.Rt, "ALL_TRADES", default);
        Assert.Empty(r.S.Rows);
        Assert.Empty(r.S.Trades);
    }

    // ---------------------------------------------------------------- пароли и переподключение

    [Fact]
    public async Task Slot_without_a_password_is_not_started_until_the_user_enters_one()
    {
        var r = Make(withPassword: false);

        await r.W.SuperviseSlotAsync(r.Cfg, r.Rt, default);
        Assert.Empty(r.F.Created);
        Assert.Equal(SlotStates.WaitingForPassword, SlotStateResolver.Resolve(r.Cfg, r.Rt, r.Cred.Has("MICEX")).State);

        r.Cred.Set("MICEX", "typed-by-user");
        await r.W.SuperviseSlotAsync(r.Cfg, r.Rt, default);
        var src = Assert.Single(r.F.Created);
        Assert.Equal("typed-by-user", src.PasswordUsed);
    }

    [Fact]
    public async Task Slot_that_stays_disconnected_is_recreated_with_the_same_password_without_user_involvement()
    {
        var r = Make();
        await r.W.SuperviseSlotAsync(r.Cfg, r.Rt, default);
        var first = r.F.Created.Single();

        first.Connected = false;
        await r.W.SuperviseSlotAsync(r.Cfg, r.Rt, default);              // начало простоя - ещё ждём
        Assert.Single(r.F.Created);

        r.Rt.DisconnectedSinceUtc = DateTime.UtcNow.AddSeconds(-11);     // простой дольше ReconnectAfterSeconds
        await r.W.SuperviseSlotAsync(r.Cfg, r.Rt, default);

        Assert.Equal(2, r.F.Created.Count);
        Assert.True(first.Disposed);
        Assert.Equal("secret-1", r.F.Created[1].PasswordUsed);           // пароль взят из памяти, вводить заново не нужно
        Assert.Equal(1, Interlocked.Read(ref r.Rt.Reconnects));
        Assert.Empty(r.Exits);                                           // процесс не перезапускается
    }

    [Fact]
    public async Task Connection_loss_and_recovery_cycle_repeats_indefinitely()
    {
        var r = Make();
        for (var cycle = 1; cycle <= 5; cycle++)
        {
            await r.W.SuperviseSlotAsync(r.Cfg, r.Rt, default);
            var src = r.F.Created.Last();
            src.Connected = true;
            await r.W.SuperviseSlotAsync(r.Cfg, r.Rt, default);                  // подключён
            Assert.True(r.Rt.IsConnected);

            src.Connected = false;                                                // обрыв
            await r.W.SuperviseSlotAsync(r.Cfg, r.Rt, default);
            r.Rt.DisconnectedSinceUtc = DateTime.UtcNow.AddSeconds(-30);
            await r.W.SuperviseSlotAsync(r.Cfg, r.Rt, default);                  // пересоздан
            Assert.Equal(cycle + 1, r.F.Created.Count);
        }
        Assert.Empty(r.Exits);
        Assert.True(r.Cred.Has("MICEX"));
    }

    [Fact]
    public async Task Start_failures_back_off_but_never_terminate_the_process()
    {
        var r = Make();
        r.F.Configure = s => s.StartFailure = new InvalidOperationException("no route to host");

        for (var i = 0; i < 25; i++)
        {
            r.Rt.NextAttemptUtc = null;                                          // имитируем истечение паузы
            await r.W.SuperviseSlotAsync(r.Cfg, r.Rt, default);
        }

        Assert.Equal(25, r.F.Created.Count);
        Assert.All(r.F.Created, s => Assert.True(s.Disposed));
        Assert.Empty(r.Exits);                                                   // пароли только в памяти: выходить нельзя
        Assert.True(r.Cred.Has("MICEX"));
        Assert.Null(r.Rt.Source);
    }

    [Fact]
    public async Task Backoff_prevents_hammering_the_server()
    {
        var r = Make();
        r.F.Configure = s => s.StartFailure = new InvalidOperationException("down");
        await r.W.SuperviseSlotAsync(r.Cfg, r.Rt, default);
        await r.W.SuperviseSlotAsync(r.Cfg, r.Rt, default);      // сразу же - пауза ещё не прошла
        Assert.Single(r.F.Created);
    }

    [Fact]
    public async Task Rejected_password_stops_reconnecting_and_asks_for_a_new_one()
    {
        var r = Make();
        await r.W.SuperviseSlotAsync(r.Cfg, r.Rt, default);
        var src = r.F.Created.Single();

        src.RaiseAuthFailed("Atentis отклонил логин или пароль.");               // из потока библиотеки

        Assert.False(r.Cred.Has("MICEX"));                                        // пароль забыт
        await r.W.SuperviseSlotAsync(r.Cfg, r.Rt, default);
        Assert.True(src.Disposed);                                                // подключение закрыто (иначе можно заблокировать аккаунт)
        Assert.Null(r.Rt.Source);
        for (var i = 0; i < 5; i++) await r.W.SuperviseSlotAsync(r.Cfg, r.Rt, default);
        Assert.Single(r.F.Created);                                               // повторных попыток нет
        Assert.Equal(SlotStates.AuthFailed, SlotStateResolver.Resolve(r.Cfg, r.Rt, false).State);

        r.Cred.Set("MICEX", "corrected");                                         // пользователь ввёл верный пароль
        await r.W.SuperviseSlotAsync(r.Cfg, r.Rt, default);
        Assert.Equal(2, r.F.Created.Count);
        Assert.Equal("corrected", r.F.Created[1].PasswordUsed);
    }

    [Fact]
    public async Task Late_rejection_of_an_old_password_does_not_erase_a_newly_entered_one()
    {
        var r = Make();
        await r.W.SuperviseSlotAsync(r.Cfg, r.Rt, default);
        var old = r.F.Created.Single();

        r.Cred.Set("MICEX", "fresh");                                             // пользователь успел ввести новый
        old.RaiseAuthFailed();                                                    // а старое подключение только сейчас получило отказ

        Assert.True(r.Cred.Has("MICEX"));
        await r.W.SuperviseSlotAsync(r.Cfg, r.Rt, default);
        Assert.Equal("fresh", r.F.Created.Last().PasswordUsed);
    }

    [Fact]
    public async Task Changing_server_or_login_reconnects_and_disabling_stops_the_slot()
    {
        var r = Make();
        await r.W.SuperviseSlotAsync(r.Cfg, r.Rt, default);
        var first = r.F.Created.Single();

        r.Cfg.Server = "other-host";
        await r.W.SuperviseSlotAsync(r.Cfg, r.Rt, default);
        Assert.True(first.Disposed);
        Assert.Equal(2, r.F.Created.Count);

        r.Cfg.Enabled = false;
        await r.W.SuperviseSlotAsync(r.Cfg, r.Rt, default);
        Assert.Null(r.Rt.Source);
        Assert.Equal(SlotStates.Disabled, SlotStateResolver.Resolve(r.Cfg, r.Rt, true).State);
    }

    [Fact]
    public async Task Unconfigured_slot_is_not_started()
    {
        var o = Options();
        o.Slots[0].Server = "";
        var r = Make(o);
        await r.W.SuperviseSlotAsync(r.Cfg, r.Rt, default);
        Assert.Empty(r.F.Created);
        Assert.Equal(SlotStates.NotConfigured, SlotStateResolver.Resolve(r.Cfg, r.Rt, true).State);
    }

    // ---------------------------------------------------------------- база данных

    [Fact]
    public async Task Database_not_configured_then_configured_becomes_ready_without_restart()
    {
        var r = Make();
        r.Db.IsConfigured = false;
        await r.W.EnsureDatabaseAsync(default);
        Assert.False(r.St.DatabaseReady);
        Assert.False(r.St.DatabaseConfigured);

        r.Db.IsConfigured = true;                                                 // пользователь сохранил настройки БД
        r.Db.Generation++;
        await r.W.EnsureDatabaseAsync(default);
        Assert.True(r.St.DatabaseReady);
        Assert.True(r.St.DatabaseUp);
    }

    [Fact]
    public async Task Database_failure_is_reported_in_plain_language_and_retried()
    {
        var r = Make();
        r.Db.Failure = new Npgsql.PostgresException("x", "FATAL", "FATAL", "28P01");
        await r.W.EnsureDatabaseAsync(default);
        Assert.False(r.St.DatabaseReady);
        Assert.Contains("пароль", r.St.DatabaseMessage, StringComparison.OrdinalIgnoreCase);

        r.Db.Failure = null;
        r.Db.Generation++;                                                         // пользователь исправил пароль -> новая попытка сразу
        await r.W.EnsureDatabaseAsync(default);
        Assert.True(r.St.DatabaseReady);
        Assert.Null(r.St.DatabaseMessage);
    }

    [Fact]
    public async Task Changing_database_settings_triggers_a_new_schema_check()
    {
        var r = Make();
        await r.W.EnsureDatabaseAsync(default);
        var calls = r.Db.Calls;
        await r.W.EnsureDatabaseAsync(default);
        Assert.Equal(calls, r.Db.Calls);                                          // ничего не менялось - повторной проверки нет

        r.Db.Generation++;
        await r.W.EnsureDatabaseAsync(default);
        Assert.Equal(calls + 1, r.Db.Calls);
    }

    [Fact]
    public async Task Rising_connection_edge_requests_full_sync_via_dirty_tracker()
    {
        var r = Make();
        await r.W.SuperviseSlotAsync(r.Cfg, r.Rt, default);                        // создан
        var t = TestData.Trades(); TestData.AddTrade(t, 7);
        r.F.Created.Single().Tables["TRADES"] = t;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await r.W.StartAsync(cts.Token);                                           // запускаем реальные циклы
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (r.S.Trades.Count == 0 && DateTime.UtcNow < deadline) await Task.Delay(50);
        await r.W.StopAsync(CancellationToken.None);

        Assert.Single(r.S.Trades);
    }
}
