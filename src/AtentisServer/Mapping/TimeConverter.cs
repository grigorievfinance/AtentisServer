namespace AtentisServer.Mapping;

/// <summary>Переводит «наивное» время сервера Atentis в UTC.</summary>
public sealed class TimeConverter
{
    private readonly TimeZoneInfo _tz;

    public TimeConverter(string timeZoneId)
    {
        _tz = Resolve(timeZoneId);
    }

    public TimeConverter(TimeZoneInfo tz) => _tz = tz;

    public DateTime ToUtc(DateTime value)
    {
        switch (value.Kind)
        {
            case DateTimeKind.Utc:
                return value;
            case DateTimeKind.Local:
                return value.ToUniversalTime();
        }
        var unspecified = DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
        if (_tz.IsInvalidTime(unspecified)) unspecified = unspecified.AddHours(1); // пропавший час при переходе на летнее время
        return TimeZoneInfo.ConvertTimeToUtc(unspecified, _tz);
    }

    private static TimeZoneInfo Resolve(string id)
    {
        foreach (var candidate in new[] { id, "Europe/Moscow", "Russian Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(candidate); }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { }
        }
        // Москва: UTC+3 без перехода на летнее время.
        return TimeZoneInfo.CreateCustomTimeZone("MSK", TimeSpan.FromHours(3), "MSK", "MSK");
    }
}
