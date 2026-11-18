using AtentisServer.Collection;
using AtentisServer.Contracts;
using AtentisServer.Security;

namespace AtentisServer.Control;

/// <summary>Выполняет команды «Панели управления». Не зависит от транспорта (канала), поэтому тестируется напрямую.</summary>
public sealed class ControlCommandHandler
{
    private const int MaxPasswordLength = 512;

    private readonly ILocalSettingsStore _settings;
    private readonly CredentialStore _credentials;
    private readonly IStatusService _status;
    private readonly IDatabaseTester _dbTester;
    private readonly CollectorWorker? _worker;
    private readonly ILogger<ControlCommandHandler> _log;

    public ControlCommandHandler(ILocalSettingsStore settings, CredentialStore credentials, IStatusService status,
        IDatabaseTester dbTester, ILogger<ControlCommandHandler> log, CollectorWorker? worker = null)
    {
        _settings = settings;
        _credentials = credentials;
        _status = status;
        _dbTester = dbTester;
        _log = log;
        _worker = worker;
    }

    public async Task<ControlResponse> HandleAsync(ControlRequest req, CancellationToken ct)
    {
        try
        {
            switch (req.Cmd)
            {
                case Commands.Status:
                    return new ControlResponse { Ok = true, Status = _status.Build() };

                case Commands.GetSettings:
                    return new ControlResponse { Ok = true, Settings = _settings.Read(), Status = _status.Build() };

                case Commands.Apply:
                    return Apply(req);

                case Commands.Disconnect:
                    if (string.IsNullOrWhiteSpace(req.Slot)) return ControlResponse.Fail(ErrorCodes.Invalid, "Не указан рынок.");
                    _credentials.Clear(req.Slot);
                    _log.LogInformation("Пользователь отключил слот {Slot} и стёр пароль из памяти", req.Slot);
                    _worker?.Wake();
                    return new ControlResponse { Ok = true, Message = "Отключено. Пароль удалён из памяти службы.", Status = _status.Build() };

                case Commands.TestDatabase:
                    if (req.Settings?.Database is null) return ControlResponse.Fail(ErrorCodes.Invalid, "Не переданы параметры базы данных.");
                    var (ok, message) = await _dbTester.TestAsync(req.Settings.Database, _settings.CurrentConnectionString, ct);
                    return new ControlResponse { Ok = ok, Message = ok ? message : null, Error = ok ? null : message, ErrorCode = ok ? null : ErrorCodes.Invalid };

                default:
                    return ControlResponse.Fail(ErrorCodes.Protocol, "Неизвестная команда. Возможно, версии панели и службы не совпадают.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // В лог идёт только тип команды и текст ошибки; содержимое запроса (там пароли) не логируется никогда.
            _log.LogError(ex, "Ошибка выполнения команды {Cmd}", req.Cmd);
            return ControlResponse.Fail(ErrorCodes.Internal, "Внутренняя ошибка службы: " + ex.Message);
        }
    }

    private ControlResponse Apply(ControlRequest req)
    {
        var before = _settings.Read();
        var effective = req.Settings ?? before;

        if (req.Settings is not null)
        {
            var errors = SettingsValidator.Validate(req.Settings);
            if (errors.Count > 0) return ControlResponse.Fail(ErrorCodes.Invalid, "Проверьте введённые данные.", errors);
        }

        var passwords = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (slot, password) in req.Passwords ?? new Dictionary<string, string>())
        {
            if (string.IsNullOrEmpty(password)) continue;
            if (password.Length > MaxPasswordLength) return ControlResponse.Fail(ErrorCodes.Invalid, "Слишком длинный пароль.");
            if (!effective.Slots.Any(s => string.Equals(s.Name, slot, StringComparison.OrdinalIgnoreCase)))
                return ControlResponse.Fail(ErrorCodes.Invalid, $"Неизвестный рынок «{slot}».");
            passwords[slot] = password;
        }

        if (req.Settings is not null)
        {
            _settings.Save(req.Settings);
            // Если у рынка поменялся сервер/порт/логин/включённость, а новый пароль не введён - прежний пароль относился к другим данным.
            foreach (var slot in req.Settings.Slots)
            {
                var old = before.Slots.FirstOrDefault(s => string.Equals(s.Name, slot.Name, StringComparison.OrdinalIgnoreCase));
                var changed = old is null || !string.Equals(old.Server.Trim(), slot.Server.Trim(), StringComparison.OrdinalIgnoreCase)
                              || old.Port != slot.Port || !string.Equals(old.Login.Trim(), slot.Login.Trim(), StringComparison.Ordinal)
                              || !string.Equals(old.PublicKeyFile.Trim(), slot.PublicKeyFile.Trim(), StringComparison.Ordinal);
                if ((changed || !slot.Enabled) && !passwords.ContainsKey(slot.Name)) _credentials.Clear(slot.Name);
            }
            _log.LogInformation("Настройки сохранены пользователем: {File}", _settings.FilePath);
        }

        foreach (var (slot, password) in passwords)
        {
            _credentials.Set(slot, password);
            _log.LogInformation("Для слота {Slot} введён пароль Atentis (хранится только в памяти)", slot);
        }

        _worker?.Wake();
        return new ControlResponse { Ok = true, Message = "Готово. Подключение выполняется.", Status = _status.Build(), Settings = _settings.Read() };
    }
}
