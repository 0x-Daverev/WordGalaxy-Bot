using FunnyProject;
using FunnyProject.Commands.Write;
using FunnyProject.Logic;
using FunnyProject.Modules;
using FunnyProject.Navigation;
using FunnyProject.Sessions;
using FunnyProject.UserCollections.Objects;
using Xunit;

namespace PacketBot.Core.Tests;

// NPC Killer and the map-bound loop without WinForms, against the loopback
// fake game server. NPCs are injected into the game state directly.
public class NpcKillerTests : IDisposable
{
    private readonly FakeGameServer server = new();
    private readonly InstanceManager manager;
    private readonly MapGraph graph = new();

    public NpcKillerTests()
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

    private MapGraph.Map MapNamed(string name) => graph.Maps.Values.Single(m => m.name == name);

    private async Task<(WordGalaxySession, FakeGameServer.Connection)> OpenOnMapAsync(MapGraph.Map map)
    {
        var session = await manager.OpenAsync("test-php-session-0001");
        var connection = server.NextConnection();
        var user = session.Api.User;
        user.UserData.MapId = map.id;
        user.ShipInfo.Hp = user.ShipInfo.MaxHp = 10_000;
        user.Position.X = 1_000;
        user.Position.Y = 1_000;
        user.Position.Speed = 300;
        return (session, connection);
    }

    private static List<short> SentIds(FakeGameServer.Connection connection)
    {
        byte[] bytes = connection.Snapshot();
        var ids = new List<short>();
        for (int offset = 0; offset + 4 <= bytes.Length;)
        {
            int length = (bytes[offset] << 8) | bytes[offset + 1];
            ids.Add((short)((bytes[offset + 2] << 8) | bytes[offset + 3]));
            offset += 2 + length;
        }
        return ids;
    }

    private static Player Npc(int id, string name, int x, int y) =>
        new() { ID = id, Name = name, X = x, Y = y, IsNpc = true, HP = 1000, MaxHP = 1000 };

    [Fact]
    public async Task AttacksOnlySelectedNpcsAndStopsCleanly()
    {
        var map = MapNamed("1-1");
        var (session, connection) = await OpenOnMapAsync(map);
        session.Api.User.Players.Add(Npc(501, "-=[ Lordakia ]=-", 1_500, 1_500), true);
        using var travel = new MapTravelModule(session.Api);
        var killer = new NpcKillerBotModule(travel, map, new[] { "-=[ Streuner ]=-" });

        Assert.True(await session.Api.Runner.StartAsync(killer));
        await Task.Delay(800);
        Assert.DoesNotContain(AttackCommand.ID, SentIds(connection)); // not a selected NPC
        Assert.Equal(BotAction.Run, session.Api.User.ActionState.Current); // roaming

        session.Api.User.Players.Add(Npc(502, "-=[ Streuner ]=-", 1_600, 1_600), true);
        await FakeGameServer.WaitUntil(() => SentIds(connection).Contains(AttackCommand.ID));
        Assert.Contains(SelectShip.ID, SentIds(connection));
        Assert.Equal(502, session.Api.User.Target?.ID);
        Assert.Equal(BotAction.Attack, session.Api.User.ActionState.Current);

        // Live change of targets while running.
        killer.UpdateTargets(Array.Empty<string>());
        await FakeGameServer.WaitUntil(() => session.Api.User.ActionState.Current == BotAction.Run);

        await session.Api.Runner.StopAsync(TimeSpan.FromSeconds(5));
        Assert.True(session.Api.Runner.Completion.IsCompleted);
        Assert.Equal(BotAction.Idle, session.Api.User.ActionState.Current);
        Assert.Null(session.Api.User.Target);
        int sent = SentIds(connection).Count;
        await Task.Delay(500);
        Assert.Equal(sent, SentIds(connection).Count); // nothing sent after the stop
    }

    [Fact]
    public async Task UsesTheConfiguredAmmunitionAndLiveChanges()
    {
        var map = MapNamed("1-1");
        var (session, connection) = await OpenOnMapAsync(map);
        session.Api.User.Players.Add(Npc(510, "-=[ Streuner ]=-", 1_500, 1_500), true);
        using var travel = new MapTravelModule(session.Api);
        var killer = new NpcKillerBotModule(travel, map, new[] { "-=[ Streuner ]=-" }, "mcb-50");
        Assert.True(await session.Api.Runner.StartAsync(killer));
        string Sent() => System.Text.Encoding.ASCII.GetString(connection.Snapshot());
        await FakeGameServer.WaitUntil(() => Sent().Contains("ammunition_laser_mcb-50"));
        Assert.DoesNotContain("ammunition_laser_ucb-100", Sent());

        killer.UpdateAmmunition("lcb-10");
        // The laser ammunition is re-sent with each attack (every 3 s).
        await FakeGameServer.WaitUntil(() => Sent().Contains("ammunition_laser_lcb-10"), 8_000);
        killer.UpdateAmmunition("invalid"); // ignored
        Assert.Equal("lcb-10", killer.Ammunition);
        await session.Api.Runner.StopAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task InvalidAmmunitionFallsBackToUcb100()
    {
        var (session, _) = await OpenOnMapAsync(MapNamed("1-1"));
        using var travel = new MapTravelModule(session.Api);
        var killer = new NpcKillerBotModule(travel, MapNamed("1-1"), Array.Empty<string>(), "rockets");
        Assert.Equal("ucb-100", killer.Ammunition);
    }

    [Fact]
    public async Task WaitsWhileTcpIsDownAndResumesAfterReconnect()
    {
        var map = MapNamed("1-1");
        var (session, first) = await OpenOnMapAsync(map);
        using var travel = new MapTravelModule(session.Api);
        var killer = new NpcKillerBotModule(travel, map, new[] { "-=[ Streuner ]=-" });
        Assert.True(await session.Api.Runner.StartAsync(killer));

        first.Client.Close(); // bot active: automatic reconnect
        var second = server.NextConnection(timeoutMs: 10_000);
        await FakeGameServer.WaitUntil(() => session.PacketManager.Connected);
        session.Api.User.Players.Add(Npc(503, "-=[ Streuner ]=-", 1_400, 1_400), true);
        await FakeGameServer.WaitUntil(() => SentIds(second).Contains(AttackCommand.ID));
        Assert.True(session.Api.Runner.IsRunning);
    }

    [Theory]
    [InlineData("collect")]
    [InlineData("npc")]
    public async Task LeavingTheTargetMapTravelsBack(string kind)
    {
        var map = MapNamed("1-1");
        var (session, _) = await OpenOnMapAsync(map);
        using var travel = new MapTravelModule(session.Api);
        MapBoundModule module = kind == "collect"
            ? new CollectBotModule(travel, map, new[] { "BONUS_BOX" })
            : new NpcKillerBotModule(travel, map, new[] { "-=[ Streuner ]=-" });
        module.OffMapGrace = TimeSpan.FromMilliseconds(300);
        var statuses = new List<string>();
        session.Api.Runner.StatusChanged += (_, s) => { lock (statuses) statuses.Add(s); };
        Assert.True(await session.Api.Runner.StartAsync(module));
        await FakeGameServer.WaitUntil(() => { lock (statuses) return statuses.Count > 0; });

        // e.g. respawn at base after a death. (Without a real hero init the
        // travel module may stop itself at once, so check the module status.)
        session.Api.User.UserData.MapId = MapNamed("1-2").id;
        await FakeGameServer.WaitUntil(() => { lock (statuses) return statuses.Any(s => s.Contains("viajando a 1-1")); });
        Assert.True(session.Api.Runner.IsRunning);

        // Back on the map: the inner logic runs again.
        int before;
        lock (statuses) before = statuses.Count;
        session.Api.User.UserData.MapId = map.id;
        await FakeGameServer.WaitUntil(() =>
        {
            lock (statuses) return statuses.Skip(before).Any(s => s.StartsWith(module.Name + ": 1-1\n"));
        });
        await session.Api.Runner.StopAsync(TimeSpan.FromSeconds(5));
        Assert.False(travel.Running);
    }

    [Fact]
    public async Task ShortVisitToAnotherMapDoesNotRestartTheModule()
    {
        var map = MapNamed("1-1");
        var (session, _) = await OpenOnMapAsync(map);
        using var travel = new MapTravelModule(session.Api);
        var killer = new NpcKillerBotModule(travel, map, new[] { "-=[ Streuner ]=-" })
        {
            OffMapGrace = TimeSpan.FromSeconds(3)
        };
        Assert.True(await session.Api.Runner.StartAsync(killer));
        await Task.Delay(300);
        session.Api.User.UserData.MapId = MapNamed("1-2").id;
        await Task.Delay(500);
        session.Api.User.UserData.MapId = map.id;
        await Task.Delay(3_000);
        Assert.False(travel.Running);
    }

    [Fact]
    public async Task OrbitUsesTheWholeAreaOfLargeMaps()
    {
        var large = graph.Maps.Values.First(m => m.width == 42000);
        var (session, _) = await OpenOnMapAsync(large);
        session.Api.User.Position.X = 30_000;
        session.Api.User.Position.Y = 20_000;
        var combat = new NpcOrbitCombat(session.Api) { MaxX = large.width - 500, MaxY = large.height - 500 };
        combat.Tick(new[] { Npc(600, "-=[ Streuner ]=-", 30_500, 20_500) });
        // Before, every move was clamped to 20500 x 12500 (standard map size).
        Assert.True(session.Api.User.Position.TargetX > 20_500);
        Assert.True(session.Api.User.Position.TargetY > 12_500);

        var standard = new NpcOrbitCombat(session.Api); // defaults unchanged (GG)
        Assert.Equal((20_500, 12_500), (standard.MaxX, standard.MaxY));
    }

    [Fact]
    public async Task KeepsFightingTheSelectedNpcUntilItIsGone()
    {
        var (session, _) = await OpenOnMapAsync(MapNamed("1-1"));
        var combat = new NpcOrbitCombat(session.Api) { KeepTarget = true };
        var first = Npc(701, "-=[ Streuner ]=-", 1_700, 1_000);
        var closer = Npc(702, "-=[ Streuner ]=-", 1_100, 1_000);

        Assert.Equal(701, combat.Tick(new[] { first })!.Value.TargetId);
        Assert.Equal(701, combat.Tick(new[] { closer, first })!.Value.TargetId); // no switch
        Assert.Equal(701, combat.SelectedNpc);
        Assert.Equal(702, combat.Tick(new[] { closer })!.Value.TargetId); // first one died

        var nearest = new NpcOrbitCombat(session.Api); // GG: nearest first, as before
        nearest.Tick(new[] { first });
        Assert.Equal(702, nearest.Tick(new[] { closer, first })!.Value.TargetId);
    }

    [Fact]
    public void NewTargetsComeFromThePreferredZones()
    {
        var zone = new[] { new PreferredZone { X = 5000, Y = 5000, Width = 3000, Height = 3000 } };
        var outside = Npc(801, "a", 1_500, 1_500);
        var inside = Npc(802, "a", 6_000, 6_000);

        // Without zones: whatever is visible.
        Assert.Equal(NpcKillerModule.Step.Fight, NpcKillerModule.Plan(new[] { outside }, 0, Array.Empty<PreferredZone>(), 1_000, 1_000).Step);
        // Ship outside the zones, only NPCs outside: go back, do not attack.
        var plan = NpcKillerModule.Plan(new[] { outside }, 0, zone, 1_000, 1_000);
        Assert.Equal(NpcKillerModule.Step.ReturnToZone, plan.Step);
        Assert.Empty(plan.Targets);
        // In the zone with nothing inside it: search there.
        Assert.Equal(NpcKillerModule.Step.Roam, NpcKillerModule.Plan(new[] { outside }, 0, zone, 6_500, 6_500).Step);
        // An NPC inside a zone is a valid new target from anywhere.
        plan = NpcKillerModule.Plan(new[] { outside, inside }, 0, zone, 1_000, 1_000);
        Assert.Equal((NpcKillerModule.Step.Fight, 802), (plan.Step, Assert.Single(plan.Targets).ID));
        // The NPC being fought is kept even if it is outside the zones.
        plan = NpcKillerModule.Plan(new[] { outside, inside }, 801, zone, 1_000, 1_000);
        Assert.Equal(NpcKillerModule.Step.Fight, plan.Step);
        Assert.Contains(plan.Targets, p => p.ID == 801);
    }

    [Fact]
    public async Task AfterAKillOutsideTheZoneItFliesBackInsteadOfAttackingNearby()
    {
        var map = MapNamed("1-1");
        var (session, connection) = await OpenOnMapAsync(map);
        using var travel = new MapTravelModule(session.Api);
        var zone = new PreferredZone { X = 8_000, Y = 6_000, Width = 3_000, Height = 3_000 };
        var killer = new NpcKillerBotModule(travel, map, new[] { "-=[ Streuner ]=-" }, preferredZones: new[] { zone });
        // Only an NPC next to the ship, outside the zone.
        session.Api.User.Players.Add(Npc(901, "-=[ Streuner ]=-", 1_400, 1_400), true);

        Assert.True(await session.Api.Runner.StartAsync(killer));
        await FakeGameServer.WaitUntil(() => zone.Contains(session.Api.User.Position.TargetX, session.Api.User.Position.TargetY));
        Assert.DoesNotContain(AttackCommand.ID, SentIds(connection));
        session.Api.Runner.Stop();
        await FakeGameServer.WaitUntil(() => !session.Api.Runner.IsActive);
    }

    [Fact]
    public async Task ANpcThatRunsAwayIsDroppedForANearerOne()
    {
        var (session, _) = await OpenOnMapAsync(MapNamed("1-1"));
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var combat = new NpcOrbitCombat(session.Api) { KeepTarget = true, Clock = () => now };
        session.Api.User.Position.X = 1_000;
        session.Api.User.Position.Y = 1_000;
        var runner = Npc(711, "-=[ Streuner ]=-", 2_500, 1_000);
        var other = Npc(712, "-=[ Streuner ]=-", 1_000, 2_800);

        Assert.Equal(711, combat.Tick(new[] { runner })!.Value.TargetId);
        // It keeps getting away; the ship never gets 100 closer.
        foreach (int x in new[] { 2_600, 2_700, 2_800 })
        {
            now = now.AddSeconds(1.5);
            runner.X = x;
            session.Api.User.Position.X = 1_000;
            var tick = combat.Tick(new[] { runner, other });
            if (now - new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc) < NpcOrbitCombat.GiveUpAfter)
                Assert.Equal(711, tick!.Value.TargetId);
            else
                Assert.Null(tick); // dropped
        }
        Assert.True(combat.IsIgnored(711));
        Assert.Equal(712, combat.Tick(new[] { runner, other })!.Value.TargetId);

        now = now.Add(NpcOrbitCombat.IgnoreFor);
        Assert.False(combat.IsIgnored(711)); // a minute later it counts again
    }

    [Fact]
    public async Task AFarNpcIsKeptWhileTheShipGetsCloser()
    {
        var (session, _) = await OpenOnMapAsync(MapNamed("1-1"));
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var combat = new NpcOrbitCombat(session.Api) { KeepTarget = true, Clock = () => now };
        var far = Npc(721, "-=[ Streuner ]=-", 5_000, 1_000);
        for (int ship = 1_000; ship <= 3_000; ship += 250)
        {
            session.Api.User.Position.X = ship;
            session.Api.User.Position.Y = 1_000;
            Assert.Equal(721, combat.Tick(new[] { far })!.Value.TargetId);
            now = now.AddSeconds(2);
        }
        Assert.False(combat.IsIgnored(721));
    }

    // Packet 11468 (server ship selection) for this NPC id.
    private static byte[] SelectionConfirmed(int npcId)
    {
        var bytes = new List<byte> { 0x2C, 0xCC }; // 11468
        for (int word = 0; word < 8; word++)
        {
            // PlayerID = ror(word6, 9) -> word6 = rol(id, 9)
            uint v = word == 6 ? ((uint)npcId << 9) | ((uint)npcId >> 23) : 0;
            bytes.AddRange(new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v });
        }
        bytes.Add(0);
        return bytes.ToArray();
    }

    [Fact]
    public async Task SelectionIsRepeatedUntilTheServerConfirmsIt()
    {
        var (session, connection) = await OpenOnMapAsync(MapNamed("1-1"));
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var combat = new NpcOrbitCombat(session.Api) { KeepTarget = true, Clock = () => now };
        var npc = Npc(731, "-=[ Streuner ]=-", 1_500, 1_000);
        int Selections() => SentIds(connection).Count(id => id == SelectShip.ID);

        combat.Tick(new[] { npc });
        await FakeGameServer.WaitUntil(() => Selections() == 1);
        Assert.False(session.Api.User.TargetConfirmed);

        now = now.AddSeconds(1);
        combat.Tick(new[] { npc }); // too soon to retry
        now = now.Add(NpcOrbitCombat.SelectRetry);
        combat.Tick(new[] { npc }); // still not confirmed: select again
        await FakeGameServer.WaitUntil(() => Selections() == 2);

        var reader = new Krypton_Core.Packets.Bytes.EndianBinaryReader(
            Krypton_Core.Packets.Bytes.EndianBitConverter.Big, new MemoryStream(SelectionConfirmed(731)));
        session.Api.User.PacketManager!.Parse(reader);
        Assert.True(session.Api.User.TargetConfirmed);

        now = now.AddSeconds(10);
        combat.Tick(new[] { npc });
        await Task.Delay(300);
        Assert.Equal(2, Selections()); // confirmed: no more selections
    }
}
