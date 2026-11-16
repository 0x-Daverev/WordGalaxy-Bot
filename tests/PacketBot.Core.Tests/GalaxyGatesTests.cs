using System.Net.Http;
using FunnyProject.Logic;
using FunnyProject.Modules;
using FunnyProject.Sessions;
using Xunit;

namespace PacketBot.Core.Tests;

public class GalaxyGatesTests : IDisposable
{
    private readonly FakeGameServer server = new();
    private readonly InstanceManager manager;
    private readonly FakeGalaxyGateApi api = new();
    private readonly List<string> statuses = new();

    public GalaxyGatesTests()
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

    // MMO pilot on its home map (1 = 1-1).
    private async Task<(WordGalaxySession Session, MapTravelModule Travel)> OpenAsync(int mapId = 1)
    {
        var session = await manager.OpenAsync("test-php-session-0001");
        server.NextConnection();
        var user = session.Api.User;
        user.UserData.FactionId = 1;
        user.UserData.MapId = mapId;
        user.ShipInfo.Hp = user.ShipInfo.MaxHp = 10_000;
        session.Api.Runner.StatusChanged += (_, s) => { lock (statuses) statuses.Add(s); };
        return (session, new MapTravelModule(session.Api));
    }

    private Task WaitForStatus(string text, int timeoutMs = 5_000) =>
        FakeGameServer.WaitUntil(() => { lock (statuses) return statuses.Any(s => s.Contains(text)); }, timeoutMs);

    [Fact]
    public async Task SpinsUntilAllPartsThenPreparesAndGoesToTheGatePortal()
    {
        var (session, travel) = await OpenAsync();
        using var _ = travel;
        var gg = new GalaxyGatesBotModule(travel, api, new GalaxyGateOptions(3, "Beta", 10));
        int refreshes = 0;
        gg.GatesChanged += () => Interlocked.Increment(ref refreshes);

        Assert.True(await session.Api.Runner.StartAsync(gg));
        await WaitForStatus("Buscando Beta (tipo 3)");
        Assert.Equal(new[] { "spin 3 x10", "spin 3 x10", "prepare 3" }, api.Writes());
        lock (statuses)
        {
            Assert.Contains("Girando x10", statuses);
            Assert.Contains("Preparando GG...", statuses);
            Assert.Contains("GG preparada. Iniciando viaje a X-1...", statuses);
            Assert.Contains("En 1-1. Portal GG pendiente de identificar.", statuses);
        }
        Assert.True(refreshes >= 2);

        await session.Api.Runner.StopAsync(TimeSpan.FromSeconds(5));
        Assert.True(session.Api.Runner.Completion.IsCompleted);
        Assert.False(travel.Running);
    }

    [Fact]
    public async Task PrepareThatTimesOutButWasAppliedIsNotSentAgain()
    {
        api.Gate.partsOwned = api.Gate.maxParts;
        api.PrepareThrowsTimeout = true;
        var (session, travel) = await OpenAsync();
        using var _ = travel;
        Assert.True(await session.Api.Runner.StartAsync(new GalaxyGatesBotModule(travel, api, new GalaxyGateOptions(3, "Beta", 10))));
        await WaitForStatus("GG preparada. Iniciando viaje a X-1...");
        Assert.Equal(new[] { "prepare 3" }, api.Writes());
        await session.Api.Runner.StopAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RejectedPrepareWaitsBeforeTryingAgain()
    {
        api.Gate.partsOwned = api.Gate.maxParts;
        api.PrepareAppliedOnServer = false;
        var (session, travel) = await OpenAsync();
        using var _ = travel;
        Assert.True(await session.Api.Runner.StartAsync(new GalaxyGatesBotModule(travel, api, new GalaxyGateOptions(3, "Beta", 10))));
        await WaitForStatus("Preparacion pendiente/rechazada. Reintentando en 15 s...");
        await Task.Delay(1_000);
        Assert.Equal(new[] { "prepare 3" }, api.Writes()); // no blind re-post
        await session.Api.Runner.StopAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task NotEnoughUridiumIsReportedWithoutSpinning()
    {
        api.Uridium = 500; // 10 spins x 100 needed
        var (session, travel) = await OpenAsync();
        using var _ = travel;
        Assert.True(await session.Api.Runner.StartAsync(new GalaxyGatesBotModule(travel, api, new GalaxyGateOptions(3, "Beta", 10))));
        await WaitForStatus("GG: Uridium insuficiente para los spins configurados.");
        Assert.Empty(api.Writes());
        Assert.True(session.Api.Runner.IsRunning); // recoverable: retried every 5 s
        await session.Api.Runner.StopAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task GateCanBeGivenByNameOnly()
    {
        var (session, travel) = await OpenAsync();
        using var _ = travel;
        var gg = new GalaxyGatesBotModule(travel, api, new GalaxyGateOptions(0, "beta", 5));
        Assert.True(await session.Api.Runner.StartAsync(gg));
        await FakeGameServer.WaitUntil(() => api.Writes().Length > 0);
        Assert.Equal("spin 3 x5", api.Writes()[0]);
        Assert.Equal((3, "Beta"), (gg.Options.GateId, gg.Options.GateName));
        await session.Api.Runner.StopAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task InsideAGalaxyGateTheCombatResumesWithoutTouchingTheApi()
    {
        var (session, travel) = await OpenAsync(mapId: 52);
        using var _ = travel;
        Assert.True(await session.Api.Runner.StartAsync(new GalaxyGatesBotModule(travel, api, new GalaxyGateOptions(3, "Beta", 10))));
        await WaitForStatus("Ya dentro de GG. Reanudando combate...");
        await WaitForStatus("GG Beta: combate activo.");
        Assert.Empty(api.Writes());
        await session.Api.Runner.StopAsync(TimeSpan.FromSeconds(5));
        Assert.True(session.Api.Runner.Completion.IsCompleted);
    }

    [Fact]
    public async Task InvalidOptionsAreRejectedOrNormalised()
    {
        var (_, travel) = await OpenAsync();
        using var _ = travel;
        Assert.Throws<ArgumentException>(() => new GalaxyGatesBotModule(travel, api, new GalaxyGateOptions(3, "Beta", 7)));
        Assert.Throws<ArgumentException>(() => new GalaxyGatesBotModule(travel, api, new GalaxyGateOptions(0, " ", 10)));
        var gg = new GalaxyGatesBotModule(travel, api, new GalaxyGateOptions(3, "Beta", 10, "rockets", "both-ways"));
        Assert.Equal(("ucb-100", "both"), (gg.Options.Ammunition, gg.Options.AttackMode));
    }
}
