using AtentisServer.Contracts;

namespace AtentisServer.Panel.Tests;

/// <summary>Имитация службы: хранит настройки и «пароли в памяти», как настоящая.</summary>
internal sealed class FakeControlClient : IControlClient
{
    public bool Reachable = true;
    public List<ControlRequest> Requests { get; } = new();
    public SettingsDto Settings { get; set; } = new()
    {
        Database = new DatabaseSettingsDto { Host = "", Port = 5432, Database = "atentis", Username = "" },
        Slots =
        {
            new SlotSettingsDto { Name = "MICEX", Market = "SPOT", Enabled = true },
            new SlotSettingsDto { Name = "FORTS", Market = "FUT", Enabled = true }
        }
    };
    public Dictionary<string, string> Passwords { get; } = new();
    public Dictionary<string, string> SlotStateOverride { get; } = new();
    public bool DatabaseReady;
    public string? DatabaseMessage;
    public (bool Ok, string Message) DbTestResult = (true, "Подключение успешно. Таблицы будут созданы автоматически.");

    public Task<ControlResponse> SendAsync(ControlRequest request, CancellationToken ct = default)
    {
        Requests.Add(request);
        if (!Reachable)
            return Task.FromResult(ControlResponse.Fail(ErrorCodes.Unreachable, "Служба AtentisServer не отвечает. Возможно, она не запущена. Обратитесь к администратору сервера."));

        switch (request.Cmd)
        {
            case Commands.Status:
                return Task.FromResult(new ControlResponse { Ok = true, Status = Status() });
            case Commands.GetSettings:
                return Task.FromResult(new ControlResponse { Ok = true, Settings = Settings, Status = Status() });
            case Commands.Apply:
                if (request.Settings is not null)
                {
                    var errors = SettingsValidator.Validate(request.Settings);
                    if (errors.Count > 0) return Task.FromResult(ControlResponse.Fail(ErrorCodes.Invalid, "Проверьте введённые данные.", errors));
                    var hadPassword = Settings.Database.HasPassword;
                    Settings = request.Settings;
                    // как настоящая служба: пароль БД наружу не отдаётся, остаётся только признак «пароль сохранён»
                    Settings.Database.HasPassword = hadPassword || !string.IsNullOrEmpty(Settings.Database.Password);
                    Settings.Database.Password = null;
                    DatabaseReady = true;
                }
                foreach (var (slot, pw) in request.Passwords ?? new()) Passwords[slot] = pw;
                return Task.FromResult(new ControlResponse { Ok = true, Settings = Settings, Status = Status() });
            case Commands.Disconnect:
                Passwords.Remove(request.Slot!);
                return Task.FromResult(new ControlResponse { Ok = true, Status = Status() });
            case Commands.TestDatabase:
                return Task.FromResult(DbTestResult.Ok
                    ? new ControlResponse { Ok = true, Message = DbTestResult.Message }
                    : ControlResponse.Fail(ErrorCodes.Invalid, DbTestResult.Message));
        }
        return Task.FromResult(ControlResponse.Fail(ErrorCodes.Protocol, "?"));
    }

    private StatusDto Status()
    {
        var dto = new StatusDto
        {
            Version = "4.2.0",
            ServerTimeUtc = DateTime.UtcNow,
            DatabaseConfigured = !string.IsNullOrEmpty(Settings.Database.Host),
            DatabaseReady = DatabaseReady,
            DatabaseUp = DatabaseReady,
            DatabaseMessage = DatabaseMessage
        };
        foreach (var s in Settings.Slots)
        {
            var has = Passwords.ContainsKey(s.Name);
            var configured = !string.IsNullOrEmpty(s.Server) && s.Port > 0 && !string.IsNullOrEmpty(s.Login);
            var state = SlotStateOverride.TryGetValue(s.Name, out var o) ? o
                : !s.Enabled ? SlotStates.Disabled
                : !configured ? SlotStates.NotConfigured
                : !has ? SlotStates.WaitingForPassword
                : SlotStates.Connected;
            dto.Slots.Add(new SlotStatusDto
            {
                Name = s.Name, Market = s.Market, Enabled = s.Enabled, State = state, HasPassword = has,
                Connected = state == SlotStates.Connected,
                Message = state switch
                {
                    SlotStates.NotConfigured => "Заполните адрес сервера, порт и логин",
                    SlotStates.WaitingForPassword => "Ожидает пароль. Введите его в панели управления.",
                    SlotStates.AuthFailed => "Atentis отклонил логин или пароль. Проверьте данные и введите пароль заново.",
                    SlotStates.Reconnecting => "Связь с Atentis потеряна, переподключаюсь автоматически",
                    _ => null
                }
            });
        }
        return dto;
    }
}
