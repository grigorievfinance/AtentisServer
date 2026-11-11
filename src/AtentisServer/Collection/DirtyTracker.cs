using System.Collections.Concurrent;
using System.Threading.Channels;

namespace AtentisServer.Collection;

/// <summary>
/// Набор «грязных» (обновившихся) таблиц. Обновления одной таблицы схлопываются в одну запись, потому что
/// Atentis отдаёт полное актуальное состояние таблицы - обрабатывать каждое событие по отдельности не нужно и нельзя
/// блокировать поток библиотеки записью в БД. Ничего не теряется: пока таблица помечена, она будет обработана.
/// </summary>
public sealed class DirtyTracker
{
    private readonly ConcurrentDictionary<(string Slot, string Table), byte> _dirty = new();
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true
    });

    public int Count => _dirty.Count;

    public void Mark(string slot, string table)
    {
        _dirty[(slot, table.ToUpperInvariant())] = 0;
        _wake.Writer.TryWrite(true);
    }

    /// <summary>Ждёт, пока появится хотя бы одна грязная таблица, и забирает все.</summary>
    public async Task<List<(string Slot, string Table)>> TakeAsync(CancellationToken ct)
    {
        while (true)
        {
            var batch = TakeNow();
            if (batch.Count > 0) return batch;
            await _wake.Reader.ReadAsync(ct);
        }
    }

    public List<(string Slot, string Table)> TakeNow()
    {
        var result = new List<(string, string)>();
        foreach (var key in _dirty.Keys)
            if (_dirty.TryRemove(key, out _)) result.Add(key);
        return result;
    }
}
