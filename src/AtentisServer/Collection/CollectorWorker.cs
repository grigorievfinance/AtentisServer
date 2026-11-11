using System.Data;
using AtentisServer.Configuration;
using AtentisServer.Mapping;
using AtentisServer.Monitoring;
using AtentisServer.Sources;
using AtentisServer.Storage;
using Microsoft.Extensions.Options;

namespace AtentisServer.Collection;

/// <summary>
/// Ядро сервиса: подключает слоты Atentis, забирает обновлённые таблицы и идемпотентно пишет их в PostgreSQL.
/// Никакой бизнес-логики (финрез, пары, сделки) - только сбор и сохранение.
/// </summary>
public sealed class CollectorWorker : BackgroundService
{
    private static readonly TimeSpan SupervisorTick = TimeSpan.FromSeconds(5);

    private readonly CollectorOptions _options;
    private readonly DatabaseOptions _dbOptions;
    private readonly ISlotSourceFactory _factory;
    private readonly IDataStore _store;
    private readonly SchemaMigrator? _migrator;
    private readonly CollectorState _state;
    private readonly DirtyTracker _dirty = new();
    private readonly ILogger<CollectorWorker> _log;
    private readonly Action<int> _fatalExit;
    private readonly TradeMapper _tradeMapper;
    private readonly QuoteMapper _quoteMapper;
    private readonly RowHistoryMapper _rowMapper = new();
    private readonly Dictionary<string, TableOptions> _tableOptions;

    // Состояние дедупликации (используется только из ProcessLoop - однопоточно).
    private readonly Dictionary<string, HashSet<(long, char, string)>> _seenTrades = new();
    private readonly Dictionary<(string, string), Dictionary<string, string>> _rowHashes = new();
    private readonly Dictionary<(string, string), QuoteFilter> _quoteFilters = new();
    private readonly HashSet<(string, string)> _warnedKeys = new();
    private DateOnly _seenDay = DateOnly.FromDateTime(DateTime.UtcNow);

    public CollectorWorker(
        IOptions<CollectorOptions> options,
        IOptions<DatabaseOptions> dbOptions,
        ISlotSourceFactory factory,
        IDataStore store,
        CollectorState state,
        ILogger<CollectorWorker> log,
        SchemaMigrator? migrator = null,
        Action<int>? fatalExit = null)
    {
        _options = options.Value;
        _dbOptions = dbOptions.Value;
        _factory = factory;
        _store = store;
        _migrator = migrator;
        _state = state;
        _log = log;
        _fatalExit = fatalExit ?? (code => { Serilog.Log.CloseAndFlush(); Environment.Exit(code); });
        var time = new TimeConverter(_options.ServerTimeZone);
        _tradeMapper = new TradeMapper(time);
        _quoteMapper = new QuoteMapper(time);
        _tableOptions = _options.Tables.Where(t => t.Enabled)
            .GroupBy(t => t.Name.ToUpperInvariant()).ToDictionary(g => g.Key, g => g.First());
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("AtentisServer {Version} стартует. Слотов: {Slots}, таблиц: {Tables}",
            AppInfo.Version, _options.Slots.Count(s => s.Enabled), _tableOptions.Count);
        AppMetrics.BuildInfo.WithLabels(AppInfo.Version).Set(1);

        foreach (var s in _options.Slots.Where(s => s.Enabled))
            _state.Slots.Add(new SlotRuntime(s.Name, s.Market));

        try
        {
            await InitializeDatabaseAsync(stoppingToken);

            var tasks = new List<Task>
            {
                ProcessLoopAsync(stoppingToken),
                SupervisorLoopAsync(stoppingToken),
                StatusLoopAsync(stoppingToken)
            };
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // штатная остановка
        }
        catch (Exception ex)
        {
            _log.LogCritical(ex, "Критическая ошибка сборщика, процесс будет завершён для перезапуска службой");
            _fatalExit(1);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _log.LogInformation("Остановка сервиса: дозаписываю накопленное и отключаю слоты");
        await base.StopAsync(cancellationToken);   // циклы завершены

        // Финальная дозапись: таблицы приходят снимками целиком, поэтому достаточно один раз обработать «грязные».
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            if (_state.DatabaseReady)
            {
                foreach (var (slot, table) in _dirty.TakeNow())
                {
                    var rt = _state.Slots.FirstOrDefault(s => s.Name == slot);
                    if (rt?.Source is null) continue;
                    await ProcessTableAsync(rt, table, cts.Token);
                }
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "Ошибка финальной дозаписи при остановке"); }

        foreach (var rt in _state.Slots)
        {
            if (rt.Source is null) continue;
            try { await rt.Source.StopAsync(); }
            catch (Exception ex) { _log.LogWarning(ex, "Ошибка остановки слота {Slot}", rt.Name); }
        }

        // Помечаем слоты остановленными, чтобы мониторинг отличал штатную остановку от «зависания».
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            foreach (var rt in _state.Slots)
                await _store.UpsertStatusAsync(new SlotStatus(rt.Name, rt.Market, "Stopped", false, null,
                    rt.LastTableUpdateUtc, Interlocked.Read(ref rt.Reconnects), rt.LastError), cts.Token);
        }
        catch (Exception ex) { _log.LogDebug(ex, "Не удалось записать статус Stopped"); }

        _log.LogInformation("AtentisServer остановлен");
    }

    // -------------------------------------------------------------------- инициализация БД

    private async Task InitializeDatabaseAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(2);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_dbOptions.AutoMigrate && _migrator is not null)
                    await _migrator.MigrateAsync(ct);
                else if (!await _store.PingAsync(ct))
                    throw new InvalidOperationException("PostgreSQL недоступен");
                _state.DatabaseReady = true;
                _state.DatabaseUp = true;
                AppMetrics.DbUp.Set(1);
                _log.LogInformation("PostgreSQL готов, схема актуальна");
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AppMetrics.DbUp.Set(0);
                _log.LogError(ex, "Не удалось инициализировать PostgreSQL, повтор через {Delay}", delay);
                await Task.Delay(delay, ct);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 60));
            }
        }
        ct.ThrowIfCancellationRequested();
    }

    // -------------------------------------------------------------------- обработка таблиц

    private async Task ProcessLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            List<(string Slot, string Table)> batch;
            try { batch = await _dirty.TakeAsync(ct); }
            catch (OperationCanceledException) { return; }

            AppMetrics.PendingTables.Set(batch.Count);
            RollDayIfNeeded();

            foreach (var (slot, table) in batch)
            {
                if (ct.IsCancellationRequested) { _dirty.Mark(slot, table); return; }
                var rt = _state.Slots.FirstOrDefault(s => s.Name == slot);
                if (rt?.Source is null) continue;
                try
                {
                    await ProcessTableAsync(rt, table, ct);
                    _state.DatabaseUp = true;
                    AppMetrics.DbUp.Set(1);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    if (ex is Npgsql.NpgsqlException or TimeoutException or System.IO.IOException)
                    {
                        _state.DatabaseUp = false;
                        AppMetrics.DbUp.Set(0);
                    }
                    AppMetrics.ProcessingErrors.WithLabels(slot, table).Inc();
                    rt.LastError = ex.Message;
                    _log.LogError(ex, "Ошибка обработки таблицы {Table} слота {Slot}; повтор через 5 с", table, slot);
                    _ = RetryLaterAsync(slot, table, ct);
                }
            }
            AppMetrics.PendingTables.Set(_dirty.Count);
        }
    }

    private async Task RetryLaterAsync(string slot, string table, CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(5), ct); _dirty.Mark(slot, table); }
        catch (OperationCanceledException) { }
    }

    private void RollDayIfNeeded()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (today == _seenDay) return;
        _seenDay = today;
        _seenTrades.Clear();   // ограничиваем рост памяти; дубли всё равно отсекает UNIQUE + ON CONFLICT
    }

    internal async Task ProcessTableAsync(SlotRuntime rt, string tableName, CancellationToken ct)
    {
        if (!_tableOptions.TryGetValue(tableName.ToUpperInvariant(), out var topt)) return;

        var snapshot = rt.Source!.Snapshot(tableName);
        if (snapshot is null || snapshot.Rows.Count == 0) return;

        var slot = rt.Name;
        AppMetrics.RowsReceived.WithLabels(slot, topt.Name).Inc(snapshot.Rows.Count);

        switch (topt.Name.ToUpperInvariant())
        {
            case "TRADES":
                await StoreTradesAsync(rt, snapshot, topt, ct);
                break;
            case "SECURITIES":
                await StoreQuotesAsync(rt, snapshot, topt, ct);
                break;
            default:
                await StoreRowHistoryAsync(rt, snapshot, topt, ct);
                break;
        }
    }

    private async Task StoreTradesAsync(SlotRuntime rt, DataTable snapshot, TableOptions topt, CancellationToken ct)
    {
        var mapped = _tradeMapper.Map(snapshot);
        ReportSkipped(rt.Name, topt.Name, mapped.Skipped);

        if (!_seenTrades.TryGetValue(rt.Name, out var seen))
            _seenTrades[rt.Name] = seen = new HashSet<(long, char, string)>();

        var fresh = mapped.Items.Where(t => !seen.Contains(t.Key)).ToList();
        var stored = 0;
        for (var i = 0; i < fresh.Count; i += _options.BatchSize)
        {
            var chunk = fresh.Skip(i).Take(_options.BatchSize).ToList();
            stored += await _store.InsertTradesAsync(rt.Name, rt.Market, chunk, ct);
            foreach (var t in chunk) seen.Add(t.Key);   // помечаем только после успешной записи
        }
        if (stored > 0)
        {
            AppMetrics.RowsStored.WithLabels(rt.Name, topt.Name).Inc(stored);
            _log.LogInformation("Слот {Slot}: сохранено новых сделок: {Count} (в снимке {Total})", rt.Name, stored, snapshot.Rows.Count);
        }
    }

    private async Task StoreQuotesAsync(SlotRuntime rt, DataTable snapshot, TableOptions topt, CancellationToken ct)
    {
        var mapped = _quoteMapper.Map(snapshot);
        ReportSkipped(rt.Name, topt.Name, mapped.Skipped);

        var key = (rt.Name, topt.Name);
        if (!_quoteFilters.TryGetValue(key, out var filter))
            _quoteFilters[key] = filter = new QuoteFilter(TimeSpan.FromSeconds(topt.MinIntervalSeconds));

        var fresh = filter.Filter(mapped.Items);
        var stored = 0;
        for (var i = 0; i < fresh.Count; i += _options.BatchSize)
        {
            var chunk = fresh.Skip(i).Take(_options.BatchSize).ToList();
            stored += await _store.InsertQuotesAsync(rt.Name, chunk, ct);
            filter.Commit(chunk);
        }
        if (stored > 0) AppMetrics.RowsStored.WithLabels(rt.Name, topt.Name).Inc(stored);
    }

    private async Task StoreRowHistoryAsync(SlotRuntime rt, DataTable snapshot, TableOptions topt, CancellationToken ct)
    {
        var key = (rt.Name, topt.Name.ToUpperInvariant());
        if (!_rowHashes.TryGetValue(key, out var known))
            _rowHashes[key] = known = await _store.LoadLatestRowHashesAsync(rt.Name, key.Item2, ct);

        var mapped = _rowMapper.Map(snapshot, topt.KeyFields, out var keyMissing);
        if (keyMissing && _warnedKeys.Add(key))
            _log.LogWarning("Таблица {Table} слота {Slot}: ключевые поля {Keys} не найдены в данных; ключом строки будет её содержимое",
                topt.Name, rt.Name, string.Join(",", topt.KeyFields));

        var changed = mapped.Items.Where(r => !known.TryGetValue(r.RowKey, out var h) || h != r.RowHash).ToList();
        var stored = 0;
        for (var i = 0; i < changed.Count; i += _options.BatchSize)
        {
            var chunk = changed.Skip(i).Take(_options.BatchSize).ToList();
            stored += await _store.InsertTableRowsAsync(rt.Name, key.Item2, chunk, ct);
            foreach (var r in chunk) known[r.RowKey] = r.RowHash;
        }
        if (stored > 0) AppMetrics.RowsStored.WithLabels(rt.Name, topt.Name).Inc(stored);
    }

    private void ReportSkipped(string slot, string table, Dictionary<string, int> skipped)
    {
        foreach (var (reason, count) in skipped)
        {
            AppMetrics.RowsSkipped.WithLabels(slot, table, reason).Inc(count);
            _log.LogWarning("Слот {Slot}, таблица {Table}: пропущено строк {Count}, причина {Reason}", slot, table, count, reason);
        }
    }

    // -------------------------------------------------------------------- надзор за слотами

    private async Task SupervisorLoopAsync(CancellationToken ct)
    {
        var lastFullSync = DateTime.UtcNow;
        while (!ct.IsCancellationRequested)
        {
            foreach (var cfg in _options.Slots.Where(s => s.Enabled))
            {
                var rt = _state.Slots.First(s => s.Name == cfg.Name);
                try { await SuperviseSlotAsync(cfg, rt, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex) { _log.LogError(ex, "Ошибка надзора за слотом {Slot}", cfg.Name); }
            }

            if (_options.FullSyncIntervalSeconds > 0 &&
                DateTime.UtcNow - lastFullSync >= TimeSpan.FromSeconds(_options.FullSyncIntervalSeconds))
            {
                lastFullSync = DateTime.UtcNow;
                MarkAllDirty();
            }

            try { await Task.Delay(SupervisorTick, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void MarkAllDirty()
    {
        foreach (var rt in _state.Slots.Where(s => s.IsConnected))
            foreach (var t in _tableOptions.Keys) _dirty.Mark(rt.Name, t);
    }

    internal async Task SuperviseSlotAsync(SlotOptions cfg, SlotRuntime rt, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        if (rt.Source is null)
        {
            if (rt.NextAttemptUtc is { } next && now < next) return;
            await TryStartAsync(cfg, rt, ct);
            return;
        }

        if (rt.IsConnected)
        {
            if (!rt.WasConnected)
            {
                rt.WasConnected = true;
                _log.LogInformation("Слот {Slot} подключён, запрашиваю полную синхронизацию таблиц", rt.Name);
                foreach (var t in _tableOptions.Keys) _dirty.Mark(rt.Name, t);
            }
            rt.DisconnectedSinceUtc = null;
            rt.ConsecutiveFailures = 0;
            return;
        }
        rt.WasConnected = false;

        rt.DisconnectedSinceUtc ??= now;
        var down = now - rt.DisconnectedSinceUtc.Value;
        if (down < TimeSpan.FromSeconds(_options.ReconnectAfterSeconds)) return;

        _log.LogWarning("Слот {Slot} без соединения {Down:F0} с (состояние {State}); пересоздаю подключение",
            rt.Name, down.TotalSeconds, rt.Source.State);
        await DropSourceAsync(rt);
        rt.DisconnectedSinceUtc = null;
        rt.WasConnected = false;
        Interlocked.Increment(ref rt.Reconnects);
        await TryStartAsync(cfg, rt, ct);
    }

    private async Task TryStartAsync(SlotOptions cfg, SlotRuntime rt, CancellationToken ct)
    {
        ISlotSource? source = null;
        try
        {
            source = _factory.Create(cfg, _options.Tables);
            source.TableUpdated += table =>
            {
                var now = DateTime.UtcNow;
                rt.LastTableUpdateUtc = now;
                AppMetrics.TableUpdates.WithLabels(rt.Name, table).Inc();
                AppMetrics.TableLastUpdate.WithLabels(rt.Name, table).Set(new DateTimeOffset(now).ToUnixTimeMilliseconds() / 1000.0);
                _dirty.Mark(rt.Name, table);
            };
            rt.Source = source;
            await source.StartAsync(ct);
            rt.NextAttemptUtc = null;
            _log.LogInformation("Слот {Slot} запущен, жду соединения", rt.Name);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            rt.ConsecutiveFailures++;
            rt.LastError = ex.Message;
            var backoff = TimeSpan.FromSeconds(Math.Min(5 * Math.Pow(2, Math.Min(rt.ConsecutiveFailures, 6)), 300));
            rt.NextAttemptUtc = DateTime.UtcNow + backoff;
            _log.LogError(ex, "Не удалось запустить слот {Slot} (попытка {N}); следующая через {Backoff}",
                rt.Name, rt.ConsecutiveFailures, backoff);
            await DropSourceAsync(rt);

            if (rt.ConsecutiveFailures >= _options.MaxConsecutiveReconnectFailures)
            {
                _log.LogCritical("Слот {Slot}: {N} неудачных запусков подряд - завершаю процесс, чтобы его перезапустила служба Windows",
                    rt.Name, rt.ConsecutiveFailures);
                _fatalExit(1);
            }
        }
    }

    private async Task DropSourceAsync(SlotRuntime rt)
    {
        var src = rt.Source;
        rt.Source = null;
        if (src is null) return;
        try { await src.DisposeAsync(); }
        catch (Exception ex) { _log.LogWarning(ex, "Ошибка освобождения слота {Slot}", rt.Name); }
    }

    // -------------------------------------------------------------------- статус и метрики

    private async Task StatusLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            foreach (var rt in _state.Slots)
            {
                var connected = rt.IsConnected;
                AppMetrics.SlotConnected.WithLabels(rt.Name).Set(connected ? 1 : 0);
                AppMetrics.SlotReconnects.WithLabels(rt.Name).Set(Interlocked.Read(ref rt.Reconnects));
                try
                {
                    await _store.UpsertStatusAsync(new SlotStatus(
                        rt.Name, rt.Market, rt.Source?.State ?? "NotStarted", connected,
                        rt.Source?.LastConnectedUtc, rt.LastTableUpdateUtc,
                        Interlocked.Read(ref rt.Reconnects), rt.Source?.LastError ?? rt.LastError), ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Не удалось записать статус слота {Slot} в БД", rt.Name);
                }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(_options.StatusIntervalSeconds), ct); }
            catch (OperationCanceledException) { return; }
        }
    }
}
