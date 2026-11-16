using System.Text;
using FunnyProject;
using FunnyProject.Commands.Write;
using FunnyProject.Logic;
using FunnyProject.Runtime;
using FunnyProject.Services;
using FunnyProject.Sessions;
using Xunit;

namespace PacketBot.Core.Tests;

// Formations and boosters, shared by the Windows timers and the server loops.
public class BotServicesTests : IDisposable
{
    private readonly FakeGameServer server = new();
    private readonly InstanceManager manager;
    private readonly MemorySettings settings = new();

    public BotServicesTests()
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

    internal sealed class MemorySettings : IBotSettingsAccess
    {
        public BotSettings Value = new();
        public int Updates;
        public BotSettings Read() => Value;
        public void Update(Action<BotSettings> change)
        {
            change(Value);
            Updates++;
        }
    }

    // Runs until stopped; can pretend to have sent the GG final formation.
    private sealed class HoldModule : IBotModule, IFinalFormationSource
    {
        public string Name => "Hold";
        public bool FinalFormationActive { get; set; }
        public string FinalFormation { get; set; } = "";
        public Task RunAsync(BotModuleContext context, CancellationToken token) =>
            Task.Delay(Timeout.Infinite, token);
    }

    private sealed class FakePurchase : IBoosterPurchaseApi
    {
        private readonly Func<int, string> buy;
        public FakePurchase(Func<int, string> buy) => this.buy = buy;
        public Task<string> BuyOneAsync(int itemId, CancellationToken token) => Task.FromResult(buy(itemId));
        public void Dispose() { }
    }

    private async Task<(Api Api, FakeGameServer.Connection Connection)> OpenAsync()
    {
        var session = await manager.OpenAsync("test-php-session-0001");
        return (session.Api, server.NextConnection());
    }

    private static List<(short Id, byte[] Frame)> Frames(FakeGameServer.Connection connection)
    {
        byte[] bytes = connection.Snapshot();
        var frames = new List<(short, byte[])>();
        for (int offset = 0; offset + 4 <= bytes.Length;)
        {
            int length = (bytes[offset] << 8) | bytes[offset + 1];
            if (offset + 2 + length > bytes.Length) break;
            frames.Add(((short)((bytes[offset + 2] << 8) | bytes[offset + 3]),
                bytes.AsSpan(offset, 2 + length).ToArray()));
            offset += 2 + length;
        }
        return frames;
    }

    // Action body: [u16 1][u16 1][writeUTF formation][u16 21922].
    private static List<string> FormationsSent(FakeGameServer.Connection connection) =>
        Frames(connection).Where(f => f.Id == FunnyProject.Commands.Write.Action.ID).Select(f =>
        {
            int length = (f.Frame[8] << 8) | f.Frame[9];
            return Encoding.UTF8.GetString(f.Frame, 10, length);
        }).ToList();

    private static int Count(FakeGameServer.Connection connection, short id) =>
        Frames(connection).Count(f => f.Id == id);

    private static async Task<List<string>> SettleFormations(FakeGameServer.Connection connection)
    {
        await Task.Delay(150); // let the socket deliver what was sent
        return FormationsSent(connection);
    }

    [Fact]
    public async Task FormationFollowsTheActionOnlyOnChangeAndOnlyWhileRunning()
    {
        var (api, connection) = await OpenAsync();
        api.User.UpdateAvailableDroneFormations(new[] { "drill", "turtle", "arrow" });
        settings.Value.RunFormation = "arrow";
        settings.Value.AttackFormation = "drill";
        var formations = new FormationService(api, settings);

        api.User.ActionState.Current = BotAction.Run;
        formations.Tick();
        Assert.Empty(await SettleFormations(connection)); // bot stopped

        Assert.True(await api.Runner.StartAsync(new HoldModule()));
        formations.Tick(); // Idle: nothing
        api.User.ActionState.Current = BotAction.Run;
        formations.Tick();
        formations.Tick();
        api.User.ActionState.Current = BotAction.Attack;
        formations.Tick();
        formations.Tick();
        api.User.ActionState.Current = BotAction.Idle;
        formations.Tick(); // Idle keeps the last formation
        api.User.ActionState.Current = BotAction.Attack;
        formations.Tick();
        await FakeGameServer.WaitUntil(() => FormationsSent(connection).Count >= 2);
        Assert.Equal(new[] { "arrow", "drill" }, await SettleFormations(connection));

        formations.Reset(); // e.g. settings changed
        formations.Tick();
        await FakeGameServer.WaitUntil(() => FormationsSent(connection).Count >= 3);

        settings.Value.RunFormation = "missing"; // not available on the ship
        api.User.ActionState.Current = BotAction.Run;
        formations.Tick();
        Assert.Equal(new[] { "arrow", "drill", "drill" }, await SettleFormations(connection));
        api.Runner.Stop();
    }

    [Fact]
    public async Task GalaxyGateFinalFormationIsNotSentTwiceNorOverridden()
    {
        var (api, connection) = await OpenAsync();
        api.User.UpdateAvailableDroneFormations(new[] { "drill", "turtle", "arrow" });
        settings.Value.AttackFormation = "drill";
        var formations = new FormationService(api, settings);
        var module = new HoldModule { FinalFormation = "turtle" };
        Assert.True(await api.Runner.StartAsync(module));

        api.User.ActionState.Current = BotAction.Attack;
        formations.Tick();
        await FakeGameServer.WaitUntil(() => FormationsSent(connection).Count >= 1);

        module.FinalFormationActive = true; // the GG module sent "turtle" itself
        formations.Tick();
        formations.Tick();
        api.User.ActionState.Current = BotAction.Run;
        formations.Tick();
        Assert.Equal(new[] { "drill" }, await SettleFormations(connection));
        api.Runner.Stop();
    }

    [Fact]
    public async Task BoostersAreOpenedOncePerTcpAndRequestedEveryMinute()
    {
        var (api, connection) = await OpenAsync();
        var polling = new BoosterPollingService(api);
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        polling.Tick(t0); // not ready until the game finished the TCP init
        api.User.BoosterRequestsReady = true;
        polling.Tick(t0);
        polling.Tick(t0.AddSeconds(30));
        polling.Tick(t0.AddSeconds(60));
        await FakeGameServer.WaitUntil(() => Count(connection, RequestBoosters.ID) >= 2);
        Assert.Equal(1, Count(connection, OpenBoosters.ID));

        api.User.BoosterRequestsReady = false; // TCP re-initialized
        polling.Tick(t0.AddSeconds(61));
        api.User.BoosterRequestsReady = true;
        polling.Tick(t0.AddSeconds(62));
        await FakeGameServer.WaitUntil(() => Count(connection, OpenBoosters.ID) >= 2);
        await Task.Delay(150);
        Assert.Equal(3, Count(connection, RequestBoosters.ID));
    }

    private async Task<Api> ReadyForPurchaseAsync(long remainingSeconds = 3_600, bool snapshot = true)
    {
        var (api, _) = await OpenAsync();
        api.User.BoosterRequestsReady = true;
        if (snapshot)
            Assert.True(api.User.Boosters.TryReceive(
                BoosterStateTests.Message(damageBooster: "DMG_B01", remaining: remainingSeconds), RequestBoosters.ID));
        return api;
    }

    [Fact]
    public async Task NothingIsBoughtWithoutOptIn()
    {
        var api = await ReadyForPurchaseAsync();
        var bought = new List<int>();
        var service = new BoosterAutoBuyService(api, settings, _ => new FakePurchase(id => { bought.Add(id); return "ok"; }));
        await service.CheckAsync(CancellationToken.None);
        settings.Value.AutoBuyBoosters[2051] = false;
        await service.CheckAsync(CancellationToken.None);
        Assert.Empty(bought);
        Assert.Equal(0, settings.Updates);
        Assert.Equal("Compras automaticas: desactivadas", service.Status);
    }

    [Fact]
    public async Task OptedInBoosterIsBoughtOnceAndTheAttemptIsSavedBeforeThePost()
    {
        var api = await ReadyForPurchaseAsync();
        settings.Value.AutoBuyBoosters[2051] = true;
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var bought = new List<int>();
        var service = new BoosterAutoBuyService(api, settings, cookie =>
        {
            Assert.Equal("test-php-session-0001", cookie);
            return new FakePurchase(id =>
            {
                // Persisted before the request is made.
                Assert.Equal(now, settings.Value.BoosterPurchaseAttemptUtc[id]);
                bought.Add(id);
                return "Compra realizada";
            });
        }, () => now);

        await service.CheckAsync(CancellationToken.None);
        Assert.Equal(new[] { 2051 }, bought);
        Assert.Equal("DMG_B01: Compra realizada", service.Status);

        // Still below 6 h right after renewing: no repeat for 6 h - 5 min.
        await service.CheckAsync(CancellationToken.None);
        now = now.AddHours(5).AddMinutes(54);
        await service.CheckAsync(CancellationToken.None);
        Assert.Single(bought);

        now = now.AddMinutes(2);
        await service.CheckAsync(CancellationToken.None);
        Assert.Equal(new[] { 2051, 2051 }, bought);
    }

    [Fact]
    public async Task FailedPurchaseIsNotRetriedImmediately()
    {
        var api = await ReadyForPurchaseAsync();
        settings.Value.AutoBuyBoosters[2051] = true;
        int calls = 0;
        var service = new BoosterAutoBuyService(api, settings, _ => new FakePurchase(_ =>
        {
            calls++;
            throw new HttpRequestException("timeout");
        }));
        await service.CheckAsync(CancellationToken.None);
        await service.CheckAsync(CancellationToken.None);
        Assert.Equal(1, calls);
        Assert.Equal("DMG_B01: timeout", service.Status);
        Assert.True(settings.Value.BoosterPurchaseAttemptUtc.ContainsKey(2051));
    }

    [Theory]
    [InlineData(21_600L, true)]  // 6 h left: not yet
    [InlineData(3_600L, false)]  // no snapshot: never guess
    public async Task NoPurchaseWithEnoughTimeLeftOrWithoutSnapshot(long remaining, bool snapshot)
    {
        var api = await ReadyForPurchaseAsync(remaining, snapshot);
        settings.Value.AutoBuyBoosters[2051] = true;
        settings.Value.AutoBuyBoosters[2052] = true; // opted in, but not in the snapshot
        int calls = 0;
        var service = new BoosterAutoBuyService(api, settings, _ => new FakePurchase(_ => { calls++; return "ok"; }));
        await service.CheckAsync(CancellationToken.None);
        Assert.Equal(0, calls);
        Assert.Equal(0, settings.Updates);
    }

    // ---- Energy Leech (NPC Killer) ----

    private sealed class NamedHold : IBotModule
    {
        public NamedHold(string name) => Name = name;
        public string Name { get; }
        public Task RunAsync(BotModuleContext context, CancellationToken token) =>
            Task.Delay(Timeout.Infinite, token);
    }

    private static List<string> ActionsSent(FakeGameServer.Connection connection) => FormationsSent(connection);

    [Fact]
    public async Task EnergyLeechIsBoughtOncePerCycleOnlyWithNpcKiller()
    {
        var (api, connection) = await OpenAsync();
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var leech = new EnergyLeechService(api, settings, () => now);
        api.User.BoosterRequestsReady = true;

        settings.Value.NpcKillerEnergyLeech = true;
        leech.Tick(); // bot stopped
        Assert.True(await api.Runner.StartAsync(new NamedHold("Collect")));
        leech.Tick(); // another module
        api.Runner.Stop();
        await FakeGameServer.WaitUntil(() => !api.Runner.IsActive);
        Assert.Empty(await SettleFormations(connection));

        Assert.True(await api.Runner.StartAsync(new NamedHold("NPC Killer")));
        leech.Tick();
        await FakeGameServer.WaitUntil(() => ActionsSent(connection).Count >= 1);
        Assert.Equal(now, settings.Value.EnergyLeechAttemptUtc);

        now = now.AddMinutes(10);
        leech.Tick(); // active: no second purchase
        now = now.AddMinutes(24);
        leech.Tick(); // 34 min: still in cooldown
        Assert.Equal(new[] { EnergyLeechService.ItemId }, await SettleFormations(connection));

        now = now.AddMinutes(1);
        leech.Tick(); // 35 min after the first purchase
        await FakeGameServer.WaitUntil(() => ActionsSent(connection).Count >= 2);

        settings.Value.NpcKillerEnergyLeech = false;
        now = now.AddHours(2);
        leech.Tick();
        Assert.Equal(2, (await SettleFormations(connection)).Count);
        api.Runner.Stop();
    }

    [Fact]
    public async Task EnergyLeechFollowsTheServerTimer()
    {
        var (api, connection) = await OpenAsync();
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var leech = new EnergyLeechService(api, settings, () => now);
        api.User.BoosterRequestsReady = true;
        settings.Value.NpcKillerEnergyLeech = true;
        // Restart while the leech is active: the server says 12 min remain.
        settings.Value.EnergyLeechAttemptUtc = now.AddMinutes(-18);
        api.User.ItemTimers.Update(EnergyLeechService.ItemId, 1, 12 * 60_000, 30 * 60_000, false, now);
        Assert.True(await api.Runner.StartAsync(new NamedHold("NPC Killer")));

        now = now.AddMinutes(16);
        leech.Tick(); // 12 min active + 5 min cooldown not over yet
        Assert.Empty(await SettleFormations(connection));

        // Server: ready again earlier than estimated.
        api.User.ItemTimers.Update(EnergyLeechService.ItemId, 0, 0, 0, true, now);
        leech.Tick();
        await FakeGameServer.WaitUntil(() => ActionsSent(connection).Count >= 1);
        api.Runner.Stop();
    }

    [Fact]
    public void EnergyLeechReadyTime()
    {
        var t = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        FunnyProject.UserCollections.ItemTimer Timer(int state, double ms, bool activatable, DateTime at) =>
            new(EnergyLeechService.ItemId, (FunnyProject.UserCollections.ItemTimerState)state, ms, 0, activatable, at);

        Assert.Equal(DateTime.MinValue, EnergyLeechService.NextReady(null, null).ReadyUtc);
        Assert.Equal(t.AddMinutes(35), EnergyLeechService.NextReady(t, null).ReadyUtc);
        Assert.Equal(t.AddMinutes(35), EnergyLeechService.NextReady(t, Timer(0, 0, true, t.AddMinutes(-1))).ReadyUtc);
        Assert.Equal(t.AddMinutes(30 + 5), EnergyLeechService.NextReady(t, Timer(1, 30 * 60_000, false, t)).ReadyUtc);
        Assert.Equal(t.AddMinutes(3), EnergyLeechService.NextReady(t, Timer(2, 3 * 60_000, false, t)).ReadyUtc);
        Assert.Equal(t.AddMinutes(2), EnergyLeechService.NextReady(t, Timer(0, 0, true, t.AddMinutes(2))).ReadyUtc);
        Assert.Null(EnergyLeechService.NextReady(t, Timer(0, 0, false, t.AddMinutes(2))).ReadyUtc);
    }
}
