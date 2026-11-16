using FunnyProject;
using FunnyProject.Logic;
using FunnyProject.Modules;
using FunnyProject.Navigation;
using FunnyProject.Runtime;
using FunnyProject.Sessions;
using Xunit;

namespace PacketBot.Core.Tests;

// BotRunner lifecycle and TCP policy against the loopback fake game server.
public class BotRunnerTests : IDisposable
{
    private const string PhpSessId = "test-php-session-0001";
    private readonly FakeGameServer server = new();
    private readonly InstanceManager manager;

    public BotRunnerTests()
    {
        var web = new WordGalaxyWebClient(cookies => new FakeWordGalaxyHttp(cookies,
            (_, _) => FakeWordGalaxyHttp.Html(FakeWordGalaxyHttp.MapHtml(host: "127.0.0.1"))));
        manager = new InstanceManager(web, server.Port);
    }

    public void Dispose()
    {
        manager.Dispose();
        server.Dispose();
    }

    private async Task<(WordGalaxySession Session, FakeGameServer.Connection Connection)> OpenAsync()
    {
        var session = await manager.OpenAsync(PhpSessId);
        return (session, server.NextConnection());
    }

    // Records what the module saw and runs until cancelled or released.
    private sealed class ProbeModule : IBotModule
    {
        public readonly TaskCompletionSource<bool> Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool CancelledByToken;
        public Exception? Throw;
        public string Name => "Probe";

        public async Task RunAsync(BotModuleContext context, CancellationToken token)
        {
            Started.TrySetResult(context.Api.User.PacketManager!.Connected);
            context.Report("probe running");
            try { await Release.Task.WaitAsync(token); }
            catch (OperationCanceledException) { CancelledByToken = true; throw; }
            if (Throw != null) throw Throw;
        }
    }

    [Fact]
    public async Task StartEnablesReconnectAndStopCancelsEverything()
    {
        var (session, _) = await OpenAsync();
        var runner = session.Api.Runner;
        var states = new List<BotRunnerState>();
        runner.StateChanged += (_, s) => { lock (states) states.Add(s); };
        var probe = new ProbeModule();

        Assert.True(await runner.StartAsync(probe));
        Assert.True(await probe.Started.Task);
        Assert.Equal(BotRunnerState.Running, runner.State);
        Assert.True(session.PacketManager.AutoReconnectEnabled);
        Assert.Equal("probe running", runner.Status);
        Assert.Equal("Probe", runner.ModuleName);

        runner.Stop();
        Assert.Equal(BotRunnerState.Stopped, runner.State);
        Assert.False(session.PacketManager.AutoReconnectEnabled);
        Assert.Equal(BotAction.Idle, session.Api.User.ActionState.Current);
        await runner.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(probe.CancelledByToken);
        Assert.Equal(new[] { BotRunnerState.Starting, BotRunnerState.Running, BotRunnerState.Stopped }, states);
    }

    [Fact]
    public async Task SecondStartWhileRunningIsIgnored()
    {
        var (session, _) = await OpenAsync();
        Assert.True(await session.Api.Runner.StartAsync(new ProbeModule()));
        Assert.False(await session.Api.Runner.StartAsync(new ProbeModule()));
    }

    [Fact]
    public async Task StartWithTcpDownReconnectsBeforeRunningTheModule()
    {
        var (session, first) = await OpenAsync();
        first.Client.Close();
        await FakeGameServer.WaitUntil(() => !session.PacketManager.Connected);
        Assert.True(server.NoConnectionWithin(500)); // stopped bot: no reconnect by itself

        var probe = new ProbeModule();
        Assert.True(await session.Api.Runner.StartAsync(probe));
        Assert.True(await probe.Started.Task); // TCP was already up when the module started
        var reconnected = server.NextConnection();
        await FakeGameServer.WaitUntil(() => reconnected.Snapshot().Length > 0); // Login sent again
    }

    [Fact]
    public async Task FailedReconnectLeavesTheBotStopped()
    {
        var (session, first) = await OpenAsync();
        server.Dispose(); // the game server is gone
        first.Client.Close();
        await FakeGameServer.WaitUntil(() => !session.PacketManager.Connected);

        var probe = new ProbeModule();
        await Assert.ThrowsAnyAsync<Exception>(() => session.Api.Runner.StartAsync(probe));
        Assert.Equal(BotRunnerState.Stopped, session.Api.Runner.State);
        Assert.False(session.PacketManager.AutoReconnectEnabled);
        Assert.False(probe.Started.Task.IsCompleted);
        Assert.NotNull(session.Api.Runner.LastError);
    }

    [Fact]
    public async Task RunningBotReconnectsAfterDropButNotAfterStop()
    {
        var (session, first) = await OpenAsync();
        var runner = session.Api.Runner;
        Assert.True(await runner.StartAsync(new ProbeModule()));

        first.Client.Close();
        var second = server.NextConnection(timeoutMs: 10_000);
        await FakeGameServer.WaitUntil(() => session.PacketManager.Connected);

        runner.Stop();
        second.Client.Close();
        await FakeGameServer.WaitUntil(() => !session.PacketManager.Connected);
        Assert.True(server.NoConnectionWithin(4_500));
    }

    [Fact]
    public async Task ModuleFinishingByItselfStopsTheBot()
    {
        var (session, _) = await OpenAsync();
        var probe = new ProbeModule();
        Assert.True(await session.Api.Runner.StartAsync(probe));
        probe.Release.SetResult();
        await session.Api.Runner.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(BotRunnerState.Stopped, session.Api.Runner.State);
        Assert.False(session.PacketManager.AutoReconnectEnabled);
    }

    [Fact]
    public async Task ModuleErrorFaultsTheBotAndAllowsRestart()
    {
        var (session, _) = await OpenAsync();
        var runner = session.Api.Runner;
        var probe = new ProbeModule { Throw = new InvalidOperationException("boom") };
        Assert.True(await runner.StartAsync(probe));
        probe.Release.SetResult();
        await runner.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(BotRunnerState.Faulted, runner.State);
        Assert.Equal("boom", runner.LastError?.Message);
        Assert.False(session.PacketManager.AutoReconnectEnabled);

        Assert.True(await runner.StartAsync(new ProbeModule()));
        Assert.Equal(BotRunnerState.Running, runner.State);
        Assert.Null(runner.LastError);
    }

    [Fact]
    public async Task StopReasonAndCountAreRecorded()
    {
        var (session, _) = await OpenAsync();
        var runner = session.Api.Runner;
        Assert.Equal((BotStopReason.None, 0), (runner.LastStopReason, runner.StopCount));

        Assert.True(await runner.StartAsync(new ProbeModule()));
        runner.Stop();
        Assert.Equal((BotStopReason.Requested, 1), (runner.LastStopReason, runner.StopCount));

        var done = new ProbeModule();
        Assert.True(await runner.StartAsync(done));
        Assert.Equal(BotStopReason.None, runner.LastStopReason);
        done.Release.SetResult();
        await runner.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal((BotStopReason.Completed, 2), (runner.LastStopReason, runner.StopCount));

        var failing = new ProbeModule { Throw = new InvalidOperationException("boom") };
        Assert.True(await runner.StartAsync(failing));
        failing.Release.SetResult();
        await runner.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal((BotStopReason.Faulted, 3), (runner.LastStopReason, runner.StopCount));
    }

    [Fact]
    public async Task FailedTcpRestoreIsRecordedAsStartFailed()
    {
        var (session, first) = await OpenAsync();
        server.Dispose();
        first.Client.Close();
        await FakeGameServer.WaitUntil(() => !session.PacketManager.Connected);
        await Assert.ThrowsAnyAsync<Exception>(() => session.Api.Runner.StartAsync(new ProbeModule()));
        Assert.Equal((BotStopReason.StartFailed, 1), (session.Api.Runner.LastStopReason, session.Api.Runner.StopCount));
    }

    [Fact]
    public async Task StatusFromAStoppedRunIsIgnored()
    {
        var (session, _) = await OpenAsync();
        var runner = session.Api.Runner;
        BotModuleContext? oldContext = null;
        var first = new DelegateBotModule("old", async (context, token) =>
        {
            oldContext = context;
            await Task.Delay(Timeout.Infinite, token);
        });
        Assert.True(await runner.StartAsync(first));
        runner.Stop();
        Assert.True(await runner.StartAsync(new ProbeModule()));
        oldContext!.Report("stale");
        Assert.Equal("probe running", runner.Status);
    }

    [Fact]
    public async Task ClosingTheSessionStopsTheBot()
    {
        var (session, _) = await OpenAsync();
        var probe = new ProbeModule();
        Assert.True(await session.Api.Runner.StartAsync(probe));
        manager.Close(session.UserId);
        Assert.Equal(BotRunnerState.Stopped, session.Api.Runner.State);
        await session.Api.Runner.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(probe.CancelledByToken);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.Api.Runner.StartAsync(new ProbeModule()));
    }

    [Fact]
    public async Task CollectRunsOnTheTargetMapAndStopsCleanly()
    {
        var (session, _) = await OpenAsync();
        var graph = new MapGraph();
        var map = graph.Maps.Values.Single(m => m.name == "1-1");
        session.Api.User.UserData.MapId = map.id; // already on the target map
        using var travel = new MapTravelModule(session.Api);
        var collect = new CollectBotModule(travel, map, new[] { "BONUS_BOX" });
        var statuses = new List<string>();
        session.Api.Runner.StatusChanged += (_, s) => { lock (statuses) statuses.Add(s); };

        Assert.True(await session.Api.Runner.StartAsync(collect));
        await FakeGameServer.WaitUntil(() => { lock (statuses) return statuses.Count > 0; });
        Assert.Equal("Collect: 1-1\nBuscando cajas seleccionadas", statuses[0]);
        collect.UpdateTypes(new[] { "BONUS_BOX", "CARGO" }); // live update while running

        await session.Api.Runner.StopAsync(TimeSpan.FromSeconds(5));
        Assert.True(session.Api.Runner.Completion.IsCompleted);
        Assert.False(travel.Running);
    }
}
