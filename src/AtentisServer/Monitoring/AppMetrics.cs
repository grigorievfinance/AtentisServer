using Prometheus;

namespace AtentisServer.Monitoring;

/// <summary>Все метрики Prometheus сервиса (префикс atentis_). Эндпоинт: GET /metrics.</summary>
public static class AppMetrics
{
    private static readonly string[] SlotLabel = { "slot" };
    private static readonly string[] SlotTable = { "slot", "table" };

    public static readonly Gauge BuildInfo = Metrics.CreateGauge(
        "atentis_build_info", "Версия сервиса.", new GaugeConfiguration { LabelNames = new[] { "version" } });

    public static readonly Gauge SlotConnected = Metrics.CreateGauge(
        "atentis_slot_connected", "1 - слот Atentis подключён.", SlotLabel);

    public static readonly Gauge SlotReconnects = Metrics.CreateGauge(
        "atentis_slot_reconnects", "Число пересозданий слота с момента старта сервиса.", SlotLabel);

    public static readonly Counter TableUpdates = Metrics.CreateCounter(
        "atentis_table_updates_total", "Получено обновлений таблиц от Atentis.", SlotTable);

    public static readonly Gauge TableLastUpdate = Metrics.CreateGauge(
        "atentis_table_last_update_timestamp_seconds", "Unix-время последнего обновления таблицы.", SlotTable);

    public static readonly Counter RowsReceived = Metrics.CreateCounter(
        "atentis_rows_received_total", "Строк прочитано из таблиц Atentis.", SlotTable);

    public static readonly Counter RowsStored = Metrics.CreateCounter(
        "atentis_rows_stored_total", "Новых строк записано в PostgreSQL.", SlotTable);

    public static readonly Counter RowsSkipped = Metrics.CreateCounter(
        "atentis_rows_skipped_total", "Строк пропущено из-за некорректных данных.",
        new CounterConfiguration { LabelNames = new[] { "slot", "table", "reason" } });

    public static readonly Counter ProcessingErrors = Metrics.CreateCounter(
        "atentis_processing_errors_total", "Ошибки обработки таблиц (будет повтор).", SlotTable);

    public static readonly Histogram DbWriteSeconds = Metrics.CreateHistogram(
        "atentis_db_write_duration_seconds", "Длительность пакетной записи в PostgreSQL.",
        new HistogramConfiguration { LabelNames = new[] { "target" }, Buckets = Histogram.ExponentialBuckets(0.001, 2, 14) });

    public static readonly Counter DbErrors = Metrics.CreateCounter(
        "atentis_db_errors_total", "Ошибки записи в PostgreSQL.", new CounterConfiguration { LabelNames = new[] { "target" } });

    public static readonly Gauge PendingTables = Metrics.CreateGauge(
        "atentis_pending_tables", "Таблиц ожидает обработки (грязные, ещё не записанные).");

    public static readonly Gauge DbUp = Metrics.CreateGauge("atentis_db_up", "1 - PostgreSQL доступен.");
}
