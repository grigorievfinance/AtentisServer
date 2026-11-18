using System.Text.Json;
using System.Text.Json.Serialization;

namespace AtentisServer.Contracts;

/// <summary>
/// Протокол управления службой AtentisServer из «Панели управления».
/// Транспорт: именованный канал Windows (локально, доступ по ACL), один запрос = одна JSON-строка, один ответ = одна JSON-строка.
/// Пароли Atentis передаются ТОЛЬКО по этому каналу и хранятся ТОЛЬКО в памяти службы.
/// </summary>
public static class ControlPipe
{
    public const string Name = "AtentisServer.Control";

    /// <summary>Локальная группа Windows, члены которой могут управлять службой (создаётся установщиком).</summary>
    public const string OperatorsGroup = "AtentisServerOperators";

    public const int MaxMessageBytes = 64 * 1024;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };
}

public static class Commands
{
    public const string Status = "status";
    public const string GetSettings = "get_settings";
    public const string Apply = "apply";
    public const string Disconnect = "disconnect";
    public const string TestDatabase = "test_db";
}

/// <summary>Состояния слота, показываемые пользователю.</summary>
public static class SlotStates
{
    public const string Disabled = "Disabled";                 // выключен пользователем
    public const string NotConfigured = "NotConfigured";       // не заполнены сервер/порт/логин
    public const string WaitingForPassword = "WaitingForPassword";
    public const string Connecting = "Connecting";
    public const string Connected = "Connected";
    public const string Reconnecting = "Reconnecting";         // была связь, пропала, идёт переподключение
    public const string AuthFailed = "AuthFailed";             // Atentis отклонил логин/пароль
}

public sealed class DatabaseSettingsDto
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5432;
    public string Database { get; set; } = "atentis";
    public string Username { get; set; } = "";

    /// <summary>Пароль БД. Служба НИКОГДА не отправляет его обратно: пусто при ответе, см. HasPassword. Пусто в запросе = оставить прежний.</summary>
    public string? Password { get; set; }
    public bool HasPassword { get; set; }

    /// <summary>Disable, Prefer, Require, VerifyCA, VerifyFull.</summary>
    public string SslMode { get; set; } = "Prefer";
}

public sealed class SlotSettingsDto
{
    public string Name { get; set; } = "";
    public string Market { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string Server { get; set; } = "";
    public int Port { get; set; }
    public string Login { get; set; } = "";
    public string PublicKeyFile { get; set; } = "";
}

public sealed class SettingsDto
{
    public DatabaseSettingsDto Database { get; set; } = new();
    public List<SlotSettingsDto> Slots { get; set; } = new();
}

public sealed class SlotStatusDto
{
    public string Name { get; set; } = "";
    public string Market { get; set; } = "";
    public bool Enabled { get; set; }
    public string State { get; set; } = SlotStates.NotConfigured;
    public string? Message { get; set; }
    public bool Connected { get; set; }
    public bool HasPassword { get; set; }
    public DateTime? LastTableUpdateUtc { get; set; }
    public long Reconnects { get; set; }
}

public sealed class StatusDto
{
    public string Version { get; set; } = "";
    public DateTime ServerTimeUtc { get; set; }
    public bool DatabaseConfigured { get; set; }
    public bool DatabaseReady { get; set; }
    public bool DatabaseUp { get; set; }
    public string? DatabaseMessage { get; set; }
    public string? ConfigError { get; set; }
    public string? SettingsFile { get; set; }
    public List<SlotStatusDto> Slots { get; set; } = new();
}

public sealed class ControlRequest
{
    public string Cmd { get; set; } = "";
    public SettingsDto? Settings { get; set; }

    /// <summary>Имя слота -> пароль Atentis (только в памяти службы).</summary>
    public Dictionary<string, string>? Passwords { get; set; }
    public string? Slot { get; set; }
}

public static class ErrorCodes
{
    public const string Unreachable = "unreachable";      // служба не запущена / канал недоступен
    public const string AccessDenied = "access_denied";   // нет прав на управление
    public const string Timeout = "timeout";
    public const string Protocol = "protocol";
    public const string Invalid = "invalid";              // данные не прошли проверку
    public const string Internal = "internal";
}

public sealed class ControlResponse
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public string? ErrorCode { get; set; }
    public List<string>? Errors { get; set; }
    public string? Message { get; set; }
    public StatusDto? Status { get; set; }
    public SettingsDto? Settings { get; set; }

    public static ControlResponse Fail(string code, string error, List<string>? errors = null) =>
        new() { Ok = false, ErrorCode = code, Error = error, Errors = errors };
}
