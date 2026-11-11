using AtentisServer.Configuration;
using AtentisServer.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace AtentisServer.Tests;

/// <summary>
/// Интеграционные тесты с настоящим PostgreSQL. Запускаются, только если задана переменная окружения
/// ATENTIS_TEST_PG (строка подключения к ПУСТОЙ тестовой БД), иначе помечаются как пропущенные.
/// Пример: ATENTIS_TEST_PG="Host=localhost;Database=atentis_test;Username=atentis_test;Password=..."
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public static readonly string? ConnectionString = Environment.GetEnvironmentVariable("ATENTIS_TEST_PG");
    public NpgsqlDataSource? DataSource { get; private set; }
    public PostgresStore? Store { get; private set; }

    public async Task InitializeAsync()
    {
        if (ConnectionString is null) return;
        DataSource = NpgsqlDataSource.Create(ConnectionString);
        await using (var cmd = DataSource.CreateCommand("DROP SCHEMA IF EXISTS atentis CASCADE")) await cmd.ExecuteNonQueryAsync();
        await new SchemaMigrator(DataSource, NullLogger<SchemaMigrator>.Instance).MigrateAsync(default);
        Store = new PostgresStore(DataSource, Options.Create(new DatabaseOptions()), NullLogger<PostgresStore>.Instance);
    }

    public async Task DisposeAsync() { if (DataSource is not null) await DataSource.DisposeAsync(); }
}

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute() { if (PostgresFixture.ConnectionString is null) Skip = "Задайте ATENTIS_TEST_PG, чтобы запустить интеграционные тесты PostgreSQL"; }
}

public class PostgresIntegrationTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fx;
    public PostgresIntegrationTests(PostgresFixture fx) => _fx = fx;

    private static TradeRecord Trade(long real, char side = 'B', string account = "ACC", decimal price = 250.5m) =>
        new(real + 1000, real, real + 5000, new DateTime(2026, 11, 11, 7, 0, 0, DateTimeKind.Utc), "TQBR", "SBER", side, 10,
            price, 2505m, 1.25m, 0.1m, 0.2m, account, "ref", "firm", "user", "T", "N", null);

    private async Task<long> Scalar(string sql)
    {
        await using var cmd = _fx.DataSource!.CreateCommand(sql);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    [PostgresFact]
    public async Task Migrations_are_idempotent()
    {
        var m = new SchemaMigrator(_fx.DataSource!, NullLogger<SchemaMigrator>.Instance);
        await m.MigrateAsync(default);
        await m.MigrateAsync(default);
        Assert.Equal(1, await Scalar("SELECT count(*) FROM atentis.schema_migrations"));
    }

    [PostgresFact]
    public async Task Trades_are_inserted_in_bulk_and_duplicates_are_ignored()
    {
        var batch = new[] { Trade(1), Trade(2), Trade(3, 'S') };
        Assert.Equal(3, await _fx.Store!.InsertTradesAsync("MICEX", "SPOT", batch, default));
        // повтор того же пакета + одна новая сделка
        Assert.Equal(1, await _fx.Store.InsertTradesAsync("MICEX", "SPOT", batch.Append(Trade(4)).ToArray(), default));
        // тот же номер сделки в другом слоте - это другая сделка
        Assert.Equal(1, await _fx.Store.InsertTradesAsync("FORTS", "FUT", new[] { Trade(1) }, default));

        Assert.Equal(5, await Scalar("SELECT count(*) FROM atentis.trades WHERE real_trade_no < 5"));
        Assert.Equal(0, await Scalar("SELECT count(*) FROM atentis.trades WHERE market NOT IN ('SPOT','FUT')"));
    }

    [PostgresFact]
    public async Task Trade_values_round_trip_exactly()
    {
        await _fx.Store!.InsertTradesAsync("RT", "SPOT", new[] { Trade(900, price: 123.4567891234m) }, default);
        await using var cmd = _fx.DataSource!.CreateCommand(
            "SELECT price, trade_time, buy_sell, broker_ref, ext_ref, commission FROM atentis.trades WHERE slot_id='RT'");
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync());
        Assert.Equal(123.4567891234m, r.GetDecimal(0));
        Assert.Equal(new DateTime(2026, 11, 11, 7, 0, 0, DateTimeKind.Utc), r.GetDateTime(1));
        Assert.Equal("B", r.GetString(2));
        Assert.Equal("ref", r.GetString(3));
        Assert.True(r.IsDBNull(4));
        Assert.Equal(1.25m, r.GetDecimal(5));
    }

    [PostgresFact]
    public async Task Concurrent_writers_never_create_duplicates()
    {
        var batch = Enumerable.Range(100, 50).Select(i => Trade(i)).ToArray();
        var inserted = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => _fx.Store!.InsertTradesAsync("CONC", "SPOT", batch, default)));
        Assert.Equal(50, inserted.Sum());
        Assert.Equal(50, await Scalar("SELECT count(*) FROM atentis.trades WHERE slot_id='CONC'"));
    }

    [PostgresFact]
    public async Task Table_row_history_and_latest_hashes()
    {
        var rows1 = new[] { new TableRowRecord("1", "h1", "{\"Status\":\"O\"}") };
        Assert.Equal(1, await _fx.Store!.InsertTableRowsAsync("MICEX", "ORDERS", rows1, default));
        Assert.Equal(0, await _fx.Store.InsertTableRowsAsync("MICEX", "ORDERS", rows1, default));   // тот же hash
        Assert.Equal(1, await _fx.Store.InsertTableRowsAsync("MICEX", "ORDERS", new[] { new TableRowRecord("1", "h2", "{\"Status\":\"M\"}") }, default));

        var latest = await _fx.Store.LoadLatestRowHashesAsync("MICEX", "ORDERS", default);
        Assert.Equal("h2", latest["1"]);
        Assert.Equal("M", (string?)await ScalarText("SELECT data->>'Status' FROM atentis.v_table_rows_latest WHERE table_name='ORDERS' AND row_key='1'"));
    }

    [PostgresFact]
    public async Task Quotes_and_status_are_stored()
    {
        var q = new QuoteRecord("TQBR", "SBER", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), 100.1m, 5, 100.2m, 7, null);
        Assert.Equal(1, await _fx.Store!.InsertQuotesAsync("MICEX", new[] { q }, default));

        var st = new SlotStatus("MICEX", "SPOT", "Connected", true, DateTime.UtcNow, DateTime.UtcNow, 2, "boom");
        await _fx.Store.UpsertStatusAsync(st, default);
        await _fx.Store.UpsertStatusAsync(st with { Connected = false, State = "Disconnected" }, default);   // upsert, а не вторая строка

        Assert.Equal(1, await Scalar("SELECT count(*) FROM atentis.collector_status WHERE slot_id='MICEX'"));
        Assert.Equal(0, await Scalar("SELECT count(*) FROM atentis.v_collector_health WHERE connected AND slot_id='MICEX'"));
        Assert.True(await _fx.Store.PingAsync(default));
    }

    private async Task<string?> ScalarText(string sql)
    {
        await using var cmd = _fx.DataSource!.CreateCommand(sql);
        return (string?)await cmd.ExecuteScalarAsync();
    }
}
