using AtentisServer.Collection;
using AtentisServer.Configuration;
using AtentisServer.Contracts;
using AtentisServer.Security;
using Microsoft.Extensions.Options;

namespace AtentisServer.Control;

public interface IStatusService
{
    StatusDto Build();
}

public sealed class StatusService : IStatusService
{
    private readonly CollectorState _state;
    private readonly IOptionsMonitor<CollectorOptions> _options;
    private readonly CredentialStore _credentials;
    private readonly ILocalSettingsStore _settings;

    public StatusService(CollectorState state, IOptionsMonitor<CollectorOptions> options, CredentialStore credentials, ILocalSettingsStore settings)
    {
        _state = state;
        _options = options;
        _credentials = credentials;
        _settings = settings;
    }

    public StatusDto Build()
    {
        var dto = new StatusDto
        {
            Version = AppInfo.Version,
            ServerTimeUtc = DateTime.UtcNow,
            DatabaseConfigured = _state.DatabaseConfigured,
            DatabaseReady = _state.DatabaseReady,
            DatabaseUp = _state.DatabaseUp,
            DatabaseMessage = _state.DatabaseMessage,
            ConfigError = _state.ConfigError,
            SettingsFile = _settings.FilePath
        };
        foreach (var cfg in _options.CurrentValue.Slots)
        {
            var rt = _state.Slots.Find(cfg.Name);
            var has = _credentials.Has(cfg.Name);
            var (state, message) = SlotStateResolver.Resolve(cfg, rt, has);
            dto.Slots.Add(new SlotStatusDto
            {
                Name = cfg.Name, Market = cfg.Market, Enabled = cfg.Enabled,
                State = state, Message = message,
                Connected = rt?.IsConnected == true, HasPassword = has,
                LastTableUpdateUtc = rt?.LastTableUpdateUtc,
                Reconnects = rt is null ? 0 : Interlocked.Read(ref rt.Reconnects)
            });
        }
        return dto;
    }
}
