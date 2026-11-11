using System.Diagnostics;
using AtentisServer.Configuration;
using AtentisServer.Monitoring;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace AtentisServer.Storage;

/// <summary>
/// Запись в PostgreSQL. Используется NpgsqlDataSource (пул соединений, потокобезопасно), а не одно общее подключение.
/// Вставки - пакетные через unnest(), с ON CONFLICT DO NOTHING: никаких SELECT на каждую строку, нет гонок и дублей.
/// Все значения передаются параметрами, SQL строится только из констант.
/// </summary>
public sealed class PostgresStore : IDataStore
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly int _timeoutSeconds;
    private readonly ILogger<PostgresStore> _log;

    public PostgresStore(NpgsqlDataSource dataSource, IOptions<DatabaseOptions> options, ILogger<PostgresStore> log)
    {
        _dataSource = dataSource;
        _timeoutSeconds = options.Value.CommandTimeoutSeconds;
        _log = log;
    }

    private const string InsertTradesSql = """
        INSERT INTO atentis.trades
            (slot_id, market, trade_no, real_trade_no, order_no, trade_time, sec_board, sec_code, buy_sell, quantity,
             price, value, commission, clearing_commission, exchange_commission, account,
             broker_ref, firm_id, user_id, trade_type, period, ext_ref)
        SELECT @slot_id, @market, t.trade_no, t.real_trade_no, t.order_no, t.trade_time, t.sec_board, t.sec_code, t.buy_sell, t.quantity,
               t.price, t.value, t.commission, t.clearing_commission, t.exchange_commission, t.account,
               t.broker_ref, t.firm_id, t.user_id, t.trade_type, t.period, t.ext_ref
        FROM unnest(@trade_no, @real_trade_no, @order_no, @trade_time, @sec_board, @sec_code, @buy_sell, @quantity,
                    @price, @value, @commission, @clearing_commission, @exchange_commission, @account,
                    @broker_ref, @firm_id, @user_id, @trade_type, @period, @ext_ref)
             AS t(trade_no, real_trade_no, order_no, trade_time, sec_board, sec_code, buy_sell, quantity,
                  price, value, commission, clearing_commission, exchange_commission, account,
                  broker_ref, firm_id, user_id, trade_type, period, ext_ref)
        ON CONFLICT ON CONSTRAINT trades_uk DO NOTHING
        """;

    public async Task<int> InsertTradesAsync(string slotId, string market, IReadOnlyList<TradeRecord> trades, CancellationToken ct)
    {
        if (trades.Count == 0) return 0;
        var sw = Stopwatch.StartNew();
        try
        {
            await using var cmd = _dataSource.CreateCommand(InsertTradesSql);
            cmd.CommandTimeout = _timeoutSeconds;
            cmd.Parameters.AddWithValue("slot_id", slotId);
            cmd.Parameters.AddWithValue("market", market);
            Arr(cmd, "trade_no", NpgsqlDbType.Bigint, trades.Select(t => t.TradeNo).ToArray());
            Arr(cmd, "real_trade_no", NpgsqlDbType.Bigint, trades.Select(t => t.RealTradeNo).ToArray());
            Arr(cmd, "order_no", NpgsqlDbType.Bigint, trades.Select(t => t.OrderNo).ToArray());
            Arr(cmd, "trade_time", NpgsqlDbType.TimestampTz, trades.Select(t => DateTime.SpecifyKind(t.TradeTimeUtc, DateTimeKind.Utc)).ToArray());
            Arr(cmd, "sec_board", NpgsqlDbType.Text, trades.Select(t => t.SecBoard).ToArray());
            Arr(cmd, "sec_code", NpgsqlDbType.Text, trades.Select(t => t.SecCode).ToArray());
            Arr(cmd, "buy_sell", NpgsqlDbType.Text, trades.Select(t => t.BuySell.ToString()).ToArray());
            Arr(cmd, "quantity", NpgsqlDbType.Bigint, trades.Select(t => t.Quantity).ToArray());
            Arr(cmd, "price", NpgsqlDbType.Numeric, trades.Select(t => t.Price).ToArray());
            Arr(cmd, "value", NpgsqlDbType.Numeric, trades.Select(t => t.Value).ToArray());
            Arr(cmd, "commission", NpgsqlDbType.Numeric, trades.Select(t => t.Commission).ToArray());
            Arr(cmd, "clearing_commission", NpgsqlDbType.Numeric, trades.Select(t => t.ClearingCommission).ToArray());
            Arr(cmd, "exchange_commission", NpgsqlDbType.Numeric, trades.Select(t => t.ExchangeCommission).ToArray());
            Arr(cmd, "account", NpgsqlDbType.Text, trades.Select(t => t.Account).ToArray());
            Arr(cmd, "broker_ref", NpgsqlDbType.Text, trades.Select(t => t.BrokerRef).ToArray());
            Arr(cmd, "firm_id", NpgsqlDbType.Text, trades.Select(t => t.FirmId).ToArray());
            Arr(cmd, "user_id", NpgsqlDbType.Text, trades.Select(t => t.UserId).ToArray());
            Arr(cmd, "trade_type", NpgsqlDbType.Text, trades.Select(t => t.TradeType).ToArray());
            Arr(cmd, "period", NpgsqlDbType.Text, trades.Select(t => t.Period).ToArray());
            Arr(cmd, "ext_ref", NpgsqlDbType.Text, trades.Select(t => t.ExtRef).ToArray());
            var inserted = await cmd.ExecuteNonQueryAsync(ct);
            AppMetrics.DbWriteSeconds.WithLabels("trades").Observe(sw.Elapsed.TotalSeconds);
            return inserted;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppMetrics.DbErrors.WithLabels("trades").Inc();
            _log.LogError(ex, "Ошибка записи сделок в БД: slot={Slot}, rows={Rows}", slotId, trades.Count);
            throw;
        }
    }

    private const string InsertQuotesSql = """
        INSERT INTO atentis.quotes (slot_id, sec_board, sec_code, quote_time, bid, bid_qty, ask, ask_qty, last_price)
        SELECT @slot_id, q.sec_board, q.sec_code, q.quote_time, q.bid, q.bid_qty, q.ask, q.ask_qty, q.last_price
        FROM unnest(@sec_board, @sec_code, @quote_time, @bid, @bid_qty, @ask, @ask_qty, @last_price)
             AS q(sec_board, sec_code, quote_time, bid, bid_qty, ask, ask_qty, last_price)
        """;

    public async Task<int> InsertQuotesAsync(string slotId, IReadOnlyList<QuoteRecord> quotes, CancellationToken ct)
    {
        if (quotes.Count == 0) return 0;
        var sw = Stopwatch.StartNew();
        try
        {
            await using var cmd = _dataSource.CreateCommand(InsertQuotesSql);
            cmd.CommandTimeout = _timeoutSeconds;
            cmd.Parameters.AddWithValue("slot_id", slotId);
            Arr(cmd, "sec_board", NpgsqlDbType.Text, quotes.Select(q => q.SecBoard).ToArray());
            Arr(cmd, "sec_code", NpgsqlDbType.Text, quotes.Select(q => q.SecCode).ToArray());
            Arr(cmd, "quote_time", NpgsqlDbType.TimestampTz, quotes.Select(q => DateTime.SpecifyKind(q.QuoteTimeUtc, DateTimeKind.Utc)).ToArray());
            Arr(cmd, "bid", NpgsqlDbType.Numeric, quotes.Select(q => q.Bid).ToArray());
            Arr(cmd, "bid_qty", NpgsqlDbType.Numeric, quotes.Select(q => q.BidQty).ToArray());
            Arr(cmd, "ask", NpgsqlDbType.Numeric, quotes.Select(q => q.Ask).ToArray());
            Arr(cmd, "ask_qty", NpgsqlDbType.Numeric, quotes.Select(q => q.AskQty).ToArray());
            Arr(cmd, "last_price", NpgsqlDbType.Numeric, quotes.Select(q => q.Last).ToArray());
            var inserted = await cmd.ExecuteNonQueryAsync(ct);
            AppMetrics.DbWriteSeconds.WithLabels("quotes").Observe(sw.Elapsed.TotalSeconds);
            return inserted;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppMetrics.DbErrors.WithLabels("quotes").Inc();
            _log.LogError(ex, "Ошибка записи котировок в БД: slot={Slot}, rows={Rows}", slotId, quotes.Count);
            throw;
        }
    }

    private const string InsertRowsSql = """
        INSERT INTO atentis.table_rows (slot_id, table_name, row_key, row_hash, data)
        SELECT @slot_id, @table_name, r.row_key, r.row_hash, r.data::jsonb
        FROM unnest(@row_key, @row_hash, @data) AS r(row_key, row_hash, data)
        ON CONFLICT ON CONSTRAINT table_rows_uk DO NOTHING
        """;

    public async Task<int> InsertTableRowsAsync(string slotId, string tableName, IReadOnlyList<TableRowRecord> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return 0;
        var sw = Stopwatch.StartNew();
        try
        {
            await using var cmd = _dataSource.CreateCommand(InsertRowsSql);
            cmd.CommandTimeout = _timeoutSeconds;
            cmd.Parameters.AddWithValue("slot_id", slotId);
            cmd.Parameters.AddWithValue("table_name", tableName);
            Arr(cmd, "row_key", NpgsqlDbType.Text, rows.Select(r => r.RowKey).ToArray());
            Arr(cmd, "row_hash", NpgsqlDbType.Text, rows.Select(r => r.RowHash).ToArray());
            Arr(cmd, "data", NpgsqlDbType.Text, rows.Select(r => r.Json).ToArray());
            var inserted = await cmd.ExecuteNonQueryAsync(ct);
            AppMetrics.DbWriteSeconds.WithLabels("table_rows").Observe(sw.Elapsed.TotalSeconds);
            return inserted;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppMetrics.DbErrors.WithLabels("table_rows").Inc();
            _log.LogError(ex, "Ошибка записи строк таблицы {Table} в БД: slot={Slot}, rows={Rows}", tableName, slotId, rows.Count);
            throw;
        }
    }

    public async Task<Dictionary<string, string>> LoadLatestRowHashesAsync(string slotId, string tableName, CancellationToken ct)
    {
        const string sql = """
            SELECT DISTINCT ON (row_key) row_key, row_hash
            FROM atentis.table_rows
            WHERE slot_id = @slot_id AND table_name = @table_name
            ORDER BY row_key, collected_at DESC, id DESC
            """;
        await using var cmd = _dataSource.CreateCommand(sql);
        cmd.CommandTimeout = _timeoutSeconds;
        cmd.Parameters.AddWithValue("slot_id", slotId);
        cmd.Parameters.AddWithValue("table_name", tableName);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result[reader.GetString(0)] = reader.GetString(1);
        return result;
    }

    public async Task UpsertStatusAsync(SlotStatus s, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO atentis.collector_status
                (slot_id, market, state, connected, last_connected_at, last_table_update_at, reconnects, last_error, app_version, host, updated_at)
            VALUES (@slot_id, @market, @state, @connected, @last_connected_at, @last_table_update_at, @reconnects, @last_error, @app_version, @host, now())
            ON CONFLICT (slot_id) DO UPDATE SET
                market = EXCLUDED.market, state = EXCLUDED.state, connected = EXCLUDED.connected,
                last_connected_at = EXCLUDED.last_connected_at, last_table_update_at = EXCLUDED.last_table_update_at,
                reconnects = EXCLUDED.reconnects, last_error = EXCLUDED.last_error,
                app_version = EXCLUDED.app_version, host = EXCLUDED.host, updated_at = now()
            """;
        await using var cmd = _dataSource.CreateCommand(sql);
        cmd.CommandTimeout = _timeoutSeconds;
        cmd.Parameters.AddWithValue("slot_id", s.SlotId);
        cmd.Parameters.AddWithValue("market", s.Market);
        cmd.Parameters.AddWithValue("state", s.State);
        cmd.Parameters.AddWithValue("connected", s.Connected);
        cmd.Parameters.Add(new NpgsqlParameter("last_connected_at", NpgsqlDbType.TimestampTz) { Value = Utc(s.LastConnectedUtc) });
        cmd.Parameters.Add(new NpgsqlParameter("last_table_update_at", NpgsqlDbType.TimestampTz) { Value = Utc(s.LastTableUpdateUtc) });
        cmd.Parameters.AddWithValue("reconnects", s.Reconnects);
        cmd.Parameters.Add(new NpgsqlParameter("last_error", NpgsqlDbType.Text) { Value = (object?)Truncate(s.LastError, 1000) ?? DBNull.Value });
        cmd.Parameters.AddWithValue("app_version", AppInfo.Version);
        cmd.Parameters.AddWithValue("host", Environment.MachineName);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> PingAsync(CancellationToken ct)
    {
        try
        {
            await using var cmd = _dataSource.CreateCommand("SELECT 1");
            cmd.CommandTimeout = 5;
            await cmd.ExecuteScalarAsync(ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "PostgreSQL недоступен");
            return false;
        }
    }

    private static void Arr<T>(NpgsqlCommand cmd, string name, NpgsqlDbType elementType, T[] values) =>
        cmd.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Array | elementType) { Value = values });

    private static object Utc(DateTime? dt) =>
        dt is null ? DBNull.Value : DateTime.SpecifyKind(dt.Value, DateTimeKind.Utc);

    private static string? Truncate(string? s, int max) => s is null || s.Length <= max ? s : s[..max];
}
