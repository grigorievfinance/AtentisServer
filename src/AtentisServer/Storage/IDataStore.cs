namespace AtentisServer.Storage;

/// <summary>Хранилище собранных данных. Все методы идемпотентны: повторная запись тех же данных безопасна.</summary>
public interface IDataStore
{
    /// <returns>Сколько строк реально добавлено (дубликаты не считаются).</returns>
    Task<int> InsertTradesAsync(string slotId, string market, IReadOnlyList<TradeRecord> trades, CancellationToken ct);

    Task<int> InsertQuotesAsync(string slotId, IReadOnlyList<QuoteRecord> quotes, CancellationToken ct);

    Task<int> InsertTableRowsAsync(string slotId, string tableName, IReadOnlyList<TableRowRecord> rows, CancellationToken ct);

    /// <summary>Последний известный hash каждой строки (row_key -> row_hash), чтобы после рестарта не дублировать историю.</summary>
    Task<Dictionary<string, string>> LoadLatestRowHashesAsync(string slotId, string tableName, CancellationToken ct);

    Task UpsertStatusAsync(SlotStatus status, CancellationToken ct);

    Task<bool> PingAsync(CancellationToken ct);
}
