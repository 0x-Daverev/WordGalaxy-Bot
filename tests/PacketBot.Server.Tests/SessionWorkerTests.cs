using FunnyProject.Sessions;
using Microsoft.Extensions.Hosting;
using PacketBot.Core.Tests;
using PacketBot.Server;
using Xunit;

namespace PacketBot.Server.Tests;

// Unattended start: network errors are retried with bounded waits, a
// rejected session is not, and the user is told through Telegram.
public class SessionWorkerTests : IDisposable
{
    private readonly FakeGameServer gameServer = new();
    private readonly Lifetime lifetime = new();
    private int mapCalls;
    private ServerInstanceHost? host;

    private sealed class Lifetime : IHostApplicationLifetime
    {
        public readonly TaskCompletionSource Stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => Stopped.TrySetResult();
    }

    public void Dispose()
    {
        host?.Dispose();
        gameServer.Dispose();
    }

    private (SessionWorker Worker, List<int> ExitCodes) Create(Func<int, HttpResponseMessage> map,
        TimeSpan failureExitDelay = default)
    {
        var web = new WordGalaxyWebClient(cookies => new FakeWordGalaxyHttp(cookies,
            (_, _) => map(Interlocked.Increment(ref mapCalls))));
        host = new ServerInstanceHost(new InstanceManager(web, gameServer.Port));
        var options = new ServerOptions(new[] { "test-php-session-0001" }, false, TimeSpan.FromSeconds(5),
            FailureExitDelay: failureExitDelay);
        var codes = new List<int>();
        var worker = new SessionWorker(options, host, lifetime)
        {
            OpenRetryDelays = new[] { TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50) },
            SetExitCode = codes.Add
        };
        return (worker, codes);
    }

    [Fact]
    public async Task NetworkErrorsAtStartAreRetried()
    {
        var (worker, codes) = Create(call => call <= 2
            ? throw new HttpRequestException("Name or service not known")
            : FakeWordGalaxyHttp.Html(FakeWordGalaxyHttp.MapHtml(host: "127.0.0.1")));
        await worker.StartAsync(CancellationToken.None);
        gameServer.NextConnection();
        await FakeGameServer.WaitUntil(() => host!.Typed.Count == 1);
        Assert.Equal(3, mapCalls);
        Assert.Empty(codes);
        Assert.False(host!.TryTakeProblem(out _));
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task GivesUpAfterTheLastRetryAndTellsTheUser()
    {
        var (worker, codes) = Create(_ => throw new HttpRequestException("Connection refused"));
        await worker.StartAsync(CancellationToken.None);
        await lifetime.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, mapCalls); // first attempt + 2 retries
        Assert.Equal(new[] { 1 }, codes);
        Assert.True(host!.TryTakeProblem(out string problem));
        Assert.Contains("No se pudo abrir la cuenta 1: Connection refused (error de red", problem);
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task FailureExitWaitsSoContainerRestartsDoNotLoop()
    {
        var (worker, codes) = Create(_ => FakeWordGalaxyHttp.Html("<html>login</html>"), TimeSpan.FromMilliseconds(800));
        var started = DateTime.UtcNow;
        await worker.StartAsync(CancellationToken.None);
        await lifetime.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(DateTime.UtcNow - started >= TimeSpan.FromMilliseconds(700));
        Assert.Equal(new[] { 1 }, codes);
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopDuringTheFailureWaitIsANormalStop()
    {
        var (worker, codes) = Create(_ => FakeWordGalaxyHttp.Html("<html>login</html>"), TimeSpan.FromMinutes(5));
        await worker.StartAsync(CancellationToken.None);
        await FakeGameServer.WaitUntil(() => mapCalls == 1);
        await Task.Delay(200);
        await worker.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(codes);
        Assert.False(lifetime.Stopped.Task.IsCompleted);
    }

    [Fact]
    public async Task RejectedSessionIsNotRetried()
    {
        var (worker, codes) = Create(_ => FakeWordGalaxyHttp.Html("<html>login</html>"));
        await worker.StartAsync(CancellationToken.None);
        await lifetime.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, mapCalls);
        Assert.Equal(new[] { 1 }, codes);
        Assert.True(host!.TryTakeProblem(out string problem));
        Assert.Contains("PHPSESSID", problem);
        Assert.DoesNotContain("test-php-session-0001", problem);
        await worker.StopAsync(CancellationToken.None);
    }
}
