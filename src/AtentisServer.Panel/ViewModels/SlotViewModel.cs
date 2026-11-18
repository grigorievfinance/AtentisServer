using AtentisServer.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AtentisServer.Panel.ViewModels;

/// <summary>Один рынок Atentis (фондовый или срочный): поля ввода и состояние подключения.</summary>
public sealed partial class SlotViewModel : ObservableObject
{
    public SlotViewModel(string name, string market)
    {
        Name = name;
        Market = market;
    }

    public string Name { get; }
    public string Market { get; }

    public string Title => Market.Equals("FUT", StringComparison.OrdinalIgnoreCase) ? "Срочный рынок" : "Фондовый рынок";
    public string Subtitle => Market.Equals("FUT", StringComparison.OrdinalIgnoreCase)
        ? "FORTS · фьючерсы и опционы"
        : "MICEX · акции и облигации";

    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private string _server = "";
    [ObservableProperty] private string _portText = "";
    [ObservableProperty] private string _login = "";

    /// <summary>Вводится вручную, нигде не сохраняется; после отправки в службу поле очищается.</summary>
    [ObservableProperty] private string _password = "";

    /// <summary>Служба уже знает пароль этого рынка (он в её памяти).</summary>
    [ObservableProperty] private bool _hasPassword;

    /// <summary>Файл открытого ключа (расширенная настройка, в окне не показывается, но сохраняется).</summary>
    public string PublicKeyFile { get; set; } = "";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ToneIsOk), nameof(ToneIsWarn), nameof(ToneIsError), nameof(ToneIsMuted))]
    private Tone _tone = Tone.Muted;

    [ObservableProperty] private string _stateText = "Нет данных";
    [ObservableProperty] private string? _stateHint;

    public bool ToneIsOk => Tone == Tone.Ok;
    public bool ToneIsWarn => Tone == Tone.Warn;
    public bool ToneIsError => Tone == Tone.Error;
    public bool ToneIsMuted => Tone == Tone.Muted;

    public string PasswordPlaceholder => HasPassword ? "Пароль принят службой (оставьте пустым)" : "Введите пароль Atentis";

    partial void OnHasPasswordChanged(bool value) => OnPropertyChanged(nameof(PasswordPlaceholder));

    public void Apply(SlotStatusDto s)
    {
        HasPassword = s.HasPassword;
        (StateText, Tone) = s.State switch
        {
            SlotStates.Connected => ("Подключено", Tone.Ok),
            SlotStates.Connecting => ("Подключение…", Tone.Warn),
            SlotStates.Reconnecting => ("Связь потеряна, переподключаюсь…", Tone.Warn),
            SlotStates.WaitingForPassword => ("Ждёт пароль", Tone.Error),
            SlotStates.AuthFailed => ("Неверный логин или пароль", Tone.Error),
            SlotStates.NotConfigured => ("Не настроено", Tone.Muted),
            SlotStates.Disabled => ("Выключено", Tone.Muted),
            _ => (s.State, Tone.Muted)
        };
        StateHint = s.State == SlotStates.Connected ? null : s.Message;
    }

    public SlotSettingsDto ToDto() => new()
    {
        Name = Name,
        Market = Market,
        Enabled = Enabled,
        Server = Server.Trim(),
        Port = int.TryParse(PortText.Trim(), out var p) ? p : 0,
        Login = Login.Trim(),
        PublicKeyFile = PublicKeyFile
    };

    public void Load(SlotSettingsDto d)
    {
        Enabled = d.Enabled;
        Server = d.Server;
        PortText = d.Port > 0 ? d.Port.ToString() : "";
        Login = d.Login;
        PublicKeyFile = d.PublicKeyFile;
    }
}
