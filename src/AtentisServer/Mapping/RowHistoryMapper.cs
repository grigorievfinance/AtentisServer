using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AtentisServer.Storage;

namespace AtentisServer.Mapping;

/// <summary>
/// Универсальное представление строки произвольной таблицы Atentis (ORDERS, POSITIONS, FIRM_HOLDING_TOTAL)
/// в виде (ключ, hash, json). Hash считается по содержимому: если он не изменился - строку не пишем.
/// </summary>
public sealed class RowHistoryMapper
{
    public MapResult<TableRowRecord> Map(DataTable table, IReadOnlyList<string> keyFields, out bool keyFieldsMissing)
    {
        var keyCols = keyFields.Where(k => table.Columns.Contains(k)).ToList();
        keyFieldsMissing = keyFields.Count > 0 && keyCols.Count != keyFields.Count;

        var items = new List<TableRowRecord>(table.Rows.Count);
        foreach (DataRow row in table.Rows)
        {
            if (row.RowState == DataRowState.Deleted) continue;
            var json = ToJson(row, table);
            var hash = Hash(json);
            // Если ключевые поля не заданы/не найдены - ключом считается содержимое строки (тогда пишется каждое новое состояние).
            var key = keyCols.Count == 0 || keyFieldsMissing
                ? hash
                : string.Join("|", keyCols.Select(k => RowReader.Str(row, k) ?? ""));
            items.Add(new TableRowRecord(key, hash, json));
        }
        return new MapResult<TableRowRecord>(items, new Dictionary<string, int>());
    }

    internal static string ToJson(DataRow row, DataTable table)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            foreach (DataColumn col in table.Columns)
            {
                w.WritePropertyName(col.ColumnName);
                var v = row[col];
                switch (v)
                {
                    case DBNull: w.WriteNullValue(); break;
                    case string s: w.WriteStringValue(s.Trim()); break;
                    case bool b: w.WriteBooleanValue(b); break;
                    case byte or short or int or long: w.WriteNumberValue(Convert.ToInt64(v, CultureInfo.InvariantCulture)); break;
                    case double d when double.IsFinite(d): w.WriteNumberValue(d); break;
                    case float f when float.IsFinite(f): w.WriteNumberValue(f); break;
                    case decimal m: w.WriteNumberValue(m); break;
                    case DateTime dt: w.WriteStringValue(dt.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture)); break;
                    default: w.WriteStringValue(Convert.ToString(v, CultureInfo.InvariantCulture)); break;
                }
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    internal static string Hash(string json) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..32].ToLowerInvariant();
}
