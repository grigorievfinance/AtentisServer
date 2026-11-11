using AtentisServer.Collection;
using AtentisServer.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AtentisServer.Tests;

public class CollectorWorkerTests
{
    private static CollectorOptions Options(params TableOptions[] tables) => new()
    {
        BatchSize = 2,
        ReconnectAfterSeconds = 10,
        MaxConsecutiveReconnectFailures = 3,
        FullSyncIntervalSeconds = 0,
        Slots = { new SlotOptions { Name = "MICEX", Market = "SPOT", Server = "h", Port = 1, Login = "l", Password = "p" } },
        Tables = tables.Length > 0 ? tables.ToList() : new() { new TableOptions { Name = "TRADES" } }
    };

    private static (CollectorWorker w, FakeFactory f, InMemoryStore s, CollectorState st, List<int> exits, CollectorOptions o) Make(CollectorOptions? o = null)
    {
        o ??= Options();
        var f = new FakeFactory();
        var s = new InMemoryStore();
        var st = new CollectorState();
        var exits = new List<int>();
        var w = new CollectorWorker(Microsoft.Extensions.Options.Options.Create(o), Microsoft.Extensions.Options.Options.Create(new DatabaseOptions()),
            f, s, st, NullLogger<CollectorWorker>.Instance, migrator: null, fatalExit: exits.Add);
        st.Slots.Add(new SlotRuntime("MICEX", "SPOT"));
        return (w, f, s, st, exits, o);
    }

    [Fact]
    public async Task Trades_are_stored_once_even_if_the_same_snapshot_arrives_again()
    {
        var (w, f, store, st, _, o) = Make();
        await w.SuperviseSlotAsync(o.Slots[0], st.Slots[0], default);
        var src = f.Created.Single();
        var t = TestData.Trades();
        for (var i = 1; i <= 5; i++) TestData.AddTrade(t, i);
        src.Tables["TRADES"] = t;

        await w.ProcessTableAsync(st.Slots[0], "TRADES", default);
        var callsAfterFirst = store.InsertTradeCalls;
        await w.ProcessTableAsync(st.Slots[0], "TRADES", default);     // тот же снимок

        Assert.Equal(5, store.Trades.Count);
        Assert.Equal(3, callsAfterFirst);                                // 5 строк / батч 2 = 3 вставки
        Assert.Equal(callsAfterFirst, store.InsertTradeCalls);           // повторный снимок ничего не отправил в БД
    }

    [Fact]
    public async Task New_trades_in_a_later_snapshot_are_added_incrementally()
    {
        var (w, f, store, st, _, o) = Make();
        await w.SuperviseSlotAsync(o.Slots[0], st.Slots[0], default);
        var src = f.Created.Single();
        var t = TestData.Trades();
        TestData.AddTrade(t, 1); src.Tables["TRADES"] = t;
        await w.ProcessTableAsync(st.Slots[0], "TRADES", default);

        TestData.AddTrade(t, 2); src.Tables["TRADES"] = t;
        await w.ProcessTableAsync(st.Slots[0], "TRADES", default);

        Assert.Equal(new long[] { 1, 2 }, store.Trades.Keys.Select(k => k.Real).OrderBy(x => x).ToArray());
    }

    [Fact]
    public async Task Failed_write_is_not_marked_as_seen_so_the_next_snapshot_retries_it()
    {
        var (w, f, store, st, _, o) = Make();
        await w.SuperviseSlotAsync(o.Slots[0], st.Slots[0], default);
        var t = TestData.Trades(); TestData.AddTrade(t, 1);
        f.Created.Single().Tables["TRADES"] = t;

        store.FailNextInserts = 1;
        await Assert.ThrowsAsync<Npgsql.NpgsqlException>(() => w.ProcessTableAsync(st.Slots[0], "TRADES", default));
        Assert.Empty(store.Trades);

        await w.ProcessTableAsync(st.Slots[0], "TRADES", default);      // БД «вернулась»
        Assert.Single(store.Trades);
    }

    [Fact]
    public async Task Orders_are_history_rows_written_only_when_content_changes()
    {
        var (w, f, store, st, _, o) = Make(Options(new TableOptions { Name = "ORDERS", KeyFields = { "OrderNo" } }));
        await w.SuperviseSlotAsync(o.Slots[0], st.Slots[0], default);
        var src = f.Created.Single();
        var t = new System.Data.DataTable();
        t.Columns.Add("OrderNo", typeof(long)); t.Columns.Add("Status", typeof(string));
        t.Rows.Add(1L, "O"); src.Tables["ORDERS"] = t;

        await w.ProcessTableAsync(st.Slots[0], "ORDERS", default);
        await w.ProcessTableAsync(st.Slots[0], "ORDERS", default);       // без изменений
        Assert.Single(store.Rows);

        t.Rows[0]["Status"] = "M"; src.Tables["ORDERS"] = t;             // заявка исполнена
        await w.ProcessTableAsync(st.Slots[0], "ORDERS", default);
        Assert.Equal(2, store.Rows.Count);
    }

    [Fact]
    public async Task Tables_that_are_not_configured_are_ignored()
    {
        var (w, f, store, st, _, o) = Make();
        await w.SuperviseSlotAsync(o.Slots[0], st.Slots[0], default);
        var t = new System.Data.DataTable(); t.Columns.Add("X"); t.Rows.Add("1");
        f.Created.Single().Tables["ALL_TRADES"] = t;
        await w.ProcessTableAsync(st.Slots[0], "ALL_TRADES", default);
        Assert.Empty(store.Rows);
        Assert.Empty(store.Trades);
    }

    [Fact]
    public async Task Slot_that_stays_disconnected_is_recreated_and_counted()
    {
        var (w, f, _, st, _, o) = Make();
        var rt = st.Slots[0];
        await w.SuperviseSlotAsync(o.Slots[0], rt, default);
        var first = f.Created.Single();

        first.Connected = false;
        await w.SuperviseSlotAsync(o.Slots[0], rt, default);              // начало простоя - ещё ждём
        Assert.Single(f.Created);

        rt.DisconnectedSinceUtc = DateTime.UtcNow.AddSeconds(-11);        // простой дольше ReconnectAfterSeconds
        await w.SuperviseSlotAsync(o.Slots[0], rt, default);

        Assert.Equal(2, f.Created.Count);
        Assert.True(first.Disposed);
        Assert.Equal(1, Interlocked.Read(ref rt.Reconnects));
    }

    [Fact]
    public async Task Repeated_start_failures_back_off_and_finally_request_process_restart()
    {
        var (w, f, _, st, exits, o) = Make();
        f.Configure = s => s.StartFailure = new InvalidOperationException("no route to host");
        var rt = st.Slots[0];

        for (var i = 0; i < 3; i++)
        {
            rt.NextAttemptUtc = null;                                      // имитируем истечение паузы
            await w.SuperviseSlotAsync(o.Slots[0], rt, default);
        }

        Assert.Equal(3, f.Created.Count);
        Assert.All(f.Created, s => Assert.True(s.Disposed));
        Assert.Equal(new[] { 1 }, exits);                                  // 3 = MaxConsecutiveReconnectFailures
        Assert.Null(rt.Source);
    }

    [Fact]
    public async Task Backoff_prevents_hammering_the_server()
    {
        var (w, f, _, st, _, o) = Make();
        f.Configure = s => s.StartFailure = new InvalidOperationException("down");
        await w.SuperviseSlotAsync(o.Slots[0], st.Slots[0], default);
        await w.SuperviseSlotAsync(o.Slots[0], st.Slots[0], default);      // сразу же - пауза ещё не прошла
        Assert.Single(f.Created);
    }

    [Fact]
    public async Task Rising_connection_edge_requests_full_sync_via_dirty_tracker()
    {
        var (w, f, store, st, _, o) = Make();
        await w.SuperviseSlotAsync(o.Slots[0], st.Slots[0], default);     // создан
        var t = TestData.Trades(); TestData.AddTrade(t, 7);
        f.Created.Single().Tables["TRADES"] = t;
        await w.SuperviseSlotAsync(o.Slots[0], st.Slots[0], default);     // подключён -> грязные таблицы

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = w.StartAsync(cts.Token);                                 // запускаем реальные циклы
        await run;
        // Ждём, пока ProcessLoop заберёт таблицу и запишет сделку.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (store.Trades.Count == 0 && DateTime.UtcNow < deadline) await Task.Delay(50);
        await w.StopAsync(CancellationToken.None);

        Assert.Single(store.Trades);
    }
}
