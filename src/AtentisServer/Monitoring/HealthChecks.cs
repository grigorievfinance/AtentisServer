using AtentisServer.Collection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AtentisServer.Monitoring;

/// <summary>PostgreSQL: недоступен -> Unhealthy (данные не сохраняются).</summary>
public sealed class DatabaseHealthCheck : IHealthCheck
{
    private readonly CollectorState _state;
    public DatabaseHealthCheck(CollectorState state) => _state = state;

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default) =>
        Task.FromResult(!_state.DatabaseConfigured
            ? HealthCheckResult.Degraded("База данных ещё не настроена (откройте «Панель управления»)")
            : !_state.DatabaseReady
            ? HealthCheckResult.Unhealthy("Схема PostgreSQL ещё не инициализирована: " + _state.DatabaseMessage)
            : _state.DatabaseUp
                ? HealthCheckResult.Healthy("PostgreSQL доступен")
                : HealthCheckResult.Unhealthy("Последняя запись в PostgreSQL завершилась ошибкой"));
}

/// <summary>Слоты Atentis: нет соединения -> Degraded (процесс жив и переподключается, но данные сейчас не поступают).</summary>
public sealed class SlotsHealthCheck : IHealthCheck
{
    private readonly CollectorState _state;
    public SlotsHealthCheck(CollectorState state) => _state = state;

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        var slots = _state.Slots.Snapshot();
        var data = slots.ToDictionary(s => s.Name, s => (object)(s.IsConnected ? "connected" : "disconnected"));
        var down = slots.Where(s => !s.IsConnected).Select(s => s.Name).ToList();
        return Task.FromResult(down.Count == 0
            ? HealthCheckResult.Healthy("Все слоты подключены", data)
            : HealthCheckResult.Degraded("Нет соединения: " + string.Join(", ", down), data: data));
    }
}
