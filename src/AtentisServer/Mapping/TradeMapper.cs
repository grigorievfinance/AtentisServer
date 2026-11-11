using System.Data;
using AtentisServer.Storage;

namespace AtentisServer.Mapping;

public sealed record MapResult<T>(List<T> Items, Dictionary<string, int> Skipped);

/// <summary>Преобразует снимок таблицы TRADES Atentis в записи для БД. Некорректные строки пропускаются (с причиной), а не валят весь пакет.</summary>
public sealed class TradeMapper
{
    private readonly TimeConverter _time;

    public TradeMapper(TimeConverter time) => _time = time;

    public MapResult<TradeRecord> Map(DataTable table)
    {
        var items = new List<TradeRecord>(table.Rows.Count);
        var skipped = new Dictionary<string, int>();

        foreach (DataRow row in table.Rows)
        {
            if (row.RowState == DataRowState.Deleted) continue;

            var real = RowReader.Long(row, "RealTradeNo") ?? RowReader.Long(row, "TradeNo");
            if (real is null) { Skip(skipped, "no_trade_no"); continue; }

            var secCode = RowReader.Str(row, "SecCode");
            if (string.IsNullOrEmpty(secCode)) { Skip(skipped, "no_sec_code"); continue; }

            var side = RowReader.Str(row, "BuySell");
            if (string.IsNullOrEmpty(side) || (side[0] != 'B' && side[0] != 'S')) { Skip(skipped, "bad_buy_sell"); continue; }

            var time = RowReader.DateTime(row, "TradeTime");
            if (time is null) { Skip(skipped, "no_trade_time"); continue; }

            var qty = RowReader.Long(row, "Quantity");
            if (qty is null) { Skip(skipped, "no_quantity"); continue; }

            var price = RowReader.Decimal(row, "Price");
            if (price is null) { Skip(skipped, "no_price"); continue; }

            items.Add(new TradeRecord(
                TradeNo: RowReader.Long(row, "TradeNo") ?? real.Value,
                RealTradeNo: real.Value,
                OrderNo: RowReader.Long(row, "OrderNo") ?? 0,
                TradeTimeUtc: _time.ToUtc(time.Value),
                SecBoard: RowReader.Str(row, "SecBoard") ?? "",
                SecCode: secCode,
                BuySell: side[0],
                Quantity: qty.Value,
                Price: price.Value,
                Value: RowReader.Decimal(row, "Value") ?? 0m,
                Commission: RowReader.Decimal(row, "Commission") ?? 0m,
                ClearingCommission: RowReader.Decimal(row, "ClearingCenterComm") ?? 0m,
                ExchangeCommission: RowReader.Decimal(row, "ExchangeComm") ?? 0m,
                Account: RowReader.Str(row, "Account") ?? "",
                BrokerRef: Nz(RowReader.Str(row, "BrokerRef")),
                FirmId: Nz(RowReader.Str(row, "FirmID")),
                UserId: Nz(RowReader.Str(row, "UserID")),
                TradeType: Nz(RowReader.Str(row, "TradeType")),
                Period: Nz(RowReader.Str(row, "Period")),
                ExtRef: Nz(RowReader.Str(row, "ExtRef"))));
        }
        return new MapResult<TradeRecord>(items, skipped);
    }

    private static string? Nz(string? s) => string.IsNullOrEmpty(s) ? null : s;
    private static void Skip(Dictionary<string, int> d, string reason) => d[reason] = d.GetValueOrDefault(reason) + 1;
}
