using AtentisServer.Contracts;
using AtentisServer.Storage;
using Npgsql;

namespace AtentisServer.Control;

public interface IDatabaseTester
{
    Task<(bool Ok, string Message)> TestAsync(DatabaseSettingsDto db, string? existingConnectionString, CancellationToken ct);
}

/// <summary>Проверка подключения к PostgreSQL из «Панели управления» (кнопка «Проверить»).</summary>
public sealed class DatabaseTester : IDatabaseTester
{
    public async Task<(bool Ok, string Message)> TestAsync(DatabaseSettingsDto db, string? existingConnectionString, CancellationToken ct)
    {
        var errors = SettingsValidator.ValidateDatabase(db);
        if (errors.Count > 0) return (false, string.Join(" ", errors));

        try
        {
            var cs = new NpgsqlConnectionStringBuilder(LocalSettingsStore.BuildConnectionString(db, existingConnectionString))
            {
                Timeout = 8,
                CommandTimeout = 8,
                ApplicationName = "AtentisServer (проверка)",
                Pooling = false
            }.ConnectionString;

            await using var conn = new NpgsqlConnection(cs);
            await conn.OpenAsync(ct);

            await using var cmd = new NpgsqlCommand(
                "SELECT has_database_privilege(current_user, current_database(), 'CREATE'), " +
                "EXISTS (SELECT 1 FROM information_schema.schemata WHERE schema_name = 'atentis')", conn);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            var canCreate = r.GetBoolean(0);
            var schemaExists = r.GetBoolean(1);
            if (!canCreate && !schemaExists)
                return (false, "Подключение есть, но у пользователя нет права создавать таблицы в этой базе. Попросите администратора выдать право (см. инструкцию).");
            return (true, schemaExists ? "Подключение успешно. Таблицы AtentisServer уже созданы." : "Подключение успешно. Таблицы будут созданы автоматически.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return (false, DbErrorTranslator.Translate(ex));
        }
    }
}
