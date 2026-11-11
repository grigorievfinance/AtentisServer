using AtentisServer.Sources;

namespace AtentisServer.Collection;

/// <summary>Состояние слота, видимое health-check'ам, метрикам и репортеру статуса.</summary>
public sealed class SlotRuntime
{
    public SlotRuntime(string name, string market) { Name = name; Market = market; }

    public string Name { get; }
    public string Market { get; }
    public ISlotSource? Source { get; set; }
    public long Reconnects;
    public int ConsecutiveFailures;
    public DateTime? DisconnectedSinceUtc { get; set; }
    public DateTime? NextAttemptUtc { get; set; }
    public DateTime? LastTableUpdateUtc { get; set; }
    public string? LastError { get; set; }
    public bool WasConnected { get; set; }

    public bool IsConnected => Source?.IsConnected == true;
}

public sealed class CollectorState
{
    public List<SlotRuntime> Slots { get; } = new();
    public volatile bool DatabaseReady;
    public volatile bool DatabaseUp;
}
