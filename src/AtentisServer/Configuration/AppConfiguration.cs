namespace AtentisServer.Configuration;

/// <summary>
/// Источники конфигурации. Приоритет (последний побеждает):
///   1. appsettings.json рядом с программой (значения по умолчанию, обновляется вместе с программой)
///   2. переменные окружения
///   3. ФАЙЛ НАСТРОЕК %ProgramData%\AtentisServer\appsettings.local.json  (читается при КАЖДОМ запуске службы)
///   4. аргументы командной строки
/// Файл настроек выше переменных окружения намеренно: то, что написано в файле, всегда действует
/// и не может быть тихо перекрыто забытой переменной окружения.
/// </summary>
public static class AppConfiguration
{
    public const string LocalFileName = "appsettings.local.json";

    public static string LocalFilePath(string dataDir) => Path.Combine(dataDir, LocalFileName);

    /// <summary>Вызывается после источников по умолчанию (json + переменные окружения + командная строка) из WebApplication.CreateBuilder.</summary>
    public static void AddLocalSources(IConfigurationBuilder builder, string dataDir, string[] args)
    {
        builder.AddJsonFile(LocalFilePath(dataDir), optional: true, reloadOnChange: false);
        builder.AddCommandLine(args);   // аргументы остаются самыми приоритетными
    }
}
