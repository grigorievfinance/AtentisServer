using Microsoft.Extensions.Options;

namespace AtentisServer.Configuration;

/// <summary>Проверка конфигурации при старте: служба не запустится с понятной ошибкой, а не упадёт позже. Значения паролей в сообщения не попадают.</summary>
public sealed class CollectorOptionsValidator : IValidateOptions<CollectorOptions>
{
    private static readonly HashSet<string> Markets = new(StringComparer.OrdinalIgnoreCase) { "SPOT", "FUT" };

    public ValidateOptionsResult Validate(string? name, CollectorOptions o)
    {
        var errors = new List<string>();

        var enabled = o.Slots.Where(s => s.Enabled).ToList();
        if (enabled.Count == 0) errors.Add("Collector:Slots: не задан ни один включённый слот.");

        if (o.Slots.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            errors.Add("Collector:Slots: имена слотов должны быть уникальны.");

        for (var i = 0; i < o.Slots.Count; i++)
        {
            var s = o.Slots[i];
            if (!s.Enabled) continue;
            var p = $"Collector:Slots:{i}";
            if (string.IsNullOrWhiteSpace(s.Name)) errors.Add($"{p}:Name не задан.");
            if (!Markets.Contains(s.Market ?? "")) errors.Add($"{p}:Market должен быть SPOT или FUT.");
            if (string.IsNullOrWhiteSpace(s.Server)) errors.Add($"{p}:Server не задан.");
            if (s.Port is < 1 or > 65535) errors.Add($"{p}:Port вне диапазона 1..65535.");
            if (string.IsNullOrWhiteSpace(s.Login)) errors.Add($"{p}:Login не задан.");
            if (string.IsNullOrEmpty(s.Password))
                errors.Add($"{p}:Password не задан (задайте переменной окружения Collector__Slots__{i}__Password, не в appsettings.json).");
            if (s.RefreshPeriodMs is < 100 or > 60000) errors.Add($"{p}:RefreshPeriodMs вне диапазона 100..60000.");
        }

        var tables = o.Tables.Where(t => t.Enabled).ToList();
        if (tables.Count == 0) errors.Add("Collector:Tables: не задана ни одна включённая таблица.");
        if (tables.Any(t => string.IsNullOrWhiteSpace(t.Name))) errors.Add("Collector:Tables: у таблицы не задано имя.");
        if (o.BatchSize is < 1 or > 50000) errors.Add("Collector:BatchSize вне диапазона 1..50000.");
        if (o.ReconnectAfterSeconds < 10) errors.Add("Collector:ReconnectAfterSeconds не может быть меньше 10.");

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
