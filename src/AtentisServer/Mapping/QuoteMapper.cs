using System.Data;
using AtentisServer.Storage;

namespace AtentisServer.Mapping;

/// <summary>SECURITIES -> котировки (bid/ask/last).</summary>
public sealed class QuoteMapper
{
    private readonly TimeConverter _time;
    private readonly Func<DateTime> _utcNow;

    public QuoteMapper(TimeConverter time, Func<DateTime>? utcNow = null)
    {
        _time = time;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public MapResult<QuoteRecord> Map(DataTable table)
    {
        var items = new List<QuoteRecord>(table.Rows.Count);
        var skipped = new Dictionary<string, int>();
        foreach (DataRow row in table.Rows)
        {
            if (row.RowState == DataRowState.Deleted) continue;
            var code = RowReader.Str(row, "SecCode");
            if (string.IsNullOrEmpty(code)) { skipped["no_sec_code"] = skipped.GetValueOrDefault("no_sec_code") + 1; continue; }

            var t = RowReader.DateTime(row, "Time") ?? RowReader.DateTime(row, "TradeTime");
            items.Add(new QuoteRecord(
                SecBoard: RowReader.Str(row, "SecBoard") ?? "",
                SecCode: code,
                QuoteTimeUtc: t is null ? _utcNow() : _time.ToUtc(t.Value),
                Bid: RowReader.Decimal(row, "Bid"),
                BidQty: RowReader.Decimal(row, "BidDepth"),
                Ask: RowReader.Decimal(row, "Offer"),
                AskQty: RowReader.Decimal(row, "OfferDepth"),
                Last: RowReader.Decimal(row, "Last")));
        }
        return new MapResult<QuoteRecord>(items, skipped);
    }
}
