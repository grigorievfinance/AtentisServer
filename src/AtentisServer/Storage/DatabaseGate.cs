using AtentisServer.Configuration;
using Microsoft.Extensions.Options;

namespace AtentisServer.Storage;

/// <summary>Готовность базы данных для сборщика: настроена ли, актуальна ли схема.</summary>
public interface IDatabaseGate
{
    bool IsConfigured { get; }

    /// <summary>Увеличивается при смене параметров подключения - тогда схему нужно проверить заново.</summary>
    long Generation { get; }

    Task EnsureReadyAsync(CancellationToken ct);
}

public sealed class PostgresDatabaseGate : IDatabaseGate
{
    private readonly NpgsqlDataSourceProvider _provider;
    private readonly SchemaMigrator _migrator;
    private readonly IDataStore _store;
    private readonly DatabaseOptions _options;

    public PostgresDatabaseGate(NpgsqlDataSourceProvider provider, SchemaMigrator migrator, IDataStore store, IOptions<DatabaseOptions> options)
    {
        _provider = provider;
        _migrator = migrator;
        _store = store;
        _options = options.Value;
    }

    public bool IsConfigured => _provider.IsConfigured;
    public long Generation => _provider.Generation;

    public async Task EnsureReadyAsync(CancellationToken ct)
    {
        if (_options.AutoMigrate) await _migrator.MigrateAsync(ct);
        else if (!await _store.PingAsync(ct)) throw new InvalidOperationException("PostgreSQL недоступен");
    }
}
