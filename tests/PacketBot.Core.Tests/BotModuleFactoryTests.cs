using FunnyProject;
using FunnyProject.Logic;
using FunnyProject.Modules;
using FunnyProject.Sessions;
using Xunit;

namespace PacketBot.Core.Tests;

// The same settings build the same module on Windows, the server and the web.
public class BotModuleFactoryTests : IDisposable
{
    private readonly FakeGameServer server = new();
    private readonly InstanceManager manager;
    private MapTravelModule? travel;

    public BotModuleFactoryTests()
    {
        var web = new WordGalaxyWebClient(cookies => new FakeWordGalaxyHttp(cookies,
            (_, _) => FakeWordGalaxyHttp.Html(FakeWordGalaxyHttp.MapHtml(host: "127.0.0.1"))));
        manager = new InstanceManager(web, server.Port);
    }

    public void Dispose()
    {
        travel?.Dispose();
        manager.Dispose();
        server.Dispose();
    }

    private async Task<MapTravelModule> TravelAsync()
    {
        var session = await manager.OpenAsync("test-php-session-0001");
        server.NextConnection();
        return travel = new MapTravelModule(session.Api);
    }

    [Theory]
    [InlineData("collect", "Collect")]
    [InlineData("NPC", "NPC Killer")]
    [InlineData("gg", "GGs")]
    [InlineData("Collect", "Collect")]
    [InlineData("Otro", "Otro")]
    public void ModuleNamesAreNormalized(string input, string expected) =>
        Assert.Equal(expected, BotModuleFactory.Normalize(input));

    [Fact]
    public async Task ModulesAreBuiltFromTheSettings()
    {
        var t = await TravelAsync();
        var settings = new BotSettings
        {
            CollectMapId = 1,
            CollectTypesByMap = new() { [1] = new() { "BONUS_BOX" } },
            NpcKillerMapId = 91,
            NpcKillerLaser = "mcb-25",
            GalaxyGateName = "Beta",
            Spins = 10,
            Laser = "lcb-10",
            AttackMode = "laser",
            GgFinalNpcFormation = "turtle"
        };
        var gg = new FakeGalaxyGateApi();

        var collect = BotModuleFactory.Create("collect", settings, t, () => gg, "php");
        Assert.Equal(1, Assert.IsType<CollectBotModule>(collect.Module).Map.id);
        Assert.Null(collect.Warning);

        var npc = BotModuleFactory.Create("NPC Killer", settings, t, () => gg, "php");
        var killer = Assert.IsType<NpcKillerBotModule>(npc.Module);
        Assert.Equal(91, killer.Map.id);
        Assert.Equal("mcb-25", killer.Ammunition);
        Assert.Contains("no hay NPCs objetivo para 5-1", npc.Warning);

        var gates = Assert.IsType<GalaxyGatesBotModule>(BotModuleFactory.Create("gg", settings, t, () => gg, "php", 3).Module);
        Assert.Equal(new GalaxyGateOptions(3, "Beta", 10, "lcb-10", "laser", "turtle"), gates.Options);
    }

    [Fact]
    public async Task MissingSelectionsAreReported()
    {
        var t = await TravelAsync();
        var empty = new BotSettings();
        Assert.Contains("Selecciona el mapa antes de iniciar Collect",
            BotModuleFactory.Create("Collect", empty, t, () => new FakeGalaxyGateApi(), "php").Error);
        Assert.Contains("Selecciona el mapa antes de iniciar NPC Killer",
            BotModuleFactory.Create("NPC Killer", empty, t, () => new FakeGalaxyGateApi(), "php").Error);
        Assert.Contains("Selecciona una GG",
            BotModuleFactory.Create("GGs", empty, t, () => new FakeGalaxyGateApi(), "php").Error);
        Assert.Equal("PHPSESSID no disponible.",
            BotModuleFactory.Create("GGs", new BotSettings { GalaxyGateName = "Beta" }, t, () => new FakeGalaxyGateApi(), "").Error);
        Assert.StartsWith("Modulo desconocido: Otro.",
            BotModuleFactory.Create("Otro", empty, t, () => new FakeGalaxyGateApi(), "php").Error);
    }
}
