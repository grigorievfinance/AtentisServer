using AtentisServer.Storage;

namespace AtentisServer.Collection;

/// <summary>Пропускает только изменившиеся котировки и не чаще, чем раз в minInterval на инструмент.</summary>
public sealed class QuoteFilter
{
    private readonly TimeSpan _minInterval;
    private readonly Func<DateTime> _utcNow;
    private readonly Dictionary<(string, string), (QuoteRecord Quote, DateTime WrittenAt)> _last = new();

    public QuoteFilter(TimeSpan minInterval, Func<DateTime>? utcNow = null)
    {
        _minInterval = minInterval;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public List<QuoteRecord> Filter(IEnumerable<QuoteRecord> quotes)
    {
        var now = _utcNow();
        var result = new List<QuoteRecord>();
        foreach (var q in quotes)
        {
            var key = (q.SecBoard, q.SecCode);
            if (_last.TryGetValue(key, out var prev))
            {
                var same = prev.Quote.Bid == q.Bid && prev.Quote.BidQty == q.BidQty &&
                           prev.Quote.Ask == q.Ask && prev.Quote.AskQty == q.AskQty && prev.Quote.Last == q.Last;
                if (same || now - prev.WrittenAt < _minInterval) continue;
            }
            result.Add(q);
        }
        return result;
    }

    /// <summary>Фиксирует успешно записанные котировки (вызывать после записи в БД).</summary>
    public void Commit(IEnumerable<QuoteRecord> written)
    {
        var now = _utcNow();
        foreach (var q in written) _last[(q.SecBoard, q.SecCode)] = (q, now);
    }
}
