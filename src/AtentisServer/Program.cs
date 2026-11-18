using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;
using AtentisServer;
using AtentisServer.Collection;
using AtentisServer.Configuration;
using AtentisServer.Control;
using AtentisServer.Security;
using AtentisServer.Monitoring;
using AtentisServer.Sources;
using AtentisServer.Storage;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using Prometheus;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

// Служба Windows стартует с рабочим каталогом C:\Windows\System32 - привязываемся к каталогу приложения.
Directory.SetCurrentDirectory(AppContext.BaseDirectory);

var dataDir = Environment.GetEnvironmentVariable("ATENTIS_DATA_DIR")
              ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AtentisServer");
var logDir = Path.Combine(dataDir, "logs");

var bootstrap = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .WriteTo.File(Path.Combine(logDir, "bootstrap-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7);
if (OperatingSystem.IsWindows())
    bootstrap.WriteTo.EventLog("AtentisServer", manageEventSource: false, restrictedToMinimumLevel: LogEventLevel.Error);
Log.Logger = bootstrap.CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args = args,
        ContentRootPath = AppContext.BaseDirectory
    });

    // Конфигурация: appsettings.json -> переменные окружения -> файл настроек %ProgramData%\AtentisServer\appsettings.local.json -> аргументы.
    // Файл настроек читается при каждом запуске службы; в нём можно хранить ВСЕ параметры, включая пароли (доступ ограничен ACL).
    AppConfiguration.AddLocalSources(builder.Configuration, dataDir, args);

    builder.Host.UseWindowsService(o => o.ServiceName = "AtentisServer");

    builder.Host.UseSerilog((ctx, services, lc) =>
    {
        lc.ReadFrom.Configuration(ctx.Configuration)
          .Enrich.FromLogContext()
          .Enrich.WithProperty("App", "AtentisServer")
          .Enrich.WithProperty("Version", AppInfo.Version)
          .Enrich.WithProperty("Host", Environment.MachineName)
          // Читаемый текстовый лог для людей
          .WriteTo.File(Path.Combine(logDir, "atentis-.log"),
              rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30,
              fileSizeLimitBytes: 200_000_000, rollOnFileSizeLimit: true, shared: false,
              outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
          // Структурированный JSON для Grafana Loki / Alloy / ELK
          .WriteTo.File(new CompactJsonFormatter(), Path.Combine(logDir, "atentis-.json"),
              rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14,
              fileSizeLimitBytes: 200_000_000, rollOnFileSizeLimit: true);

        if (OperatingSystem.IsWindows())
        {
            // Предупреждения и ошибки - в журнал Windows (Application, источник AtentisServer): их видят стандартные средства мониторинга.
            lc.WriteTo.EventLog("AtentisServer", manageEventSource: false, restrictedToMinimumLevel: LogEventLevel.Warning);
        }
        if (!WindowsServiceHelpers.IsWindowsService()) lc.WriteTo.Console();
    });

    // Настройки читаются и применяются НА ЛЕТУ (без перезапуска службы): пароли Atentis хранятся только в памяти.
    builder.Services.AddOptions<CollectorOptions>().Bind(builder.Configuration.GetSection(CollectorOptions.SectionName));
    builder.Services.AddOptions<DatabaseOptions>().Bind(builder.Configuration.GetSection(DatabaseOptions.SectionName))
        .ValidateDataAnnotations().ValidateOnStart();
    builder.Services.AddOptions<MonitoringOptions>().Bind(builder.Configuration.GetSection(MonitoringOptions.SectionName));
    builder.Services.AddOptions<ControlOptions>().Bind(builder.Configuration.GetSection(ControlOptions.SectionName));

    builder.Services.AddSingleton<IConfigurationRoot>(builder.Configuration);
    builder.Services.AddSingleton(sp => new NpgsqlDataSourceProvider(sp.GetRequiredService<IConfiguration>(),
        sp.GetRequiredService<ILogger<NpgsqlDataSourceProvider>>()));
    builder.Services.AddSingleton<IDataStore, PostgresStore>();
    builder.Services.AddSingleton<SchemaMigrator>();
    builder.Services.AddSingleton<IDatabaseGate, PostgresDatabaseGate>();
    builder.Services.AddSingleton<CredentialStore>();
    builder.Services.AddSingleton<CollectorState>();
    builder.Services.AddSingleton<ISlotSourceFactory, AtentisSlotSourceFactory>();
    builder.Services.AddSingleton<CollectorWorker>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<CollectorWorker>());

    builder.Services.AddSingleton<ILocalSettingsStore>(sp => new LocalSettingsStore(
        sp.GetRequiredService<IConfigurationRoot>(), sp.GetRequiredService<IOptionsMonitor<CollectorOptions>>(),
        AppConfiguration.LocalFilePath(dataDir)));
    builder.Services.AddSingleton<IStatusService, StatusService>();
    builder.Services.AddSingleton<IDatabaseTester, DatabaseTester>();
    builder.Services.AddSingleton<ControlCommandHandler>();
    builder.Services.AddHostedService<ControlServer>();

    builder.Services.AddHealthChecks()
        .AddCheck<DatabaseHealthCheck>("postgres", tags: new[] { "ready" })
        .AddCheck<SlotsHealthCheck>("atentis_slots", tags: new[] { "ready" });

    builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(30));

    var monitoring = builder.Configuration.GetSection(MonitoringOptions.SectionName).Get<MonitoringOptions>() ?? new MonitoringOptions();
    builder.WebHost.UseUrls(monitoring.Url);

    var app = builder.Build();

    var settingsFile = AppConfiguration.LocalFilePath(dataDir);
    if (File.Exists(settingsFile))
        Log.Information("Файл настроек загружен: {SettingsFile}", settingsFile);
    else
        Log.Information("Файл настроек ещё не создан: {SettingsFile}. Он появится, когда пользователь сохранит настройки в «Панели управления»", settingsFile);

    app.MapMetrics("/metrics");
    app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
    app.MapHealthChecks("/health/ready", new HealthCheckOptions
    {
        Predicate = r => r.Tags.Contains("ready"),
        ResultStatusCodes =
        {
            [HealthStatus.Healthy] = StatusCodes.Status200OK,
            [HealthStatus.Degraded] = StatusCodes.Status200OK,
            [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable
        }
    });

    await app.RunAsync();
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "AtentisServer не смог запуститься");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}
