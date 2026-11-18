using System.Collections.ObjectModel;
using AtentisServer.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AtentisServer.Panel.ViewModels;

/// <summary>Главное окно «Панель управления AtentisServer». Вся логика здесь (окно только отображает), поэтому она проверяется тестами.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly IControlClient _client;
    private SettingsDto? _loaded;
    private StatusDto? _lastStatus;

    public MainViewModel(IControlClient client)
    {
        _client = client;
        SslModes = new ObservableCollection<SslOption>
        {
            new("Prefer", "Если возможно"),
            new("Require", "Обязательно"),
            new("VerifyFull", "Обязательно + проверка сертификата"),
            new("Disable", "Не использовать")
        };
        _sslMode = SslModes[0];
    }

    public ObservableCollection<SlotViewModel> Slots { get; } = new();
    public ObservableCollection<SslOption> SslModes { get; }
    public ObservableCollection<string> BannerDetails { get; } = new();

    // ---- служба
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanOperate))] private bool _serviceReachable;
    [ObservableProperty] private string _serviceText = "Проверяю службу…";
    [ObservableProperty] private Tone _serviceTone = Tone.Muted;
    [ObservableProperty] private string _versionText = "";
    [ObservableProperty] private string? _serviceProblem;

    // ---- база данных
    [ObservableProperty] private string _dbHost = "";
    [ObservableProperty] private string _dbPortText = "5432";
    [ObservableProperty] private string _dbName = "atentis";
    [ObservableProperty] private string _dbUser = "";
    [ObservableProperty] private string _dbPassword = "";
    [ObservableProperty] private bool _dbHasPassword;
    [ObservableProperty] private SslOption _sslMode;
    [ObservableProperty] private string _dbStateText = "";
    [ObservableProperty] private Tone _dbTone = Tone.Muted;

    // ---- сообщение о результате действия
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasBanner))] private string? _bannerText;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(BannerIsError), nameof(BannerIsSuccess), nameof(BannerIsWarning), nameof(BannerIsInfo))]
    private BannerKind _bannerKind;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanOperate))] private bool _isBusy;

    public bool HasBanner => !string.IsNullOrEmpty(BannerText);
    public bool BannerIsError => BannerKind == BannerKind.Error;
    public bool BannerIsSuccess => BannerKind == BannerKind.Success;
    public bool BannerIsWarning => BannerKind == BannerKind.Warning;
    public bool BannerIsInfo => BannerKind == BannerKind.Info;
    public bool CanOperate => ServiceReachable && !IsBusy;

    public string DbPasswordPlaceholder => DbHasPassword ? "Сохранён (оставьте пустым)" : "Пароль пользователя базы";
    partial void OnDbHasPasswordChanged(bool value) => OnPropertyChanged(nameof(DbPasswordPlaceholder));

    // ------------------------------------------------------------------ обновление статуса

    /// <summary>Опрос службы (вызывается панелью раз в 2 секунды). Поля ввода не затирает.</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        var r = await _client.SendAsync(new ControlRequest { Cmd = Commands.Status }, ct);
        if (!r.Ok || r.Status is null)
        {
            ServiceReachable = false;
            ServiceTone = Tone.Error;
            ServiceText = "Служба недоступна";
            ServiceProblem = r.Error ?? "Нет связи со службой AtentisServer.";
            return;
        }

        ServiceProblem = null;
        var wasReachable = ServiceReachable;
        ServiceReachable = true;
        ApplyStatus(r.Status);
        if (!wasReachable || _loaded is null) await LoadSettingsAsync(ct);
    }

    private void ApplyStatus(StatusDto s)
    {
        _lastStatus = s;
        ServiceTone = Tone.Ok;
        ServiceText = "Служба работает";
        VersionText = "версия " + s.Version;

        foreach (var ss in s.Slots)
        {
            var vm = Slots.FirstOrDefault(x => string.Equals(x.Name, ss.Name, StringComparison.OrdinalIgnoreCase));
            if (vm is null) { vm = new SlotViewModel(ss.Name, ss.Market); Slots.Add(vm); }
            vm.Apply(ss);
        }

        if (!s.DatabaseConfigured) { DbStateText = "База данных не настроена"; DbTone = Tone.Warn; }
        else if (s.DatabaseReady && s.DatabaseUp) { DbStateText = "База данных подключена"; DbTone = Tone.Ok; }
        else { DbStateText = s.DatabaseMessage ?? "Нет связи с базой данных"; DbTone = Tone.Error; }

        if (!string.IsNullOrEmpty(s.ConfigError)) SetBanner(BannerKind.Error, "Ошибка в файле настроек: " + s.ConfigError);
    }

    private async Task LoadSettingsAsync(CancellationToken ct)
    {
        var r = await _client.SendAsync(new ControlRequest { Cmd = Commands.GetSettings }, ct);
        if (!r.Ok || r.Settings is null) return;
        _loaded = r.Settings;

        DbHost = r.Settings.Database.Host;
        DbPortText = r.Settings.Database.Port.ToString();
        DbName = r.Settings.Database.Database;
        DbUser = r.Settings.Database.Username;
        DbHasPassword = r.Settings.Database.HasPassword;
        var ssl = SslModes.FirstOrDefault(o => o.Value.Equals(r.Settings.Database.SslMode, StringComparison.OrdinalIgnoreCase));
        if (ssl is null) { ssl = new SslOption(r.Settings.Database.SslMode, r.Settings.Database.SslMode); SslModes.Add(ssl); }
        SslMode = ssl;

        foreach (var s in r.Settings.Slots)
        {
            var vm = Slots.FirstOrDefault(x => string.Equals(x.Name, s.Name, StringComparison.OrdinalIgnoreCase));
            if (vm is null) { vm = new SlotViewModel(s.Name, s.Market); Slots.Add(vm); }
            vm.Load(s);
        }
        if (r.Status is not null) ApplyStatus(r.Status);
    }

    // ------------------------------------------------------------------ действия

    private SettingsDto BuildSettings() => new()
    {
        Database = new DatabaseSettingsDto
        {
            Host = DbHost.Trim(),
            Port = int.TryParse(DbPortText.Trim(), out var p) ? p : 0,
            Database = DbName.Trim(),
            Username = DbUser.Trim(),
            Password = string.IsNullOrEmpty(DbPassword) ? null : DbPassword,
            SslMode = SslMode.Value
        },
        Slots = Slots.Select(s => s.ToDto()).ToList()
    };

    /// <summary>Пароль слота считается «уже принятым службой», только если параметры слота не менялись (иначе служба его забудет).</summary>
    private bool ServiceKeepsPassword(SlotViewModel slot)
    {
        if (!slot.HasPassword) return false;
        var was = _loaded?.Slots.FirstOrDefault(x => string.Equals(x.Name, slot.Name, StringComparison.OrdinalIgnoreCase));
        if (was is null) return false;
        var now = slot.ToDto();
        return was.Enabled == now.Enabled && was.Port == now.Port && was.Login == now.Login &&
               string.Equals(was.Server.Trim(), now.Server, StringComparison.OrdinalIgnoreCase) && was.PublicKeyFile == now.PublicKeyFile;
    }

    [RelayCommand]
    private async Task SaveAndConnectAsync()
    {
        ClearBanner();
        var settings = BuildSettings();
        var entered = Slots.Where(s => !string.IsNullOrEmpty(s.Password)).ToDictionary(s => s.Name, s => s.Password);

        var errors = SettingsValidator.Validate(settings);
        errors.AddRange(SettingsValidator.ValidatePasswords(settings, entered, name =>
            Slots.FirstOrDefault(s => s.Name == name) is { } slot && ServiceKeepsPassword(slot)));
        if (errors.Count > 0)
        {
            SetBanner(BannerKind.Error, "Проверьте, пожалуйста, введённые данные:", errors);
            return;
        }

        IsBusy = true;
        try
        {
            var r = await _client.SendAsync(new ControlRequest { Cmd = Commands.Apply, Settings = settings, Passwords = entered });
            if (!r.Ok)
            {
                SetBanner(BannerKind.Error, r.Error ?? "Не удалось применить настройки.", r.Errors);
                return;
            }

            foreach (var s in Slots) s.Password = "";   // пароль в окне больше не нужен
            DbPassword = "";
            if (r.Settings is not null) _loaded = r.Settings;
            if (r.Status is not null) ApplyStatus(r.Status);
            DbHasPassword = r.Settings?.Database.HasPassword ?? DbHasPassword;
            SetBanner(BannerKind.Success, "Готово: настройки сохранены, подключение выполняется. Состояние каждого рынка показано выше.");
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task TestDatabaseAsync()
    {
        ClearBanner();
        var db = BuildSettings().Database;
        var errors = SettingsValidator.ValidateDatabase(db);
        if (errors.Count > 0) { SetBanner(BannerKind.Error, "Проверьте, пожалуйста, данные базы:", errors); return; }
        if (string.IsNullOrEmpty(db.Password) && !DbHasPassword)
        {
            SetBanner(BannerKind.Error, "Введите пароль пользователя базы данных.");
            return;
        }

        IsBusy = true;
        try
        {
            var r = await _client.SendAsync(new ControlRequest { Cmd = Commands.TestDatabase, Settings = new SettingsDto { Database = db } });
            if (r.Ok) SetBanner(BannerKind.Success, r.Message ?? "Подключение к базе данных успешно.");
            else SetBanner(BannerKind.Error, r.Error ?? "Не удалось подключиться к базе данных.");
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task DisconnectAsync(SlotViewModel? slot)
    {
        if (slot is null) return;
        ClearBanner();
        IsBusy = true;
        try
        {
            var r = await _client.SendAsync(new ControlRequest { Cmd = Commands.Disconnect, Slot = slot.Name });
            if (r.Ok)
            {
                if (r.Status is not null) ApplyStatus(r.Status);
                SetBanner(BannerKind.Info, $"{slot.Title}: отключено, пароль удалён из памяти службы.");
            }
            else SetBanner(BannerKind.Error, r.Error ?? "Не удалось отключить.");
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private Task RefreshNowAsync() => RefreshAsync();

    // ------------------------------------------------------------------ сообщения

    private void SetBanner(BannerKind kind, string text, IEnumerable<string>? details = null)
    {
        BannerDetails.Clear();
        if (details is not null) foreach (var d in details) BannerDetails.Add(d);
        BannerKind = kind;
        BannerText = text;
    }

    public void ClearBanner()
    {
        BannerDetails.Clear();
        BannerKind = BannerKind.None;
        BannerText = null;
    }

    /// <summary>Для тестов.</summary>
    internal StatusDto? LastStatus => _lastStatus;
}
