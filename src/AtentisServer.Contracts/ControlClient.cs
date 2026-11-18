using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace AtentisServer.Contracts;

public interface IControlClient
{
    Task<ControlResponse> SendAsync(ControlRequest request, CancellationToken ct = default);
}

/// <summary>Клиент канала управления. Никогда не бросает исключения: все сбои возвращаются как ControlResponse с понятным текстом.</summary>
public sealed class ControlClient : IControlClient
{
    private readonly string _pipeName;
    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan _responseTimeout;

    public ControlClient(string pipeName = ControlPipe.Name, TimeSpan? connectTimeout = null, TimeSpan? responseTimeout = null)
    {
        _pipeName = pipeName;
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(3);
        _responseTimeout = responseTimeout ?? TimeSpan.FromSeconds(30);
    }

    public async Task<ControlResponse> SendAsync(ControlRequest request, CancellationToken ct = default)
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                connectCts.CancelAfter(_connectTimeout);
                try { await pipe.ConnectAsync(connectCts.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return ControlResponse.Fail(ErrorCodes.Unreachable,
                        "Служба AtentisServer не отвечает. Возможно, она не запущена. Обратитесь к администратору сервера.");
                }
            }

            var payload = JsonSerializer.SerializeToUtf8Bytes(request, ControlPipe.Json);
            if (payload.Length > ControlPipe.MaxMessageBytes)
                return ControlResponse.Fail(ErrorCodes.Invalid, "Слишком большой запрос.");
            await pipe.WriteAsync(payload, ct);
            await pipe.WriteAsync(new byte[] { (byte)'\n' }, ct);
            await pipe.FlushAsync(ct);

            using var respCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            respCts.CancelAfter(_responseTimeout);
            var line = await PipeLine.ReadAsync(pipe, respCts.Token);
            if (line is null) return ControlResponse.Fail(ErrorCodes.Protocol, "Служба закрыла соединение без ответа.");
            return JsonSerializer.Deserialize<ControlResponse>(line, ControlPipe.Json)
                   ?? ControlResponse.Fail(ErrorCodes.Protocol, "Пустой ответ службы.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            return ControlResponse.Fail(ErrorCodes.Timeout, "Служба не ответила вовремя. Попробуйте ещё раз.");
        }
        catch (UnauthorizedAccessException)
        {
            return ControlResponse.Fail(ErrorCodes.AccessDenied,
                "Нет прав на управление службой. Обратитесь к администратору сервера.");
        }
        catch (JsonException)
        {
            return ControlResponse.Fail(ErrorCodes.Protocol, "Служба вернула непонятный ответ. Возможно, версии панели и службы не совпадают.");
        }
        catch (IOException)
        {
            return ControlResponse.Fail(ErrorCodes.Unreachable, "Связь со службой AtentisServer прервана.");
        }
    }
}

/// <summary>Чтение одной строки (запрос/ответ) из потока канала с ограничением размера.</summary>
public static class PipeLine
{
    public static async Task<byte[]?> ReadAsync(Stream s, CancellationToken ct)
    {
        var buf = new List<byte>(512);
        var one = new byte[1];
        while (true)
        {
            var n = await s.ReadAsync(one.AsMemory(0, 1), ct);
            if (n == 0) return buf.Count == 0 ? null : buf.ToArray();
            if (one[0] == (byte)'\n') return buf.ToArray();
            buf.Add(one[0]);
            if (buf.Count > ControlPipe.MaxMessageBytes) throw new JsonException("message too large");
        }
    }
}
