using System.Data;
using AtentisServer.Configuration;
using AtentisServer.Mapping;
using AtentisServer.Monitoring;
using AtentisServer.Security;
using AtentisServer.Sources;
using AtentisServer.Storage;
using Microsoft.Extensions.Options;

namespace AtentisServer.Collection;

/// <summary>
/// Ядро сервиса: подключает слоты Atentis (пароли берёт из памяти, куда их ввёл пользователь), забирает обновлённые таблицы
/// и идемпотентно пишет их в PostgreSQL. Никакой бизнес-логики (финрез, пары, сделки) - только сбор и сохранение.
/// Процесс НЕ перезапускается из-за проблем со связью: пароли хранятся только в памяти, поэтому все сбои
/// (потеря связи с Atentis, недоступная БД, ошибки данных) лечатся переподключением внутри процесса.
/// </summary>
public sealed class CollectorWorker : BackgroundService
{
    private static readonly TimeSpan SupervisorTick = TimeSpan.FromSeconds(5);

    private readonly IOptionsMonitor<CollectorOptions> _optionsMonitor;
    private readonly ISlotSourceFactory _factory;
    private readonly IDataStore _store;
    private readonly IDatabaseGate _db;
    private readonly CredentialStore _credentials;
    private readonly CollectorState _state;
    private readonly DirtyTracker _dirty = new();
    private readonly ILogger<CollectorWorker> _log;
    private readonly Action<int> _fatalExit;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly RowHistoryMapper _rowMapper = new();

    // Кэш, зависящий от текущей конфигурации (пересчитывается при её смене).
    private CollectorOptions? _cfgSource;
    private Dictionary<string, TableOptions> _tableOptions = new();
    private TradeMapper _tradeMapper = new(new TimeConverter("Europe/Moscow"));
    private QuoteMapper _quoteMapper = new(new TimeConverter("Europe/Moscow"));

    // Состояние дедупликации (используется только из ProcessLoop - однопоточно).
    private readonly Dictionary<string, HashSet<(long, char, string)>> _seenTrades = new();
    private readonly Dictionary<(string, string), Dictionary<string, string>> _rowHashes = new();
    private readonly Dictionary<(string, string), QuoteFilter> _quoteFilters = new();
    private readonly HashSet<(string, string)> _warnedKeys = new();
    private DateOnly _seenDay = DateOnly.FromDateTime(DateTime.UtcNow);

    private long _dbGeneration = -1;
    private DateTime _dbNextAttemptUtc = DateTime.MinValue;
    private int _dbFailures;
    private string? _lastConfigError;

    public CollectorWorker(
        IOptionsMonitor<CollectorOptions> options,
        ISlotSourceFactory factory,
        IDataStore store,
        IDatabaseGate db,
        CredentialStore credentials,
        CollectorState state,
        ILogger<CollectorWorker> log,
        Action<int>? fatalExit = null)
    {
        _optionsMonitor = options;
        _factory = factory;
        _store = store;
        _db = db;
        _credentials = credentials;
        _state = state;
        _log = log;
        _fatalExit = fatalExit ?? (code => { Serilog.Log.CloseAndFlush(); Environment.Exit(code); });
        _ = Options;   // заполняет кэш
        options.OnChange(_ => Wake());
        credentials.Changed += Wake;
    }

    /// <summary>Текущая конфигурация (при смене пересчитывает таблицы и преобразователи).</summary>
    internal CollectorOptions Options
    {
        get
        {
            var current = _optionsMonitor.CurrentValue;
            if (!ReferenceEquals(current, _cfgSource))
            {
                _cfgSource = current;
                _tableOptions = current.Tables.Where(t => t.Enabled)
                    .GroupBy(t => t.Name.ToUpperInvariant()).ToDictionary(g => g.Key, g => g.First());
                var time = new TimeConverter(current.ServerTimeZone);
                _tradeMapper = new TradeMapper(time);
                _quoteMapper = new QuoteMapper(time);
            }
            return current;
        }
    }

    /// <summary>Разбудить супервизор (изменились настройки или пароли).</summary>
    public void Wake()
    {
        try { _wake.Release(); } catch (SemaphoreFullException) { /* уже разбужен */ }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = Options;
        _log.LogInformation("AtentisServer {Version} стартует. Слотов в настройках: {Slots}, таблиц: {Tables}",
            AppInfo.Version, o.Slots.Count, _tableOptions.Count);
        AppMetrics.BuildInfo.WithLabels(AppInfo.Version).Set(1);

        try
        {
            await Task.WhenAll(
                ProcessLoopAsync(stoppingToken),
                SupervisorLoopAsync(stoppingToken),
                StatusLoopAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // штатная остановка
        }
        catch (Exception ex)
        {
            // Сюда попадать не должны: каждый цикл защищает свои итерации. Если всё же попали - единственный выход.
            _log.LogCritical(ex, "Критическая ошибка сборщика, процесс будет завершён для перезапуска службой (пароли Atentis придётся ввести заново)");
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
                    var rt = _state.Slots.Find(slot);
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
        if (_state.DatabaseReady)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                foreach (var rt in _state.Slots)
                    await _store.UpsertStatusAsync(new SlotStatus(rt.Name, rt.Market, "Stopped", false, null,
                        rt.LastTableUpdateUtc, Interlocked.Read(ref rt.Reconnects), rt.LastError), cts.Token);
            }
            catch (Exception ex) { _log.LogDebug(ex, "Не удалось записать статус Stopped"); }
        }

        _log.LogInformation("AtentisServer остановлен");
    }

    // -------------------------------------------------------------------- база данных

    /// <summary>Приводит БД в рабочее состояние: проверяет настройки, создаёт схему. Вызывается каждый такт супервизора.</summary>
    internal async Task EnsureDatabaseAsync(CancellationToken ct)
    {
        _state.DatabaseConfigured = _db.IsConfigured;
        if (!_db.IsConfigured)
        {
            _state.DatabaseReady = false;
            _state.DatabaseUp = false;
            _state.DatabaseMessage = "База данных не настроена";
            AppMetrics.DbUp.Set(0);
            _dbGeneration = -1;
            return;
        }

        var generation = _db.Generation;
        if (_state.DatabaseReady && generation == _dbGeneration) return;
        if (generation != _dbGeneration)
        {
            _state.DatabaseReady = false;          // параметры сменились - схему проверяем заново
            _dbFailures = 0;
            _dbNextAttemptUtc = DateTime.MinValue;
        }
        if (DateTime.UtcNow < _dbNextAttemptUtc) return;

        try
        {
            await _db.EnsureReadyAsync(ct);
            _dbGeneration = generation;
            _dbFailures = 0;
            _state.DatabaseReady = true;
            _state.DatabaseUp = true;
            _state.DatabaseMessage = null;
            AppMetrics.DbUp.Set(1);
            _log.LogInformation("PostgreSQL готов, схема актуальна");
            Wake();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _dbFailures++;
            var delay = TimeSpan.FromSeconds(Math.Min(2 * Math.Pow(2, Math.Min(_dbFailures, 5)), 60));
            _dbNextAttemptUtc = DateTime.UtcNow + delay;
            _state.DatabaseReady = false;
            _state.DatabaseUp = false;
            _state.DatabaseMessage = DbErrorTranslator.Translate(ex);
            AppMetrics.DbUp.Set(0);
            _log.LogError(ex, "Не удалось подготовить PostgreSQL ({Message}), повтор через {Delay}", _state.DatabaseMessage, delay);
        }
    }

    // -------------------------------------------------------------------- обработка таблиц

    private async Task ProcessLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            List<(string Slot, string Table)> batch;
            try { batch = await _dirty.TakeAsync(ct); }
            catch (OperationCanceledException) { return; }

            if (!_state.DatabaseReady)
            {
                // БД не готова: возвращаем таблицы в очередь и ждём (Atentis хранит сделки сессии, ничего не теряется).
                foreach (var (slot, table) in batch) _dirty.Mark(slot, table);
                try { await Task.Delay(TimeSpan.FromSeconds(2), ct); } catch (OperationCanceledException) { return; }
                continue;
            }

            AppMetrics.PendingTables.Set(batch.Count);
            RollDayIfNeeded();

            foreach (var (slot, table) in batch)
            {
                if (ct.IsCancellationRequested) { _dirty.Mark(slot, table); return; }
                var rt = _state.Slots.Find(slot);
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
                        _state.DatabaseMessage = DbErrorTranslator.Translate(ex);
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
        _ = Options;   // обновить кэш при смене конфигурации
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

        var batchSize = Options.BatchSize;
        var fresh = mapped.Items.Where(t => !seen.Contains(t.Key)).ToList();
        var stored = 0;
        for (var i = 0; i < fresh.Count; i += batchSize)
        {
            var chunk = fresh.Skip(i).Take(batchSize).ToList();
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

        var batchSize = Options.BatchSize;
        var fresh = filter.Filter(mapped.Items);
        var stored = 0;
        for (var i = 0; i < fresh.Count; i += batchSize)
        {
            var chunk = fresh.Skip(i).Take(batchSize).ToList();
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

        var batchSize = Options.BatchSize;
        var changed = mapped.Items.Where(r => !known.TryGetValue(r.RowKey, out var h) || h != r.RowHash).ToList();
        var stored = 0;
        for (var i = 0; i < changed.Count; i += batchSize)
        {
            var chunk = changed.Skip(i).Take(batchSize).ToList();
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
            try
            {
                await SuperviseOnceAsync(ct);

                var o = Options;
                if (o.FullSyncIntervalSeconds > 0 &&
                    DateTime.UtcNow - lastFullSync >= TimeSpan.FromSeconds(o.FullSyncIntervalSeconds))
                {
                    lastFullSync = DateTime.UtcNow;
                    MarkAllDirty();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                // Любая неожиданная ошибка итерации не должна убивать цикл (иначе пропадёт переподключение).
                _log.LogError(ex, "Ошибка в цикле надзора за слотами; продолжаю");
            }

            try { await _wake.WaitAsync(SupervisorTick, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Одна итерация надзора: проверка конфигурации, БД и всех слотов.</summary>
    internal async Task SuperviseOnceAsync(CancellationToken ct)
    {
        var o = Options;

        var errors = new CollectorOptionsValidator().Validate(null, o);
        _state.ConfigError = errors.Failed ? string.Join(" ", errors.Failures!) : null;
        if (errors.Failed)
        {
            var message = string.Join(" ", errors.Failures!);
            if (message != _lastConfigError) _log.LogError("Ошибка в настройках: {Errors}", message);
            _lastConfigError = message;
        }
        else _lastConfigError = null;

        await EnsureDatabaseAsync(ct);

        // Приводим список слотов в соответствие с настройками.
        foreach (var cfg in o.Slots)
        {
            var rt = _state.Slots.Find(cfg.Name);
            if (rt is null) { rt = new SlotRuntime(cfg.Name, cfg.Market); _state.Slots.Add(rt); }
            rt.Market = cfg.Market;
        }
        foreach (var rt in _state.Slots.Where(r => o.Slots.All(c => !string.Equals(c.Name, r.Name, StringComparison.OrdinalIgnoreCase))))
        {
            await DropSourceAsync(rt);
            _state.Slots.Remove(rt);
        }

        if (errors.Failed) return;   // с некорректными настройками слоты не трогаем

        foreach (var cfg in o.Slots)
        {
            var rt = _state.Slots.Find(cfg.Name)!;
            try { await SuperviseSlotAsync(cfg, rt, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { _log.LogError(ex, "Ошибка надзора за слотом {Slot}", cfg.Name); }
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

        // 1. Выключен или не настроен: слот не должен работать.
        if (!cfg.Enabled || !cfg.IsConfigured)
        {
            if (rt.Source is not null) { _log.LogInformation("Слот {Slot}: отключён настройками", rt.Name); await DropSourceAsync(rt); }
            return;
        }

        // 2. Изменились сервер/порт/логин: старое подключение больше не годится.
        var identity = cfg.Identity;
        if (rt.Source is not null && rt.Identity != identity)
        {
            _log.LogInformation("Слот {Slot}: параметры подключения изменены, переподключаюсь", rt.Name);
            await DropSourceAsync(rt);
        }

        // 3. Нет пароля (не вводили, забыт, отвергнут Atentis): подключаться нельзя, ждём ввода.
        if (!_credentials.TryGet(rt.Name, out var password, out var version))
        {
            if (rt.Source is not null) await DropSourceAsync(rt);
            return;
        }

        // 4. Введён новый пароль: переподключаемся с ним.
        if (rt.Source is not null && rt.CredentialVersion != version)
        {
            _log.LogInformation("Слот {Slot}: введён новый пароль, переподключаюсь", rt.Name);
            await DropSourceAsync(rt);
        }

        if (rt.Source is null)
        {
            if (rt.NextAttemptUtc is { } next && now < next) return;
            await TryStartAsync(cfg, rt, password, version, ct);
            return;
        }

        if (rt.IsConnected)
        {
            if (!rt.WasConnected)
            {
                rt.WasConnected = true;
                rt.EverConnected = true;
                rt.LastError = null;
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
        if (down < TimeSpan.FromSeconds(Options.ReconnectAfterSeconds)) return;

        _log.LogWarning("Слот {Slot} без соединения {Down:F0} с (состояние {State}); пересоздаю подключение",
            rt.Name, down.TotalSeconds, rt.Source.State);
        await DropSourceAsync(rt);
        rt.DisconnectedSinceUtc = null;
        Interlocked.Increment(ref rt.Reconnects);
        await TryStartAsync(cfg, rt, password, version, ct);
    }

    private async Task TryStartAsync(SlotOptions cfg, SlotRuntime rt, string password, long version, CancellationToken ct)
    {
        ISlotSource? source = null;
        try
        {
            rt.AuthFailed = false;
            rt.AuthMessage = null;
            source = _factory.Create(cfg, password, Options.Tables);
            source.TableUpdated += table =>
            {
                var now = DateTime.UtcNow;
                rt.LastTableUpdateUtc = now;
                AppMetrics.TableUpdates.WithLabels(rt.Name, table).Inc();
                AppMetrics.TableLastUpdate.WithLabels(rt.Name, table).Set(new DateTimeOffset(now).ToUnixTimeMilliseconds() / 1000.0);
                _dirty.Mark(rt.Name, table);
            };
            source.AuthenticationFailed += message => OnAuthenticationFailed(rt, version, message);
            rt.Source = source;
            rt.Identity = cfg.Identity;
            rt.CredentialVersion = version;
            await source.StartAsync(ct);
            rt.NextAttemptUtc = null;
            _log.LogInformation("Слот {Slot} запущен, жду соединения", rt.Name);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Повторяем с нарастающей паузой (максимум 5 минут), процесс не завершаем - пароль хранится только в памяти.
            rt.ConsecutiveFailures++;
            rt.LastError = ex.Message;
            var backoff = TimeSpan.FromSeconds(Math.Min(5 * Math.Pow(2, Math.Min(rt.ConsecutiveFailures, 6)), 300));
            rt.NextAttemptUtc = DateTime.UtcNow + backoff;
            _log.LogError(ex, "Не удалось запустить слот {Slot} (попытка {N}); следующая через {Backoff}",
                rt.Name, rt.ConsecutiveFailures, backoff);
            await DropSourceAsync(rt);
        }
    }

    /// <summary>Atentis отклонил пароль: забываем его и больше не подключаемся, пока пользователь не введёт новый (иначе можно заблокировать учётную запись).</summary>
    private void OnAuthenticationFailed(SlotRuntime rt, long version, string message)
    {
        if (_credentials.ClearIfVersion(rt.Name, version))
        {
            rt.AuthFailed = true;
            rt.AuthMessage = message;
            rt.LastError = message;
            _log.LogError("Слот {Slot}: {Message} Пароль удалён из памяти, подключение остановлено до ввода нового.", rt.Name, message);
        }
        Wake();   // супервизор сразу закроет подключение
    }

    private async Task DropSourceAsync(SlotRuntime rt)
    {
        var src = rt.Source;
        rt.Source = null;
        rt.Identity = null;
        rt.WasConnected = false;
        if (src is null) return;
        try { await src.DisposeAsync(); }
        catch (Exception ex) { _log.LogWarning(ex, "Ошибка освобождения слота {Slot}", rt.Name); }
    }

    // -------------------------------------------------------------------- статус и метрики

    private async Task StatusLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                foreach (var rt in _state.Slots)
                {
                    var connected = rt.IsConnected;
                    AppMetrics.SlotConnected.WithLabels(rt.Name).Set(connected ? 1 : 0);
                    AppMetrics.SlotReconnects.WithLabels(rt.Name).Set(Interlocked.Read(ref rt.Reconnects));
                    var cfg = Options.Slots.FirstOrDefault(s => string.Equals(s.Name, rt.Name, StringComparison.OrdinalIgnoreCase));
                    var waiting = cfg is { Enabled: true } && cfg.IsConfigured && !_credentials.Has(rt.Name);
                    AppMetrics.SlotWaitingForPassword.WithLabels(rt.Name).Set(waiting ? 1 : 0);

                    if (!_state.DatabaseReady) continue;
                    try
                    {
                        await _store.UpsertStatusAsync(new SlotStatus(
                            rt.Name, rt.Market, rt.Source?.State ?? (waiting ? "WaitingForPassword" : "NotStarted"), connected,
                            rt.Source?.LastConnectedUtc, rt.LastTableUpdateUtc,
                            Interlocked.Read(ref rt.Reconnects), rt.Source?.LastError ?? rt.LastError), ct);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "Не удалось записать статус слота {Slot} в БД", rt.Name);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { _log.LogError(ex, "Ошибка обновления статуса; продолжаю"); }

            try { await Task.Delay(TimeSpan.FromSeconds(Options.StatusIntervalSeconds), ct); }
            catch (OperationCanceledException) { return; }
        }
    }
}
