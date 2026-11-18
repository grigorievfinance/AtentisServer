using AtentisServer.Configuration;
using AtentisServer.Contracts;
using AtentisServer.Control;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AtentisServer.Tests;

/// <summary>Настоящий именованный канал: клиент панели &lt;-&gt; ControlServer службы (на Linux - через сокет, права доступа Windows здесь не проверяются).</summary>
public class ControlPipeTests
{
    private static ControlServer StartServer(ControlTestsBase t, string pipe) =>
        new(t.Handler, Microsoft.Extensions.Options.Options.Create(new ControlOptions { PipeName = pipe }), NullLogger<ControlServer>.Instance);

    [Fact]
    public async Task Panel_client_talks_to_the_service_over_the_pipe_and_passwords_reach_only_memory()
    {
        using var t = new ControlTestsBase();
        var pipe = "atentis-test-" + Guid.NewGuid().ToString("N");
        var server = StartServer(t, pipe);
        await server.StartAsync(default);
        try
        {
            var client = new ControlClient(pipe);

            var status = await client.SendAsync(new ControlRequest { Cmd = Commands.Status });
            Assert.True(status.Ok, status.Error);
            Assert.Equal(2, status.Status!.Slots.Count);

            var apply = await client.SendAsync(new ControlRequest
            {
                Cmd = Commands.Apply,
                Settings = ControlTestsBase.Settings(),
                Passwords = new() { ["MICEX"] = "very secret ✓ пароль", ["FORTS"] = "p2" }
            });
            Assert.True(apply.Ok, apply.Error);
            Assert.True(t.Cred.TryGet("MICEX", out var pwd, out _) && pwd == "very secret ✓ пароль");   // юникод проходит без искажений
            Assert.DoesNotContain("very secret", await File.ReadAllTextAsync(t.File));

            var back = await client.SendAsync(new ControlRequest { Cmd = Commands.GetSettings });
            Assert.Equal("10.0.0.1", back.Settings!.Slots[0].Server);
            Assert.True(back.Settings.Database.HasPassword);
            Assert.True(string.IsNullOrEmpty(back.Settings.Database.Password));

            // несколько клиентов одновременно
            var many = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => client.SendAsync(new ControlRequest { Cmd = Commands.Status })));
            Assert.All(many, r => Assert.True(r.Ok, r.Error));
        }
        finally { await server.StopAsync(default); }
    }

    [Fact]
    public async Task Client_reports_a_friendly_message_when_the_service_is_not_running()
    {
        var client = new ControlClient("atentis-nobody-" + Guid.NewGuid().ToString("N"), connectTimeout: TimeSpan.FromMilliseconds(400));
        var r = await client.SendAsync(new ControlRequest { Cmd = Commands.Status });
        Assert.False(r.Ok);
        Assert.Equal(ErrorCodes.Unreachable, r.ErrorCode);
        Assert.Contains("не запущена", r.Error);
    }

    [Fact]
    public async Task Garbage_and_oversized_requests_do_not_hurt_the_server()
    {
        using var t = new ControlTestsBase();
        var pipe = "atentis-test-" + Guid.NewGuid().ToString("N");
        var server = StartServer(t, pipe);
        await server.StartAsync(default);
        try
        {
            await using (var p = new System.IO.Pipes.NamedPipeClientStream(".", pipe, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous))
            {
                await p.ConnectAsync(3000);
                await p.WriteAsync("this is not json\n"u8.ToArray());
                var line = await PipeLine.ReadAsync(p, default);
                Assert.Contains("\"ok\":false", System.Text.Encoding.UTF8.GetString(line!));
            }
            await using (var p = new System.IO.Pipes.NamedPipeClientStream(".", pipe, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous))
            {
                await p.ConnectAsync(3000);
                try { await p.WriteAsync(new byte[ControlPipe.MaxMessageBytes + 4096]); } catch (IOException) { }
            }
            var ok = await new ControlClient(pipe).SendAsync(new ControlRequest { Cmd = Commands.Status });
            Assert.True(ok.Ok, ok.Error);                                              // сервер после мусора жив
        }
        finally { await server.StopAsync(default); }
    }
}
