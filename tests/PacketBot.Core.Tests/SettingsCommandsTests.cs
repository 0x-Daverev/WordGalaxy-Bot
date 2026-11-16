using FunnyProject;
using FunnyProject.Telegram;
using Xunit;

namespace PacketBot.Core.Tests;

public class SettingsCommandsTests
{
    private static readonly string[] Formations = { "drill", "turtle" };

    private static (BotSettings Settings, string Reply) Apply(string key, string value, BotSettings? start = null)
    {
        var settings = start ?? new BotSettings();
        var (change, reply) = SettingsCommands.Parse(key, value, Formations);
        Assert.NotNull(change);
        change!(settings);
        return (settings, reply);
    }

    [Theory]
    [InlineData("collect", "Collect")]
    [InlineData("NPC", "NPC Killer")]
    [InlineData("gg", "GGs")]
    public void ModuleNames(string value, string module) =>
        Assert.Equal(module, Apply("modulo", value).Settings.SelectedModule);

    [Fact]
    public void ValuesAreStoredAsTheBotExpectsThem()
    {
        var s = Apply("mapa_collect", "1-1").Settings;
        Assert.Equal(1, s.CollectMapId);
        Assert.Equal(91, Apply("MAPA_NPC", "5-1", s).Settings.NpcKillerMapId);
        Assert.Equal(306, Apply("mapa_npc", "1bl", s).Settings.NpcKillerMapId);
        Assert.Equal("mcb-50", Apply("municion_npc", "MCB-50", s).Settings.NpcKillerLaser);
        Assert.Equal("lcb-10", Apply("municion_gg", "lcb-10", s).Settings.Laser);
        Assert.Equal("Beta", Apply("gg", "beta", s).Settings.GalaxyGateName);
        Assert.Equal("Alpha", Apply("gg", "Alfa", s).Settings.GalaxyGateName);
        Assert.Equal(10, Apply("spins", "10", s).Settings.Spins);
        Assert.Equal("kami", Apply("ataque", "kamikaze", s).Settings.AttackMode);
        Assert.Equal("both", Apply("ataque", "ambos", s).Settings.AttackMode);
        Assert.Equal("drill", Apply("formacion_mover", "drill", s).Settings.RunFormation);
        Assert.Equal("turtle", Apply("formacion_atacar", "turtle", s).Settings.AttackFormation);
        Assert.Equal("turtle", Apply("formacion_final", "turtle", s).Settings.GgFinalNpcFormation);
        Assert.Equal("", Apply("formacion_mover", "ninguna", s).Settings.RunFormation);
        Assert.False(Apply("invitaciones", "no", s).Settings.AutoAcceptInvites);
        Assert.False(s.NpcKillerEnergyLeech); // opt-in
        Assert.True(Apply("leech", "si", s).Settings.NpcKillerEnergyLeech);
        Assert.False(Apply("leech", "no", s).Settings.NpcKillerEnergyLeech);
        Assert.Equal(1, s.CollectMapId); // untouched by the other keys
    }

    [Fact]
    public void AutoBuyIsSetPerBoosterAndOnlyOnRequest()
    {
        var (s, reply) = Apply("autocompra", "dmg_b01 si");
        Assert.True(s.AutoBuyBoosters[2051]);
        Assert.Single(s.AutoBuyBoosters);
        Assert.Contains("menos de 6 h", reply);
        Assert.False(Apply("autocompra", "DMG_B01 no", s).Settings.AutoBuyBoosters[2051]);
    }

    [Theory]
    [InlineData("modulo", "otro", "Modulo no valido")]
    [InlineData("mapa_collect", "9-9", "Mapa no valido")]
    [InlineData("municion_npc", "rsb-75", "Municion no valida")]
    [InlineData("gg", "Omega", "GG no valida")]
    [InlineData("spins", "3", "Spins no validos")]
    [InlineData("ataque", "cohetes", "Ataque no valido")]
    [InlineData("formacion_mover", "arrow", "Formacion no disponible")]
    [InlineData("autocompra", "DMG_B01", "Uso: /ajuste N autocompra")]
    [InlineData("autocompra", "XXX si", "Uso: /ajuste N autocompra")]
    [InlineData("invitaciones", "quizas", "invitaciones si|no")]
    [InlineData("leech", "quizas", "leech si|no")]
    [InlineData("color", "azul", "Clave desconocida")]
    public void InvalidValuesAreRejected(string key, string value, string error)
    {
        var (change, reply) = SettingsCommands.Parse(key, value, Formations);
        Assert.Null(change);
        Assert.Contains(error, reply);
    }

    [Fact]
    public void FormationsAreNotCheckedBeforeTheGameSendsThem()
    {
        var (change, _) = SettingsCommands.Parse("formacion_mover", "arrow", Array.Empty<string>());
        Assert.NotNull(change);
    }

    [Fact]
    public void DescribeShowsEverythingNeededToConfigureTheBot()
    {
        var s = new BotSettings
        {
            CollectMapId = 1,
            CollectTypesByMap = new() { [1] = new() { "BONUS_BOX" } },
            NpcKillerMapId = 91,
            GalaxyGateName = "Beta",
            AutoBuyBoosters = new() { [2051] = true, [2052] = false }
        };
        string text = SettingsCommands.Describe(s, "Piloto", 1, Formations, "/data/x.json");
        Assert.Contains("Collect: mapa 1-1; cajas: BONUS_BOX (/cajas 1)", text);
        Assert.Contains("NPC Killer: mapa 5-1; 0 objetivos; municion UCB-100", text);
        Assert.Contains("GG: Beta; spins 100", text);
        Assert.Contains("Disponibles: drill, turtle", text);
        Assert.Contains("Autocompra de boosters: DMG_B01 (codigos:", text);
        Assert.Contains("Fichero: /data/x.json", text);
        Assert.Contains("/ajuste 1 <clave> <valor>", text);
    }

    [Fact]
    public void TheNormalBoxIsAlwaysOffered()
    {
        Assert.Equal(new[] { "BONUS_BOX" }, SettingsCommands.BoxList(new string[0], new string[0]));
        Assert.Equal(new[] { "BONUS_BOX", "CARGO_BOX" },
            SettingsCommands.BoxList(new string[0], new[] { "CARGO_BOX", "bonus_box" }).Select(x => x.ToUpperInvariant()));
        string text = SettingsCommands.BoxTypes("Piloto", 2, 1, new[] { "BONUS_BOX" }, new string[0]);
        Assert.Contains("1. ✅ BONUS_BOX", text);
        Assert.Contains("/caja 2 <numeros>", text);
    }

    [Fact]
    public void DocumentedExampleIsAValidSettingsFile()
    {
        string json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "settings.example.json"));
        var s = System.Text.Json.JsonSerializer.Deserialize<BotSettings>(json)!;
        Assert.Equal("Collect", s.SelectedModule);
        Assert.Equal(new[] { "BONUS_BOX" }, s.CollectTypesByMap[s.CollectMapId]);
        Assert.Equal(new[] { "-=[ Streuner ]=-" }, s.NpcKillerTargetsByMap[s.NpcKillerMapId]);
        Assert.True(BotSettings.IsValidLaser(s.NpcKillerLaser) && BotSettings.IsValidLaser(s.Laser));
        Assert.Contains(s.GalaxyGateName, SettingsCommands.GateNames);
        Assert.Contains(s.Spins, new[] { 1, 5, 10, 100 });
        Assert.Empty(s.AutoBuyBoosters); // never buys anything unless asked
        // Every property of BotSettings is documented in the example.
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var documented = doc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet();
        Assert.All(typeof(BotSettings).GetProperties().Select(p => p.Name), name => Assert.Contains(name, documented));
    }
}
