using System.Data;
using AtentisServer.Configuration;
using AtentisServer.Sources;
using AtentisServer.Storage;

namespace AtentisServer.Tests;

internal sealed class FakeSource : ISlotSource
{
    public Dictionary<string, DataTable> Tables { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool Connected { get; set; } = true;
    public bool Disposed { get; private set; }
    public Exception? StartFailure { get; set; }

    public FakeSource(string name, string market) { Name = name; Market = market; }

    public string Name { get; }
    public string Market { get; }
    public bool IsConnected => Connected && !Disposed;
    public string State => Connected ? "Connected" : "Disconnected";
    public DateTime? LastConnectedUtc => Connected ? DateTime.UtcNow : null;
    public string? LastError => null;
    public event Action<string>? TableUpdated;

    public void Raise(string table) => TableUpdated?.Invoke(table);
    public Task StartAsync(CancellationToken ct) => StartFailure is null ? Task.CompletedTask : Task.FromException(StartFailure);
    public Task StopAsync() { Disposed = true; return Task.CompletedTask; }
    public DataTable? Snapshot(string tableName) => Tables.TryGetValue(tableName, out var t) ? t.Copy() : null;
    public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
}

internal sealed class FakeFactory : ISlotSourceFactory
{
    public List<FakeSource> Created { get; } = new();
    public Action<FakeSource>? Configure { get; set; }
    public ISlotSource Create(SlotOptions slot, IReadOnlyList<TableOptions> tables)
    {
        var s = new FakeSource(slot.Name, slot.Market);
        Configure?.Invoke(s);
        Created.Add(s);
        return s;
    }
}

internal sealed class InMemoryStore : IDataStore
{
    private readonly object _gate = new();
    public Dictionary<(string Slot, long Real, char Side, string Acct), TradeRecord> Trades { get; } = new();
    public List<(string Table, TableRowRecord Row)> Rows { get; } = new();
    public List<QuoteRecord> Quotes { get; } = new();
    public List<SlotStatus> Statuses { get; } = new();
    public int InsertTradeCalls;
    public int FailNextInserts;     // сколько ближайших вставок завершить ошибкой

    public Task<int> InsertTradesAsync(string slotId, string market, IReadOnlyList<TradeRecord> trades, CancellationToken ct)
    {
        lock (_gate)
        {
            InsertTradeCalls++;
            if (FailNextInserts > 0) { FailNextInserts--; throw new Npgsql.NpgsqlException("simulated outage"); }
            var added = 0;
            foreach (var t in trades)
                if (Trades.TryAdd((slotId, t.RealTradeNo, t.BuySell, t.Account), t)) added++;
            return Task.FromResult(added);
        }
    }

    public Task<int> InsertQuotesAsync(string slotId, IReadOnlyList<QuoteRecord> quotes, CancellationToken ct)
    { lock (_gate) Quotes.AddRange(quotes); return Task.FromResult(quotes.Count); }

    public Task<int> InsertTableRowsAsync(string slotId, string tableName, IReadOnlyList<TableRowRecord> rows, CancellationToken ct)
    { lock (_gate) foreach (var r in rows) Rows.Add((tableName, r)); return Task.FromResult(rows.Count); }

    public Task<Dictionary<string, string>> LoadLatestRowHashesAsync(string slotId, string tableName, CancellationToken ct) =>
        Task.FromResult(new Dictionary<string, string>());

    public Task UpsertStatusAsync(SlotStatus status, CancellationToken ct)
    { lock (_gate) Statuses.Add(status); return Task.CompletedTask; }

    public Task<bool> PingAsync(CancellationToken ct) => Task.FromResult(true);
}
