using Microsoft.Extensions.Primitives;
using Npgsql;

namespace AtentisServer.Storage;

public sealed class DatabaseNotConfiguredException : InvalidOperationException
{
    public DatabaseNotConfiguredException() : base("База данных не настроена.") { }
}

/// <summary>
/// Держит текущее подключение к PostgreSQL и пересоздаёт его на лету, когда пользователь меняет настройки базы в «Панели управления»
/// (перезапускать службу нельзя: пароли Atentis хранятся только в памяти).
/// </summary>
public sealed class NpgsqlDataSourceProvider : IDisposable
{
    private readonly ILogger<NpgsqlDataSourceProvider>? _log;
    private readonly object _gate = new();
    private NpgsqlDataSource? _current;
    private string _currentString = "";
    private long _generation;
    private IDisposable? _subscription;

    /// <summary>Подключение берётся из ConnectionStrings:Postgres и обновляется при перечитывании конфигурации.</summary>
    public NpgsqlDataSourceProvider(IConfiguration? configuration, ILogger<NpgsqlDataSourceProvider>? log = null)
    {
        _log = log;
        if (configuration is null) return;
        Configure(configuration.GetConnectionString("Postgres"));
        if (configuration is IConfigurationRoot root)
        {
            _subscription = ChangeToken.OnChange(root.GetReloadToken,
                () => Configure(root.GetConnectionString("Postgres")));
        }
    }

    public NpgsqlDataSourceProvider(string? connectionString) : this((IConfiguration?)null) => Configure(connectionString);

    public bool IsConfigured => Current is not null;

    /// <summary>Увеличивается при каждой смене строки подключения.</summary>
    public long Generation => Interlocked.Read(ref _generation);

    public NpgsqlDataSource? Current { get { lock (_gate) return _current; } }

    public NpgsqlDataSource Require() => Current ?? throw new DatabaseNotConfiguredException();

    /// <summary>Строка подключения в виде, пригодном для NpgsqlConnectionStringBuilder, либо null, если база не настроена.</summary>
    public static string? Normalize(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return null;
        try
        {
            var b = new NpgsqlConnectionStringBuilder(connectionString);
            if (string.IsNullOrWhiteSpace(b.Host) || string.IsNullOrWhiteSpace(b.Username)) return null;
            b.ApplicationName = "AtentisServer";
            return b.ConnectionString;
        }
        catch (ArgumentException) { return null; }
    }

    public void Configure(string? connectionString)
    {
        var normalized = Normalize(connectionString) ?? "";
        NpgsqlDataSource? old;
        lock (_gate)
        {
            if (normalized == _currentString) return;
            old = _current;
            _current = normalized.Length == 0 ? null : new NpgsqlDataSourceBuilder(normalized).Build();
            _currentString = normalized;
            Interlocked.Increment(ref _generation);
        }
        _log?.LogInformation("Параметры подключения к PostgreSQL изменены ({State})", normalized.Length == 0 ? "не настроено" : "применены");
        if (old is not null)
        {
            // Даём завершиться выполняющимся запросам и освобождаем старый пул.
            _ = Task.Run(async () => { await Task.Delay(TimeSpan.FromSeconds(30)); await old.DisposeAsync(); });
        }
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        lock (_gate) { _current?.Dispose(); _current = null; }
    }
}
