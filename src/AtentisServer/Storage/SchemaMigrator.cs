using System.Reflection;
using Npgsql;

namespace AtentisServer.Storage;

/// <summary>
/// Минимальный мигратор: применяет встроенные SQL-файлы Storage/Sql/NNN_name.sql по порядку,
/// один раз, под advisory-lock (безопасно при случайном параллельном запуске двух экземпляров).
/// </summary>
public sealed class SchemaMigrator
{
    private const long AdvisoryLockKey = 0x41544E54_4953; // "ATNTIS"

    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<SchemaMigrator> _log;

    public SchemaMigrator(NpgsqlDataSource dataSource, ILogger<SchemaMigrator> log)
    {
        _dataSource = dataSource;
        _log = log;
    }

    public async Task MigrateAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await Exec(conn, "SELECT pg_advisory_lock(@k)", ct, ("k", AdvisoryLockKey));
        try
        {
            await Exec(conn, "CREATE SCHEMA IF NOT EXISTS atentis", ct);
            await Exec(conn, """
                CREATE TABLE IF NOT EXISTS atentis.schema_migrations (
                    version int PRIMARY KEY, name text NOT NULL, applied_at timestamptz NOT NULL DEFAULT now())
                """, ct);

            var applied = new HashSet<int>();
            await using (var cmd = new NpgsqlCommand("SELECT version FROM atentis.schema_migrations", conn))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct)) applied.Add(r.GetInt32(0));

            foreach (var (version, name, sql) in LoadScripts())
            {
                if (applied.Contains(version)) continue;
                _log.LogInformation("Применяю миграцию {Version}: {Name}", version, name);
                await using var tx = await conn.BeginTransactionAsync(ct);
                await using (var cmd = new NpgsqlCommand(sql, conn, tx)) await cmd.ExecuteNonQueryAsync(ct);
                await using (var cmd = new NpgsqlCommand(
                    "INSERT INTO atentis.schema_migrations (version, name) VALUES (@v, @n)", conn, tx))
                {
                    cmd.Parameters.AddWithValue("v", version);
                    cmd.Parameters.AddWithValue("n", name);
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                await tx.CommitAsync(ct);
            }
        }
        finally
        {
            await Exec(conn, "SELECT pg_advisory_unlock(@k)", CancellationToken.None, ("k", AdvisoryLockKey));
        }
    }

    internal static IEnumerable<(int Version, string Name, string Sql)> LoadScripts()
    {
        var asm = typeof(SchemaMigrator).Assembly;
        foreach (var res in asm.GetManifestResourceNames().Where(n => n.EndsWith(".sql")).OrderBy(n => n, StringComparer.Ordinal))
        {
            var file = res[..^4].Split('.').Last();            // 001_init
            var us = file.IndexOf('_');
            if (us <= 0 || !int.TryParse(file[..us], out var version))
                throw new InvalidOperationException($"Некорректное имя файла миграции: {res}");
            using var s = asm.GetManifestResourceStream(res)!;
            using var rd = new StreamReader(s);
            yield return (version, file[(us + 1)..], rd.ReadToEnd());
        }
    }

    private static async Task Exec(NpgsqlConnection conn, string sql, CancellationToken ct, params (string, object)[] ps)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
