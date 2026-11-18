using Microsoft.Extensions.Options;

namespace AtentisServer.Configuration;

/// <summary>
/// Проверка конфигурации. Пустые/незаполненные слоты - НЕ ошибка (это нормальное состояние «ещё не настроено», его показывает панель);
/// ошибкой считаются заведомо неверные значения.
/// </summary>
public sealed class CollectorOptionsValidator : IValidateOptions<CollectorOptions>
{
    private static readonly HashSet<string> Markets = new(StringComparer.OrdinalIgnoreCase) { "SPOT", "FUT" };

    public ValidateOptionsResult Validate(string? name, CollectorOptions o)
    {
        var errors = new List<string>();

        if (o.Slots.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            errors.Add("Collector:Slots: имена слотов должны быть уникальны.");

        for (var i = 0; i < o.Slots.Count; i++)
        {
            var s = o.Slots[i];
            var p = $"Collector:Slots:{i}";
            if (string.IsNullOrWhiteSpace(s.Name)) errors.Add($"{p}:Name не задан.");
            if (!Markets.Contains(s.Market ?? "")) errors.Add($"{p}:Market должен быть SPOT или FUT.");
            if (!string.IsNullOrWhiteSpace(s.Server) && s.Port is < 1 or > 65535 && s.Port != 0)
                errors.Add($"{p}:Port вне диапазона 1..65535.");
            if (s.RefreshPeriodMs is < 100 or > 60000) errors.Add($"{p}:RefreshPeriodMs вне диапазона 100..60000.");
        }

        if (o.Tables.Any(t => t.Enabled && string.IsNullOrWhiteSpace(t.Name))) errors.Add("Collector:Tables: у таблицы не задано имя.");
        if (o.BatchSize is < 1 or > 50000) errors.Add("Collector:BatchSize вне диапазона 1..50000.");
        if (o.ReconnectAfterSeconds < 10) errors.Add("Collector:ReconnectAfterSeconds не может быть меньше 10.");

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
