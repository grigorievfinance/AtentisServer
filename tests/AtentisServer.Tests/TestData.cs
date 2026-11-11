using System.Data;

namespace AtentisServer.Tests;

internal static class TestData
{
    /// <summary>Таблица TRADES как её отдаёт Atentis (имена как в документации; типы Quantity бывают int/long).</summary>
    public static DataTable Trades(Type? quantityType = null)
    {
        var t = new DataTable("TRADES");
        t.Columns.Add("TradeNo", typeof(long));
        t.Columns.Add("RealTradeNo", typeof(long));
        t.Columns.Add("OrderNo", typeof(long));
        t.Columns.Add("TradeTime", typeof(DateTime));
        t.Columns.Add("SecBoard", typeof(string));
        t.Columns.Add("SecCode", typeof(string));
        t.Columns.Add("BuySell", typeof(string));
        t.Columns.Add("Quantity", quantityType ?? typeof(int));
        t.Columns.Add("Price", typeof(double));
        t.Columns.Add("Value", typeof(double));
        t.Columns.Add("Commission", typeof(double));
        t.Columns.Add("ClearingCenterComm", typeof(double));
        t.Columns.Add("ExchangeComm", typeof(double));
        t.Columns.Add("Account", typeof(string));
        return t;
    }

    public static void AddTrade(DataTable t, long real, string code = "SBER", string side = "B", long qty = 10,
        double price = 250.5, string account = "L01-00000F00", DateTime? time = null)
    {
        var r = t.NewRow();
        r["TradeNo"] = real + 1000;
        r["RealTradeNo"] = real;
        r["OrderNo"] = real + 5000;
        r["TradeTime"] = time ?? new DateTime(2026, 11, 11, 10, 0, 0);
        r["SecBoard"] = "TQBR  ";          // Atentis дополняет текст пробелами
        r["SecCode"] = code + "   ";
        r["BuySell"] = side;
        r["Quantity"] = Convert.ChangeType(qty, t.Columns["Quantity"]!.DataType);
        r["Price"] = price;
        r["Value"] = price * qty * 10;
        r["Commission"] = 1.25;
        r["ClearingCenterComm"] = 0.1;
        r["ExchangeComm"] = 0.2;
        r["Account"] = account + " ";
        t.Rows.Add(r);
    }
}
