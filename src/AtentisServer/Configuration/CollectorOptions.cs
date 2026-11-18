using System.ComponentModel.DataAnnotations;

namespace AtentisServer.Configuration;

/// <summary>Корневая секция конфигурации "Collector".</summary>
public sealed class CollectorOptions
{
    public const string SectionName = "Collector";

    /// <summary>Часовой пояс времени, которое присылает сервер Atentis (naive DateTime). У МосБиржи - Europe/Moscow.</summary>
    public string ServerTimeZone { get; set; } = "Europe/Moscow";

    /// <summary>Как часто (сек) помечать все таблицы «грязными» для страховочной полной синхронизации. 0 = выключено.</summary>
    [Range(0, 86400)]
    public int FullSyncIntervalSeconds { get; set; } = 300;

    /// <summary>Максимум строк в одном INSERT-батче.</summary>
    [Range(1, 50000)]
    public int BatchSize { get; set; } = 1000;

    /// <summary>Сколько секунд слот может быть без соединения, прежде чем он будет пересоздан. Процесс при этом НЕ перезапускается
    /// (пароли Atentis хранятся только в памяти и пропали бы при перезапуске).</summary>
    [Range(10, 3600)]
    public int ReconnectAfterSeconds { get; set; } = 60;

    /// <summary>Как часто (сек) писать состояние в atentis.collector_status.</summary>
    [Range(5, 3600)]
    public int StatusIntervalSeconds { get; set; } = 15;

    /// <summary>Слоты Atentis (обычно два: фондовый рынок и срочный).</summary>
    public List<SlotOptions> Slots { get; set; } = new();

    /// <summary>Таблицы Atentis, которые собираются.</summary>
    public List<TableOptions> Tables { get; set; } = new();
}

public sealed class SlotOptions
{
    /// <summary>Уникальный идентификатор слота, попадает в БД (поле slot_id) и в метрики, например "MICEX".</summary>
    [Required]
    public string Name { get; set; } = "";

    /// <summary>Тип рынка, попадает в БД (поле market): SPOT или FUT.</summary>
    [Required]
    public string Market { get; set; } = "";

    public bool Enabled { get; set; } = true;

    /// <summary>Адрес сервера Atentis. Пустой = слот ещё не настроен.</summary>
    public string Server { get; set; } = "";

    public int Port { get; set; }

    public string Login { get; set; } = "";

    // Пароль Atentis в настройках НЕ хранится: его вводят вручную в «Панели управления», он живёт только в памяти службы.

    /// <summary>Файл открытого ключа для защищённого соединения; пусто = незащищённое.</summary>
    public string PublicKeyFile { get; set; } = "";

    /// <summary>Период обновления таблиц, мс.</summary>
    [Range(100, 60000)]
    public int RefreshPeriodMs { get; set; } = 1000;

    /// <summary>Заполнены ли сервер, порт и логин (иначе слот показывается как «не настроен»).</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Server) && Port is >= 1 and <= 65535 && !string.IsNullOrWhiteSpace(Login);

    /// <summary>Параметры, при изменении которых слот надо пересоздать (и пароль, скорее всего, придётся ввести заново).</summary>
    public string Identity => $"{Server.Trim()}|{Port}|{Login.Trim()}|{PublicKeyFile}|{RefreshPeriodMs}";
}

public sealed class TableOptions
{
    /// <summary>Имя таблицы Atentis: TRADES, ORDERS, POSITIONS, FIRM_HOLDING_TOTAL, SECURITIES.</summary>
    [Required]
    public string Name { get; set; } = "";

    public bool Enabled { get; set; } = true;

    /// <summary>Необязательный фильтр сервера, например "SecBoard = 'TQBR'".</summary>
    public string Filter { get; set; } = "";

    /// <summary>
    /// Ключевые поля строки для таблиц, которые пишутся как история изменений (ORDERS, POSITIONS, FIRM_HOLDING_TOTAL).
    /// </summary>
    public List<string> KeyFields { get; set; } = new();

    /// <summary>Минимальный интервал (сек) между записями одной и той же строки (для SECURITIES).</summary>
    [Range(0, 86400)]
    public int MinIntervalSeconds { get; set; }
}

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>Выполнять миграции схемы при старте.</summary>
    public bool AutoMigrate { get; set; } = true;

    [Range(1, 600)]
    public int CommandTimeoutSeconds { get; set; } = 30;
}

public sealed class MonitoringOptions
{
    public const string SectionName = "Monitoring";

    /// <summary>Адрес HTTP-сервера метрик и health-check. По умолчанию только localhost.</summary>
    public string Url { get; set; } = "http://127.0.0.1:9464";
}

public sealed class ControlOptions
{
    public const string SectionName = "Control";

    /// <summary>Имя именованного канала управления.</summary>
    public string PipeName { get; set; } = AtentisServer.Contracts.ControlPipe.Name;

    /// <summary>Группа Windows, члены которой могут управлять службой из «Панели управления».</summary>
    public string OperatorsGroup { get; set; } = AtentisServer.Contracts.ControlPipe.OperatorsGroup;
}
