namespace AtentisServer.Storage;

public sealed record TradeRecord(
    long TradeNo,
    long RealTradeNo,
    long OrderNo,
    DateTime TradeTimeUtc,
    string SecBoard,
    string SecCode,
    char BuySell,
    long Quantity,
    decimal Price,
    decimal Value,
    decimal Commission,
    decimal ClearingCommission,
    decimal ExchangeCommission,
    string Account,
    string? BrokerRef,
    string? FirmId,
    string? UserId,
    string? TradeType,
    string? Period,
    string? ExtRef)
{
    /// <summary>Ключ идемпотентности - совпадает с UNIQUE-ограничением trades_uk (без slot_id).</summary>
    public (long RealTradeNo, char BuySell, string Account) Key => (RealTradeNo, BuySell, Account);
}

public sealed record QuoteRecord(
    string SecBoard,
    string SecCode,
    DateTime QuoteTimeUtc,
    decimal? Bid,
    decimal? BidQty,
    decimal? Ask,
    decimal? AskQty,
    decimal? Last);

public sealed record TableRowRecord(string RowKey, string RowHash, string Json);

public sealed record SlotStatus(
    string SlotId,
    string Market,
    string State,
    bool Connected,
    DateTime? LastConnectedUtc,
    DateTime? LastTableUpdateUtc,
    long Reconnects,
    string? LastError);
