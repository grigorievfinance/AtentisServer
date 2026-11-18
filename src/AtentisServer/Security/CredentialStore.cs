namespace AtentisServer.Security;

/// <summary>
/// Пароли Atentis. ТОЛЬКО в памяти процесса: ни в файл, ни в БД, ни в лог они не попадают. После перезапуска службы
/// (или перезагрузки сервера) пароли нужно ввести заново в «Панели управления».
/// Каждому паролю присваивается версия, чтобы ответ «пароль неверный» относился именно к тому паролю, с которым шло подключение.
/// </summary>
public sealed class CredentialStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (string Password, long Version)> _items = new(StringComparer.OrdinalIgnoreCase);
    private long _version;

    /// <summary>Срабатывает при любом изменении (чтобы супервизор отреагировал сразу, а не через несколько секунд).</summary>
    public event Action? Changed;

    public long Set(string slot, string password)
    {
        long v;
        lock (_gate)
        {
            v = ++_version;
            _items[slot] = (password, v);
        }
        Changed?.Invoke();
        return v;
    }

    public bool TryGet(string slot, out string password, out long version)
    {
        lock (_gate)
        {
            if (_items.TryGetValue(slot, out var item)) { password = item.Password; version = item.Version; return true; }
        }
        password = "";
        version = 0;
        return false;
    }

    public bool Has(string slot)
    {
        lock (_gate) return _items.ContainsKey(slot);
    }

    public void Clear(string slot)
    {
        bool removed;
        lock (_gate) removed = _items.Remove(slot);
        if (removed) Changed?.Invoke();
    }

    /// <summary>Удаляет пароль, только если он не был заменён новым с момента неудачной попытки.</summary>
    public bool ClearIfVersion(string slot, long version)
    {
        bool removed = false;
        lock (_gate)
        {
            if (_items.TryGetValue(slot, out var item) && item.Version == version) removed = _items.Remove(slot);
        }
        if (removed) Changed?.Invoke();
        return removed;
    }
}
