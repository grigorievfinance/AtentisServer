using System.Net.Sockets;
using Npgsql;

namespace AtentisServer.Storage;

/// <summary>Понятные пользователю сообщения вместо технических исключений PostgreSQL/сети.</summary>
public static class DbErrorTranslator
{
    public static string Translate(Exception ex)
    {
        if (ex is AggregateException agg && agg.InnerException is not null) ex = agg.InnerException;
        switch (ex)
        {
            case DatabaseNotConfiguredException:
                return "База данных не настроена.";
            case PostgresException pg:
                return pg.SqlState switch
                {
                    "28P01" or "28000" => "PostgreSQL отклонил имя пользователя или пароль.",
                    "3D000" => "Такой базы данных нет на сервере PostgreSQL. Проверьте имя базы.",
                    "42501" => "У пользователя базы данных не хватает прав. Нужно право создавать таблицы в этой базе (см. инструкцию администратора).",
                    "53300" => "На сервере PostgreSQL слишком много подключений.",
                    _ => $"PostgreSQL вернул ошибку: {pg.MessageText}"
                };
            case NpgsqlException { InnerException: SocketException } or SocketException:
                return "Не удаётся подключиться к серверу PostgreSQL. Проверьте адрес, порт и что сервер доступен по сети (файрвол).";
            case TimeoutException or OperationCanceledException:
                return "PostgreSQL не отвечает (истекло время ожидания).";
            case NpgsqlException { InnerException: System.Security.Authentication.AuthenticationException }:
                return "Не удалось установить защищённое соединение с PostgreSQL. Проверьте режим шифрования.";
            default:
                return "Ошибка подключения к базе данных: " + ex.Message;
        }
    }
}
