namespace AtentisServer.Contracts;

/// <summary>Проверка введённых пользователем настроек. Сообщения - простым русским языком (их видит не программист).</summary>
public static class SettingsValidator
{
    private static readonly HashSet<string> SslModes = new(StringComparer.OrdinalIgnoreCase)
        { "Disable", "Allow", "Prefer", "Require", "VerifyCA", "VerifyFull" };

    public static List<string> Validate(SettingsDto s)
    {
        var errors = new List<string>();
        errors.AddRange(ValidateDatabase(s.Database));

        if (s.Slots.Count == 0) errors.Add("Не задано ни одного рынка.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var slot in s.Slots)
        {
            var title = Title(slot);
            if (string.IsNullOrWhiteSpace(slot.Name)) { errors.Add("У рынка не задано имя."); continue; }
            if (!names.Add(slot.Name)) errors.Add($"{title}: имя «{slot.Name}» встречается дважды.");
            if (!string.Equals(slot.Market, "SPOT", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(slot.Market, "FUT", StringComparison.OrdinalIgnoreCase))
                errors.Add($"{title}: неизвестный тип рынка «{slot.Market}».");
            if (!slot.Enabled) continue;
            if (string.IsNullOrWhiteSpace(slot.Server)) errors.Add($"{title}: укажите адрес сервера Atentis.");
            if (slot.Port is < 1 or > 65535) errors.Add($"{title}: порт должен быть числом от 1 до 65535.");
            if (string.IsNullOrWhiteSpace(slot.Login)) errors.Add($"{title}: укажите логин.");
        }
        return errors;
    }

    public static List<string> ValidateDatabase(DatabaseSettingsDto d)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(d.Host)) errors.Add("База данных: укажите адрес сервера PostgreSQL.");
        if (d.Port is < 1 or > 65535) errors.Add("База данных: порт должен быть числом от 1 до 65535 (обычно 5432).");
        if (string.IsNullOrWhiteSpace(d.Database)) errors.Add("База данных: укажите имя базы.");
        if (string.IsNullOrWhiteSpace(d.Username)) errors.Add("База данных: укажите пользователя.");
        if (!SslModes.Contains(d.SslMode ?? "")) errors.Add("База данных: неизвестный режим шифрования.");
        return errors;
    }

    /// <summary>Проверка, что для включённых рынков есть пароль (введённый сейчас или уже принятый службой).</summary>
    public static List<string> ValidatePasswords(SettingsDto s, IReadOnlyDictionary<string, string> entered, Func<string, bool> serviceHasPassword)
    {
        var errors = new List<string>();
        foreach (var slot in s.Slots.Where(x => x.Enabled))
        {
            var has = entered.TryGetValue(slot.Name, out var p) && !string.IsNullOrEmpty(p);
            if (!has && !serviceHasPassword(slot.Name)) errors.Add($"{Title(slot)}: введите пароль.");
        }
        return errors;
    }

    public static string Title(SlotSettingsDto slot) => slot.Market.ToUpperInvariant() switch
    {
        "SPOT" => "Фондовый рынок (MICEX)",
        "FUT" => "Срочный рынок (FORTS)",
        _ => string.IsNullOrWhiteSpace(slot.Name) ? "Рынок" : slot.Name
    };
}
