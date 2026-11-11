using System.Collections.Concurrent;

namespace AtentisServer.Sources;

/// <summary>
/// Библиотека Atentis при недоступном сервере сама переподключается раз в секунду и каждый раз сообщает об ошибке.
/// Чтобы не заливать лог, одинаковые сообщения пропускаются не чаще одного раза за интервал.
/// </summary>
public sealed class LogThrottle
{
    private readonly TimeSpan _interval;
    private readonly Func<DateTime> _utcNow;
    private readonly ConcurrentDictionary<string, (DateTime At, int Suppressed)> _last = new();

    public LogThrottle(TimeSpan interval, Func<DateTime>? utcNow = null)
    {
        _interval = interval;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>true - сообщение нужно записать; suppressed - сколько таких же было пропущено с прошлого раза.</summary>
    public bool ShouldLog(string key, out int suppressed)
    {
        var now = _utcNow();
        suppressed = 0;
        while (true)
        {
            if (!_last.TryGetValue(key, out var prev))
            {
                if (_last.TryAdd(key, (now, 0))) return true;
                continue;
            }
            if (now - prev.At >= _interval)
            {
                if (_last.TryUpdate(key, (now, 0), prev)) { suppressed = prev.Suppressed; return true; }
                continue;
            }
            if (_last.TryUpdate(key, (prev.At, prev.Suppressed + 1), prev)) return false;
        }
    }
}
