using System.Data;
using System.Globalization;

namespace AtentisServer.Mapping;

/// <summary>
/// Безопасное чтение полей DataRow из таблиц Atentis. Имена полей регистронезависимы
/// (в документации - TradeNo, в старом коде - TRADENO), типы бывают разными в зависимости от сервера
/// (Quantity - int или long), строки могут быть дополнены пробелами.
/// </summary>
internal static class RowReader
{
    public static bool Has(DataRow row, string name) => row.Table.Columns.Contains(name);

    public static object? Raw(DataRow row, string name)
    {
        if (!row.Table.Columns.Contains(name)) return null;
        var v = row[name];
        return v is DBNull ? null : v;
    }

    public static string? Str(DataRow row, string name)
    {
        var v = Raw(row, name);
        return v is null ? null : Convert.ToString(v, CultureInfo.InvariantCulture)?.Trim();
    }

    public static long? Long(DataRow row, string name)
    {
        var v = Raw(row, name);
        if (v is null) return null;
        try
        {
            return v switch
            {
                long l => l,
                int i => i,
                short s => s,
                byte b => b,
                double d => checked((long)Math.Round(d)),
                decimal m => checked((long)Math.Round(m)),
                string str when long.TryParse(str.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) => p,
                _ => Convert.ToInt64(v, CultureInfo.InvariantCulture)
            };
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }

    public static decimal? Decimal(DataRow row, string name)
    {
        var v = Raw(row, name);
        if (v is null) return null;
        try
        {
            return v switch
            {
                decimal m => m,
                double d when double.IsNaN(d) || double.IsInfinity(d) => null,
                double d => Convert.ToDecimal(d, CultureInfo.InvariantCulture),
                string str when decimal.TryParse(str.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var p) => p,
                _ => Convert.ToDecimal(v, CultureInfo.InvariantCulture)
            };
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }

    public static DateTime? DateTime(DataRow row, string name)
    {
        var v = Raw(row, name);
        if (v is null) return null;
        if (v is System.DateTime dt) return dt;
        return System.DateTime.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var parsed) ? parsed : null;
    }
}
