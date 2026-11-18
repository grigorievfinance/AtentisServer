using System.Data;
using AtentisServer.Configuration;

namespace AtentisServer.Sources;

/// <summary>
/// Источник данных одного слота Atentis. Отделён интерфейсом, чтобы конвейер сбора можно было
/// тестировать без реального сервера Atentis.
/// </summary>
public interface ISlotSource : IAsyncDisposable
{
    string Name { get; }
    string Market { get; }
    bool IsConnected { get; }
    string State { get; }
    DateTime? LastConnectedUtc { get; }
    string? LastError { get; }

    /// <summary>Вызывается (из потока библиотеки Atentis!) при обновлении таблицы. Обработчик должен быть быстрым.</summary>
    event Action<string>? TableUpdated;

    /// <summary>Atentis отклонил логин/пароль (или требует сменить пароль). Повторять подключение нельзя - можно заблокировать учётную запись.</summary>
    event Action<string>? AuthenticationFailed;

    Task StartAsync(CancellationToken ct);
    Task StopAsync();

    /// <summary>Независимая копия текущего содержимого таблицы либо null, если таблицы нет.</summary>
    DataTable? Snapshot(string tableName);
}

public interface ISlotSourceFactory
{
    /// <param name="password">Пароль Atentis (только из памяти службы).</param>
    ISlotSource Create(SlotOptions slot, string password, IReadOnlyList<TableOptions> tables);
}
