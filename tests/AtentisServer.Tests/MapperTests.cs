using System.Data;
using AtentisServer.Mapping;
using AtentisServer.Storage;
using Xunit;

namespace AtentisServer.Tests;

public class TradeMapperTests
{
    private static TradeMapper Mapper() => new(new TimeConverter(TimeZoneInfo.CreateCustomTimeZone("MSK", TimeSpan.FromHours(3), "MSK", "MSK")));

    [Fact]
    public void Maps_trade_trims_strings_and_converts_time_to_utc()
    {
        var t = TestData.Trades();
        TestData.AddTrade(t, real: 42, code: "SBER", side: "S", qty: 7, price: 251.25);

        var result = Mapper().Map(t);

        var tr = Assert.Single(result.Items);
        Assert.Empty(result.Skipped);
        Assert.Equal(42, tr.RealTradeNo);
        Assert.Equal(1042, tr.TradeNo);
        Assert.Equal("SBER", tr.SecCode);
        Assert.Equal("TQBR", tr.SecBoard);
        Assert.Equal('S', tr.BuySell);
        Assert.Equal(7, tr.Quantity);
        Assert.Equal(251.25m, tr.Price);
        Assert.Equal("L01-00000F00", tr.Account);
        Assert.Equal(new DateTime(2026, 11, 11, 7, 0, 0, DateTimeKind.Utc), tr.TradeTimeUtc);   // 10:00 MSK = 07:00 UTC
        Assert.Equal(1.25m, tr.Commission);
    }

    [Theory]
    [InlineData(typeof(int))]
    [InlineData(typeof(long))]
    public void Quantity_may_be_int_or_long(Type qtyType)
    {
        var t = TestData.Trades(qtyType);
        TestData.AddTrade(t, 1, qty: 123456);
        Assert.Equal(123456, Assert.Single(Mapper().Map(t).Items).Quantity);
    }

    [Fact]
    public void Column_names_are_case_insensitive_like_in_legacy_code()
    {
        var t = new DataTable();
        foreach (var c in new[] { "REALTRADENO", "TRADETIME", "SECCODE", "BUYSELL", "QUANTITY", "PRICE" })
            t.Columns.Add(c, c switch { "REALTRADENO" => typeof(long), "TRADETIME" => typeof(DateTime), "QUANTITY" => typeof(int), "PRICE" => typeof(double), _ => typeof(string) });
        t.Rows.Add(5L, new DateTime(2026, 1, 2, 3, 4, 5), "GAZP", "B", 3, 100.0);

        var r = Mapper().Map(t);

        var tr = Assert.Single(r.Items);
        Assert.Equal(5, tr.RealTradeNo);
        Assert.Equal(5, tr.TradeNo);          // TradeNo отсутствует -> берём RealTradeNo
        Assert.Equal("", tr.Account);
        Assert.Equal(0m, tr.Value);
    }

    [Fact]
    public void Bad_rows_are_skipped_with_reason_and_do_not_break_the_batch()
    {
        var t = TestData.Trades();
        TestData.AddTrade(t, 1);
        TestData.AddTrade(t, 2, side: "X");              // плохое направление
        TestData.AddTrade(t, 3, code: "");               // нет тикера
        var nullQty = t.NewRow(); nullQty["RealTradeNo"] = 4L; nullQty["TradeTime"] = DateTime.Now;
        nullQty["SecCode"] = "A"; nullQty["BuySell"] = "B"; nullQty["Price"] = 1.0; t.Rows.Add(nullQty);   // Quantity = NULL
        TestData.AddTrade(t, 5);

        var r = Mapper().Map(t);

        Assert.Equal(new long[] { 1, 5 }, r.Items.Select(i => i.RealTradeNo).ToArray());
        Assert.Equal(1, r.Skipped["bad_buy_sell"]);
        Assert.Equal(1, r.Skipped["no_sec_code"]);
        Assert.Equal(1, r.Skipped["no_quantity"]);
    }

    [Fact]
    public void Idempotency_key_distinguishes_both_sides_and_accounts()
    {
        var t = TestData.Trades();
        TestData.AddTrade(t, 10, side: "B");
        TestData.AddTrade(t, 10, side: "S");
        TestData.AddTrade(t, 10, side: "B", account: "OTHER");
        var keys = Mapper().Map(t).Items.Select(i => i.Key).Distinct().Count();
        Assert.Equal(3, keys);
    }
}

public class RowHistoryMapperTests
{
    private static DataTable Orders(params (long no, string status)[] rows)
    {
        var t = new DataTable();
        t.Columns.Add("OrderNo", typeof(long)); t.Columns.Add("Status", typeof(string)); t.Columns.Add("Price", typeof(double));
        foreach (var (no, st) in rows) t.Rows.Add(no, st + " ", 10.5);
        return t;
    }

    [Fact]
    public void Key_comes_from_key_fields_and_hash_tracks_content()
    {
        var m = new RowHistoryMapper();
        var a = m.Map(Orders((1, "O")), new[] { "OrderNo" }, out var missing).Items.Single();
        var b = m.Map(Orders((1, "M")), new[] { "OrderNo" }, out _).Items.Single();
        var a2 = m.Map(Orders((1, "O")), new[] { "OrderNo" }, out _).Items.Single();

        Assert.False(missing);
        Assert.Equal("1", a.RowKey);
        Assert.Equal(a.RowKey, b.RowKey);
        Assert.NotEqual(a.RowHash, b.RowHash);   // статус изменился
        Assert.Equal(a.RowHash, a2.RowHash);     // то же содержимое -> тот же hash
        Assert.Contains("\"Status\":\"O\"", a.Json);   // значения обрезаются
    }

    [Fact]
    public void Missing_key_fields_are_reported_and_content_becomes_key()
    {
        var r = new RowHistoryMapper().Map(Orders((1, "O")), new[] { "NoSuchField" }, out var missing).Items.Single();
        Assert.True(missing);
        Assert.Equal(r.RowHash, r.RowKey);
    }

    [Fact]
    public void Special_values_do_not_break_json()
    {
        var t = new DataTable();
        t.Columns.Add("A", typeof(double)); t.Columns.Add("B", typeof(DateTime)); t.Columns.Add("C", typeof(string));
        t.Rows.Add(double.NaN, new DateTime(2026, 1, 1), DBNull.Value);
        var json = new RowHistoryMapper().Map(t, Array.Empty<string>(), out _).Items.Single().Json;
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("NaN", doc.RootElement.GetProperty("A").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, doc.RootElement.GetProperty("C").ValueKind);
    }
}

public class TimeConverterTests
{
    [Fact]
    public void Unknown_timezone_falls_back_to_moscow()
    {
        var c = new TimeConverter("Definitely/Not_A_Zone");
        Assert.Equal(new DateTime(2026, 11, 11, 7, 0, 0, DateTimeKind.Utc), c.ToUtc(new DateTime(2026, 11, 11, 10, 0, 0)));
    }

    [Fact]
    public void Utc_values_are_kept()
    {
        var c = new TimeConverter("Europe/Moscow");
        var utc = new DateTime(2026, 5, 1, 1, 2, 3, DateTimeKind.Utc);
        Assert.Equal(utc, c.ToUtc(utc));
    }
}

public class QuoteMapperTests
{
    [Fact]
    public void Maps_securities_and_uses_now_when_time_missing()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t = new DataTable();
        foreach (var c in new[] { "SecBoard", "SecCode" }) t.Columns.Add(c, typeof(string));
        foreach (var c in new[] { "Bid", "BidDepth", "Offer", "OfferDepth" }) t.Columns.Add(c, typeof(double));
        t.Rows.Add("TQBR", "SBER ", 100.1, 5.0, 100.2, 7.0);
        t.Rows.Add("TQBR", "", 1.0, 1.0, 1.0, 1.0);

        var r = new QuoteMapper(new TimeConverter("Europe/Moscow"), () => now).Map(t);

        var q = Assert.Single(r.Items);
        Assert.Equal("SBER", q.SecCode);
        Assert.Equal(100.1m, q.Bid);
        Assert.Equal(now, q.QuoteTimeUtc);
        Assert.Null(q.Last);
        Assert.Equal(1, r.Skipped["no_sec_code"]);
    }
}
