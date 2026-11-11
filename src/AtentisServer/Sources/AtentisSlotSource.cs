using System.Data;
using Atentis.Connection;
using AtentisServer.Configuration;

namespace AtentisServer.Sources;

public sealed class AtentisSlotSourceFactory : ISlotSourceFactory
{
    private readonly ILoggerFactory _loggers;
    public AtentisSlotSourceFactory(ILoggerFactory loggers) => _loggers = loggers;

    public ISlotSource Create(SlotOptions slot, IReadOnlyList<TableOptions> tables) =>
        new AtentisSlotSource(slot, tables, _loggers.CreateLogger($"Atentis.{slot.Name}"));
}

/// <summary>Реализация ISlotSource поверх Atentis.Connection.Slot (без UI, без MessageBox, все исключения перехватываются).</summary>
public sealed class AtentisSlotSource : ISlotSource
{
    private readonly SlotOptions _options;
    private readonly IReadOnlyList<TableOptions> _tables;
    private readonly ILogger _log;
    private Slot? _slot;
    private volatile string? _lastError;
    private long _lastConnectedTicks;
    private readonly LogThrottle _throttle = new(TimeSpan.FromMinutes(1));

    public AtentisSlotSource(SlotOptions options, IReadOnlyList<TableOptions> tables, ILogger log)
    {
        _options = options;
        _tables = tables;
        _log = log;
    }

    public string Name => _options.Name;
    public string Market => _options.Market;
    public string? LastError => _lastError;

    public bool IsConnected
    {
        get
        {
            try
            {
                var connected = _slot is { Connected: true };
                if (connected) Interlocked.Exchange(ref _lastConnectedTicks, DateTime.UtcNow.Ticks);
                return connected;
            }
            catch { return false; }
        }
    }

    public string State
    {
        get { try { return _slot?.State.ToString() ?? "NotStarted"; } catch { return "Unknown"; } }
    }

    public DateTime? LastConnectedUtc
    {
        get
        {
            var t = Interlocked.Read(ref _lastConnectedTicks);
            return t == 0 ? null : new DateTime(t, DateTimeKind.Utc);
        }
    }

    public event Action<string>? TableUpdated;

    public Task StartAsync(CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        var slot = new Slot(_options.Name, _options.Server, _options.Port, _options.Login, _options.Password,
            false, _options.PublicKeyFile ?? "");
        slot.RefreshPeriod = _options.RefreshPeriodMs;
        _slot = slot;

        // Порядок вызовов как в проверенном рабочем коде: RequestSocket -> обработчики -> Init -> Start -> AddTable.
        slot.rqs = new RequestSocket(slot);
        slot.rqs.evhLogLine += OnLogLine;
        slot.rqs.evhUpdateStatus += OnStatus;
        slot.rqs.evhUpdateAllTableViews += OnTablesUpdated;
        slot.evhSlotStateChanged += OnSlotStateChanged;
        slot.rqs.Init();
        slot.Start();

        foreach (var t in _tables.Where(t => t.Enabled))
        {
            if (string.IsNullOrWhiteSpace(t.Filter))
                slot.AddTable(t.Name);
            else
                slot.AddTable(new Table(t.Name, t.Name, "", "", "", t.Filter));
        }
        _log.LogInformation("Слот {Slot} запущен ({Server}:{Port}), таблиц: {Tables}",
            _options.Name, _options.Server, _options.Port, string.Join(",", _tables.Where(t => t.Enabled).Select(t => t.Name)));
    }, ct);

    public Task StopAsync() => Task.Run(() =>
    {
        var slot = Interlocked.Exchange(ref _slot, null);
        if (slot is null) return;
        try
        {
            if (slot.rqs is not null)
            {
                slot.rqs.evhLogLine -= OnLogLine;
                slot.rqs.evhUpdateStatus -= OnStatus;
                slot.rqs.evhUpdateAllTableViews -= OnTablesUpdated;
            }
            slot.evhSlotStateChanged -= OnSlotStateChanged;
            slot.Stop();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Ошибка при остановке слота {Slot}", _options.Name);
        }
        finally
        {
            try { slot.Dispose(); } catch { /* слот уже освобождён */ }
        }
    });

    public DataTable? Snapshot(string tableName)
    {
        var slot = _slot;
        if (slot is null) return null;
        // Таблицы обновляются из потока библиотеки; копируем под её блокировкой, чтобы получить согласованный снимок.
        lock (slot.mt)
        {
            return slot.GetTable(tableName)?.Copy();
        }
    }

    // ---- Обработчики событий библиотеки. Вызываются из ЧУЖИХ потоков: не должны бросать исключения и не должны блокироваться.

    private void OnTablesUpdated(object? sender, TableListEventArgs e)
    {
        try
        {
            foreach (var t in e.List.OfType<Table>())
            {
                if (!string.IsNullOrEmpty(t.Name)) TableUpdated?.Invoke(t.Name);
            }
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            _log.LogError(ex, "Ошибка в обработчике обновления таблиц слота {Slot}", _options.Name);
        }
    }

    private void OnSlotStateChanged(object? sender, SlotEventArgs e)
    {
        try
        {
            var level = e.State switch
            {
                SlotState.Failed or SlotState.Denied or SlotState.NeedNewPassword => LogLevel.Error,
                SlotState.Disconnected or SlotState.Disconnecting or SlotState.Reconnecting => LogLevel.Warning,
                _ => LogLevel.Information
            };
            if (level >= LogLevel.Warning) _lastError = $"{e.State}: {e.Message}";
            if (e.State is SlotState.Connected or SlotState.Reconnected)
                Interlocked.Exchange(ref _lastConnectedTicks, DateTime.UtcNow.Ticks);
            if (_throttle.ShouldLog("state:" + e.State + e.Message, out var suppressed))
                _log.Log(level, "Слот {Slot}: состояние {State} {Message} (повторов за минуту: {Suppressed})",
                    _options.Name, e.State, e.Message, suppressed);
        }
        catch { /* логирование не должно ломать поток библиотеки */ }
    }

    private void OnLogLine(object? sender, TableEventArgs e)
    {
        try { _log.LogDebug("{Message}", e.Message); } catch { }
    }

    private void OnStatus(object? sender, TableEventArgs e)
    {
        try
        {
            if (_throttle.ShouldLog("status:" + e.Message, out var suppressed))
                _log.LogInformation("Статус: {Message} (повторов за минуту: {Suppressed})", e.Message, suppressed);
        }
        catch { }
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
