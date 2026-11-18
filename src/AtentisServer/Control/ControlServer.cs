using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using AtentisServer.Configuration;
using AtentisServer.Contracts;
using Microsoft.Extensions.Options;

namespace AtentisServer.Control;

/// <summary>
/// Канал управления для «Панели управления»: именованный канал Windows. Доступ только у SYSTEM, администраторов и группы
/// операторов (AtentisServerOperators); сетевые подключения запрещены. Через канал приходят пароли Atentis - они сразу попадают в память
/// службы и больше нигде не сохраняются и не логируются.
/// </summary>
public sealed class ControlServer : BackgroundService
{
    private readonly ControlCommandHandler _handler;
    private readonly ControlOptions _options;
    private readonly ILogger<ControlServer> _log;

    public ControlServer(ControlCommandHandler handler, IOptions<ControlOptions> options, ILogger<ControlServer> log)
    {
        _handler = handler;
        _options = options.Value;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("Канал управления: {Pipe}", _options.PipeName);
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = CreateServer();
                await server.WaitForConnectionAsync(stoppingToken);
                failures = 0;
                var connection = server;
                server = null;
                _ = Task.Run(() => HandleConnectionAsync(connection, stoppingToken), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                server?.Dispose();
                if (failures++ % 20 == 0) _log.LogError(ex, "Ошибка канала управления; повторяю");
                try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(1 + failures, 10)), stoppingToken); }
                catch (OperationCanceledException) { }
            }
        }
    }

    private NamedPipeServerStream CreateServer()
    {
        if (OperatingSystem.IsWindows()) return CreateWindowsServer();
        // Не Windows (разработка/тесты): права доступа к каналу не настраиваются.
        return new NamedPipeServerStream(_options.PipeName, PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    }

    [SupportedOSPlatform("windows")]
    private NamedPipeServerStream CreateWindowsServer()
    {
        var security = new PipeSecurity();
        var self = WindowsIdentity.GetCurrent().User!;
        // Сам процесс службы: нужно право создавать новые экземпляры канала.
        security.AddAccessRule(new PipeAccessRule(self, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        try
        {
            var group = (SecurityIdentifier)new NTAccount(_options.OperatorsGroup).Translate(typeof(SecurityIdentifier));
            security.AddAccessRule(new PipeAccessRule(group, PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
        }
        catch (IdentityNotMappedException)
        {
            _log.LogWarning("Группа «{Group}» не найдена: управлять службой смогут только администраторы", _options.OperatorsGroup);
        }
        // Удалённый доступ по сети (SMB) запрещён.
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));

        return NamedPipeServerStreamAcl.Create(_options.PipeName, PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken stoppingToken)
    {
        await using (pipe)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                cts.CancelAfter(TimeSpan.FromSeconds(30));

                ControlResponse response;
                var line = await PipeLine.ReadAsync(pipe, cts.Token);
                if (line is null || line.Length == 0)
                {
                    response = ControlResponse.Fail(ErrorCodes.Protocol, "Пустой запрос.");
                }
                else
                {
                    ControlRequest? request = null;
                    try { request = JsonSerializer.Deserialize<ControlRequest>(line, ControlPipe.Json); }
                    catch (JsonException) { }
                    response = request is null
                        ? ControlResponse.Fail(ErrorCodes.Protocol, "Не удалось разобрать запрос.")
                        : await _handler.HandleAsync(request, cts.Token);
                }

                var payload = JsonSerializer.SerializeToUtf8Bytes(response, ControlPipe.Json);
                await pipe.WriteAsync(payload, cts.Token);
                await pipe.WriteAsync(new byte[] { (byte)'\n' }, cts.Token);
                await pipe.FlushAsync(cts.Token);
                try { pipe.WaitForPipeDrain(); } catch (PlatformNotSupportedException) { }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or JsonException)
            {
                _log.LogDebug(ex, "Соединение с панелью завершилось с ошибкой");
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Неожиданная ошибка при обработке запроса панели");
            }
        }
    }
}
