using System.Text.Json;
using System.Text.Json.Nodes;
using AtentisServer.Configuration;
using AtentisServer.Contracts;
using AtentisServer.Storage;
using Microsoft.Extensions.Options;
using Npgsql;

namespace AtentisServer.Control;

public interface ILocalSettingsStore
{
    string FilePath { get; }

    /// <summary>Текущие действующие настройки (пароль БД наружу не отдаётся).</summary>
    SettingsDto Read();

    /// <summary>Строка подключения, действующая сейчас (с паролем) - только для внутреннего использования службой.</summary>
    string? CurrentConnectionString { get; }

    /// <summary>Сохраняет настройки в файл и сразу применяет их (без перезапуска службы).</summary>
    void Save(SettingsDto settings);
}

/// <summary>
/// Файл настроек %ProgramData%\AtentisServer\appsettings.local.json. Службой записывается только то, что вводит пользователь
/// (подключение к БД и параметры рынков); остальные ключи файла (расширенные настройки вручную) сохраняются как есть.
/// Пароли Atentis сюда НЕ записываются никогда.
/// </summary>
public sealed class LocalSettingsStore : ILocalSettingsStore
{
    private static readonly JsonDocumentOptions ReadOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly IConfigurationRoot _config;
    private readonly IOptionsMonitor<CollectorOptions> _options;
    private readonly object _gate = new();

    public LocalSettingsStore(IConfigurationRoot config, IOptionsMonitor<CollectorOptions> options, string filePath)
    {
        _config = config;
        _options = options;
        FilePath = filePath;
    }

    public string FilePath { get; }

    public string? CurrentConnectionString => _config.GetConnectionString("Postgres");

    public SettingsDto Read()
    {
        var dto = new SettingsDto { Database = ParseDatabase(CurrentConnectionString) };
        foreach (var s in _options.CurrentValue.Slots)
        {
            dto.Slots.Add(new SlotSettingsDto
            {
                Name = s.Name, Market = s.Market, Enabled = s.Enabled, Server = s.Server, Port = s.Port,
                Login = s.Login, PublicKeyFile = s.PublicKeyFile
            });
        }
        return dto;
    }

    public static DatabaseSettingsDto ParseDatabase(string? connectionString)
    {
        var dto = new DatabaseSettingsDto { Host = "", Port = 5432, Database = "atentis", Username = "", SslMode = "Prefer" };
        if (string.IsNullOrWhiteSpace(connectionString)) return dto;
        try
        {
            var b = new NpgsqlConnectionStringBuilder(connectionString);
            dto.Host = b.Host ?? "";
            dto.Port = b.Port;
            dto.Database = b.Database ?? "";
            dto.Username = b.Username ?? "";
            dto.HasPassword = !string.IsNullOrEmpty(b.Password);
            dto.SslMode = b.SslMode.ToString();
        }
        catch (ArgumentException) { /* испорченная строка: покажем пустые поля */ }
        return dto;
    }

    /// <summary>Собирает строку подключения. Пустой пароль в запросе = оставить прежний.</summary>
    public static string BuildConnectionString(DatabaseSettingsDto d, string? existingConnectionString)
    {
        var existingPassword = "";
        if (!string.IsNullOrWhiteSpace(existingConnectionString))
        {
            try { existingPassword = new NpgsqlConnectionStringBuilder(existingConnectionString).Password ?? ""; }
            catch (ArgumentException) { }
        }
        var b = new NpgsqlConnectionStringBuilder
        {
            Host = d.Host.Trim(),
            Port = d.Port,
            Database = d.Database.Trim(),
            Username = d.Username.Trim(),
            Password = string.IsNullOrEmpty(d.Password) ? existingPassword : d.Password,
            SslMode = Enum.Parse<SslMode>(d.SslMode, ignoreCase: true)
        };
        return b.ConnectionString;
    }

    public void Save(SettingsDto settings)
    {
        lock (_gate)
        {
            JsonObject root;
            if (File.Exists(FilePath))
            {
                try { root = JsonNode.Parse(File.ReadAllText(FilePath), documentOptions: ReadOptions) as JsonObject ?? new JsonObject(); }
                catch (JsonException) { root = new JsonObject(); }   // испорченный вручную файл: перезаписываем (копия .bak сохраняется ниже)
            }
            else root = new JsonObject();

            var conns = root["ConnectionStrings"] as JsonObject ?? new JsonObject();
            conns["Postgres"] = BuildConnectionString(settings.Database, CurrentConnectionString);
            root["ConnectionStrings"] = conns;

            var collector = root["Collector"] as JsonObject ?? new JsonObject();
            var oldSlots = (collector["Slots"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();
            var newSlots = new JsonArray();
            foreach (var s in settings.Slots)
            {
                var node = oldSlots.FirstOrDefault(o => string.Equals((string?)o["Name"], s.Name, StringComparison.OrdinalIgnoreCase))
                               ?.DeepClone() as JsonObject ?? new JsonObject();
                node["Name"] = s.Name;
                node["Market"] = s.Market.ToUpperInvariant();
                node["Enabled"] = s.Enabled;
                node["Server"] = s.Server.Trim();
                node["Port"] = s.Port;
                node["Login"] = s.Login.Trim();
                node["PublicKeyFile"] = s.PublicKeyFile.Trim();
                node.Remove("Password");   // на случай старого файла: пароли в файле не храним
                newSlots.Add(node);
            }
            collector["Slots"] = newSlots;
            root["Collector"] = collector;

            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, root.ToJsonString(WriteOptions), new System.Text.UTF8Encoding(false));
            if (File.Exists(FilePath)) File.Replace(temp, FilePath, FilePath + ".bak");
            else File.Move(temp, FilePath);
        }
        _config.Reload();   // применить без перезапуска службы
    }
}
