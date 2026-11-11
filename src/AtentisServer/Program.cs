using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;
using AtentisServer;
using AtentisServer.Collection;
using AtentisServer.Configuration;
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

    // Конфигурация: appsettings.json -> %ProgramData%\AtentisServer\appsettings.local.json -> переменные окружения -> аргументы.
    // Секреты (пароли) - только в переменных окружения службы или в appsettings.local.json с ACL, но не в репозитории.
    builder.Configuration.AddJsonFile(Path.Combine(dataDir, "appsettings.local.json"), optional: true, reloadOnChange: false);
    builder.Configuration.AddEnvironmentVariables();
    builder.Configuration.AddCommandLine(args);

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

    builder.Services.AddSingleton<IValidateOptions<CollectorOptions>, CollectorOptionsValidator>();
    builder.Services.AddOptions<CollectorOptions>().Bind(builder.Configuration.GetSection(CollectorOptions.SectionName)).ValidateOnStart();
    builder.Services.AddOptions<DatabaseOptions>().Bind(builder.Configuration.GetSection(DatabaseOptions.SectionName))
        .ValidateDataAnnotations().ValidateOnStart();
    builder.Services.AddOptions<MonitoringOptions>().Bind(builder.Configuration.GetSection(MonitoringOptions.SectionName));

    var connectionString = builder.Configuration.GetConnectionString("Postgres");
    if (string.IsNullOrWhiteSpace(connectionString))
        throw new InvalidOperationException("Не задана строка подключения ConnectionStrings:Postgres (переменная окружения ConnectionStrings__Postgres).");
    var csb = new NpgsqlConnectionStringBuilder(connectionString) { ApplicationName = "AtentisServer" };
    builder.Services.AddSingleton(_ => new NpgsqlDataSourceBuilder(csb.ConnectionString).Build());

    builder.Services.AddSingleton<IDataStore, PostgresStore>();
    builder.Services.AddSingleton<SchemaMigrator>();
    builder.Services.AddSingleton<CollectorState>();
    builder.Services.AddSingleton<ISlotSourceFactory, AtentisSlotSourceFactory>();
    builder.Services.AddSingleton<CollectorWorker>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<CollectorWorker>());

    builder.Services.AddHealthChecks()
        .AddCheck<DatabaseHealthCheck>("postgres", tags: new[] { "ready" })
        .AddCheck<SlotsHealthCheck>("atentis_slots", tags: new[] { "ready" });

    builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(30));

    var monitoring = builder.Configuration.GetSection(MonitoringOptions.SectionName).Get<MonitoringOptions>() ?? new MonitoringOptions();
    builder.WebHost.UseUrls(monitoring.Url);

    var app = builder.Build();

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
