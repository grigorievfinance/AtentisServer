using System.Collections;
using AtentisServer.Sources;

namespace AtentisServer.Collection;

/// <summary>Состояние слота, видимое health-check'ам, метрикам, статусу в «Панели управления» и репортеру статуса.</summary>
public sealed class SlotRuntime
{
    public SlotRuntime(string name, string market) { Name = name; Market = market; }

    public string Name { get; }
    public string Market { get; set; }
    public ISlotSource? Source { get; set; }
    public long Reconnects;
    public int ConsecutiveFailures;
    public DateTime? DisconnectedSinceUtc { get; set; }
    public DateTime? NextAttemptUtc { get; set; }
    public DateTime? LastTableUpdateUtc { get; set; }
    public string? LastError { get; set; }
    public bool WasConnected { get; set; }
    public bool EverConnected { get; set; }

    /// <summary>Параметры (сервер/порт/логин), с которыми запущен текущий источник.</summary>
    public string? Identity { get; set; }

    /// <summary>Версия пароля, с которым запущен текущий источник.</summary>
    public long CredentialVersion { get; set; }

    /// <summary>Atentis отклонил логин/пароль; пароль удалён из памяти, ждём новый.</summary>
    public volatile bool AuthFailed;
    public string? AuthMessage { get; set; }

    public bool IsConnected => Source?.IsConnected == true;
}

/// <summary>Потокобезопасный список слотов (меняется при изменении настроек, читается из разных потоков).</summary>
public sealed class SlotList : IEnumerable<SlotRuntime>
{
    private readonly object _gate = new();
    private readonly List<SlotRuntime> _items = new();

    public void Add(SlotRuntime slot) { lock (_gate) _items.Add(slot); }
    public bool Remove(SlotRuntime slot) { lock (_gate) return _items.Remove(slot); }
    public SlotRuntime this[int index] { get { lock (_gate) return _items[index]; } }
    public int Count { get { lock (_gate) return _items.Count; } }
    public SlotRuntime? Find(string name) { lock (_gate) return _items.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)); }
    public SlotRuntime[] Snapshot() { lock (_gate) return _items.ToArray(); }
    public IEnumerator<SlotRuntime> GetEnumerator() => ((IEnumerable<SlotRuntime>)Snapshot()).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public sealed class CollectorState
{
    public SlotList Slots { get; } = new();

    /// <summary>Схема БД проверена/создана и можно писать.</summary>
    public volatile bool DatabaseReady;

    /// <summary>Последняя операция с БД прошла успешно.</summary>
    public volatile bool DatabaseUp;

    public volatile bool DatabaseConfigured;
    public volatile string? DatabaseMessage;
    public volatile string? ConfigError;
}
