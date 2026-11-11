using AtentisServer.Collection;
using AtentisServer.Configuration;
using AtentisServer.Storage;
using Microsoft.Extensions.Options;
using Xunit;

namespace AtentisServer.Tests;

public class DirtyTrackerTests
{
    [Fact]
    public void Repeated_marks_of_the_same_table_collapse_into_one()
    {
        var d = new DirtyTracker();
        for (var i = 0; i < 100; i++) d.Mark("MICEX", "trades");
        d.Mark("FORTS", "TRADES");
        var taken = d.TakeNow();
        Assert.Equal(2, taken.Count);
        Assert.Contains(("MICEX", "TRADES"), taken);
        Assert.Empty(d.TakeNow());
    }

    [Fact]
    public async Task TakeAsync_waits_for_a_mark()
    {
        var d = new DirtyTracker();
        var waiter = d.TakeAsync(CancellationToken.None);
        await Task.Delay(50);
        Assert.False(waiter.IsCompleted);
        d.Mark("S", "ORDERS");
        Assert.Equal(("S", "ORDERS"), Assert.Single(await waiter.WaitAsync(TimeSpan.FromSeconds(5))));
    }

    [Fact]
    public async Task Mark_during_processing_is_not_lost()
    {
        var d = new DirtyTracker();
        d.Mark("S", "TRADES");
        await d.TakeAsync(CancellationToken.None);
        d.Mark("S", "TRADES");                       // пришло обновление, пока обрабатывали предыдущее
        Assert.Single(await d.TakeAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
    }
}

public class QuoteFilterTests
{
    private static QuoteRecord Q(decimal bid, string code = "SBER") =>
        new("TQBR", code, DateTime.UtcNow, bid, 1, bid + 1, 1, null);

    [Fact]
    public void Writes_only_changes_and_respects_min_interval()
    {
        var now = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = new QuoteFilter(TimeSpan.FromSeconds(60), () => now);

        var first = f.Filter(new[] { Q(100) });
        Assert.Single(first);
        f.Commit(first);

        Assert.Empty(f.Filter(new[] { Q(100) }));     // не изменилась
        now = now.AddSeconds(10);
        Assert.Empty(f.Filter(new[] { Q(101) }));     // изменилась, но интервал не прошёл
        now = now.AddSeconds(60);
        Assert.Single(f.Filter(new[] { Q(101) }));    // интервал прошёл
    }

    [Fact]
    public void Uncommitted_quotes_are_offered_again_after_a_failed_write()
    {
        var f = new QuoteFilter(TimeSpan.Zero);
        Assert.Single(f.Filter(new[] { Q(100) }));
        Assert.Single(f.Filter(new[] { Q(100) }));    // Commit не вызывали (запись упала) -> повторяем
    }
}

public class OptionsValidatorTests
{
    private static CollectorOptions Valid() => new()
    {
        Slots =
        {
            new SlotOptions { Name = "MICEX", Market = "SPOT", Server = "h", Port = 1, Login = "l", Password = "p" },
            new SlotOptions { Name = "FORTS", Market = "FUT", Server = "h", Port = 2, Login = "l", Password = "p" }
        },
        Tables = { new TableOptions { Name = "TRADES" } }
    };

    private static ValidateOptionsResult Run(CollectorOptions o) => new CollectorOptionsValidator().Validate(null, o);

    [Fact] public void Valid_config_passes() => Assert.True(Run(Valid()).Succeeded);

    [Fact]
    public void Missing_password_fails_without_leaking_values()
    {
        var o = Valid(); o.Slots[0].Password = "";
        var r = Run(o);
        Assert.True(r.Failed);
        Assert.Contains("Password", string.Join(";", r.Failures!));
    }

    [Fact] public void Bad_market_fails() { var o = Valid(); o.Slots[1].Market = "XXX"; Assert.True(Run(o).Failed); }
    [Fact] public void Duplicate_slot_names_fail() { var o = Valid(); o.Slots[1].Name = "micex"; Assert.True(Run(o).Failed); }
    [Fact] public void No_tables_fails() { var o = Valid(); o.Tables.Clear(); Assert.True(Run(o).Failed); }
    [Fact] public void Disabled_slot_is_not_validated() { var o = Valid(); o.Slots[1].Enabled = false; o.Slots[1].Password = ""; Assert.True(Run(o).Succeeded); }
}

public class LogThrottleTests
{
    [Fact]
    public void Identical_messages_are_logged_once_per_interval_with_suppressed_count()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t = new AtentisServer.Sources.LogThrottle(TimeSpan.FromSeconds(60), () => now);

        Assert.True(t.ShouldLog("k", out _));
        for (var i = 0; i < 59; i++) { now = now.AddSeconds(1); Assert.False(t.ShouldLog("k", out _)); }
        Assert.True(t.ShouldLog("other", out _));              // другое сообщение - независимо
        now = now.AddSeconds(1);
        Assert.True(t.ShouldLog("k", out var suppressed));
        Assert.Equal(59, suppressed);
    }
}
