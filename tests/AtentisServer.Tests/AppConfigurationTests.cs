using AtentisServer.Configuration;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AtentisServer.Tests;

public sealed class AppConfigurationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "atentis-cfg-" + Guid.NewGuid().ToString("N"));
    private readonly string _envVar = "ATENTIS_TEST_" + Guid.NewGuid().ToString("N");

    public AppConfigurationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(_envVar, null);
        Directory.Delete(_dir, recursive: true);
    }

    /// <summary>Те же источники, что у WebApplication.CreateBuilder: json -> переменные окружения -> командная строка, затем наш файл.</summary>
    private IConfigurationRoot Build(string baseJson, string[]? args = null)
    {
        args ??= Array.Empty<string>();
        var baseFile = Path.Combine(_dir, "appsettings.json");
        File.WriteAllText(baseFile, baseJson);
        var b = new ConfigurationBuilder()
            .AddJsonFile(baseFile)
            .AddEnvironmentVariables()
            .AddCommandLine(args);
        AppConfiguration.AddLocalSources(b, _dir, args);
        return b.Build();
    }

    private void WriteLocal(string json) => File.WriteAllText(AppConfiguration.LocalFilePath(_dir), json);

    [Fact]
    public void Missing_settings_file_is_fine()
    {
        var cfg = Build("""{ "A": "base" }""");
        Assert.Equal("base", cfg["A"]);
    }

    [Fact]
    public void Settings_file_overrides_appsettings_json_and_is_read_on_each_start()
    {
        var cfg1 = Build("""{ "Collector": { "BatchSize": 1000 } }""");
        Assert.Equal("1000", cfg1["Collector:BatchSize"]);

        WriteLocal("""{ "Collector": { "BatchSize": 77 } }""");
        var cfg2 = Build("""{ "Collector": { "BatchSize": 1000 } }""");       // «перезапуск» службы
        Assert.Equal("77", cfg2["Collector:BatchSize"]);
    }

    [Fact]
    public void Settings_file_wins_over_stale_environment_variables()
    {
        Environment.SetEnvironmentVariable(_envVar, "from-env");
        WriteLocal($$"""{ "{{_envVar}}": "from-file" }""");
        Assert.Equal("from-file", Build("{}")[_envVar]);
    }

    [Fact]
    public void Environment_still_applies_for_keys_missing_in_the_file()
    {
        Environment.SetEnvironmentVariable(_envVar, "from-env");
        WriteLocal("""{ "Other": "x" }""");
        Assert.Equal("from-env", Build("{}")[_envVar]);
    }

    [Fact]
    public void Command_line_wins_over_the_file()
    {
        WriteLocal("""{ "Monitoring": { "Url": "http://file:1" } }""");
        var cfg = Build("{}", new[] { "--Monitoring:Url=http://cli:2" });
        Assert.Equal("http://cli:2", cfg["Monitoring:Url"]);
    }

    [Fact]
    public void Shipped_template_parses_with_comments_and_is_rejected_until_filled_in()
    {
        var template = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "template", "appsettings.local.template.json"));
        WriteLocal(template);
        var cfg = Build(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "appsettings.json")));
        var opts = cfg.GetSection(CollectorOptions.SectionName).Get<CollectorOptions>()!;

        // шаблон читается, несмотря на комментарии, и по умолчанию НЕ проходит проверку: Server/Port/Login/Password пустые
        var result = new CollectorOptionsValidator().Validate(null, opts);
        Assert.True(result.Failed);
        Assert.Contains("Password", string.Join(";", result.Failures!));
        Assert.Equal("MICEX", opts.Slots[0].Name);
        Assert.Contains("Host=localhost", cfg.GetConnectionString("Postgres"));
    }

    [Fact]
    public void Template_filled_in_by_the_user_passes_validation()
    {
        var template = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "template", "appsettings.local.template.json"));
        template = template.Replace("\"Server\": \"\"", "\"Server\": \"10.0.0.1\"")
                           .Replace("\"Port\": 0", "\"Port\": 7800")
                           .Replace("\"Login\": \"\"", "\"Login\": \"user\"")
                           .Replace("\"Password\": \"\"", "\"Password\": \"p@ss;word\"");
        WriteLocal(template);
        var cfg = Build(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "appsettings.json")));
        var opts = cfg.GetSection(CollectorOptions.SectionName).Get<CollectorOptions>()!;

        Assert.True(new CollectorOptionsValidator().Validate(null, opts).Succeeded);
        Assert.Equal(2, opts.Slots.Count);
        Assert.Equal("p@ss;word", opts.Slots[1].Password);
        Assert.Equal(5, opts.Tables.Count);          // таблицы из appsettings.json остались
    }
}
