using AtentisServer.Configuration;
using AtentisServer.Contracts;

namespace AtentisServer.Collection;

/// <summary>Вычисляет состояние слота для пользователя и подсказку, что делать.</summary>
public static class SlotStateResolver
{
    public static (string State, string? Message) Resolve(SlotOptions cfg, SlotRuntime? rt, bool hasPassword)
    {
        if (!cfg.Enabled) return (SlotStates.Disabled, "Выключен");
        if (!cfg.IsConfigured) return (SlotStates.NotConfigured, "Заполните адрес сервера, порт и логин");
        if (!hasPassword)
        {
            return rt?.AuthFailed == true
                ? (SlotStates.AuthFailed, rt.AuthMessage ?? "Atentis отклонил логин или пароль. Введите пароль заново.")
                : (SlotStates.WaitingForPassword, "Ожидает пароль. Введите его в панели управления.");
        }
        if (rt?.Source is null) return (SlotStates.Connecting, rt?.LastError is { Length: > 0 } e ? "Не удалось подключиться, повторяю: " + e : "Подключение…");
        if (rt.IsConnected) return (SlotStates.Connected, null);
        return rt.EverConnected
            ? (SlotStates.Reconnecting, "Связь с Atentis потеряна, переподключаюсь автоматически")
            : (SlotStates.Connecting, rt.Source.LastError is { Length: > 0 } err ? "Подключение… (" + err + ")" : "Подключение…");
    }
}
