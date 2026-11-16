using FunnyProject;
using FunnyProject.Telegram;
using FunnyProject.UserCollections;
using FunnyProject.UserCollections.Objects;
using FunnyProject.UserCollections.Resources;
using Xunit;

namespace PacketBot.Core.Tests;

public class MapObservationsTests
{
    private static User UserOnMap(int mapId)
    {
        var user = new User("1", "s", "wordgalaxy");
        user.UserData.MapId = mapId;
        return user;
    }

    [Fact]
    public void RecordsNpcNamesAndBoxTypesPerMap()
    {
        var observations = new MapObservations();
        var user = UserOnMap(1);
        user.Players.Add(new Player { ID = 1, Name = " -=[ Streuner ]=- ", IsNpc = true }, true);
        user.Players.Add(new Player { ID = 2, Name = "-=[ streuner ]=-", IsNpc = true }, true); // same name, other case
        user.Players.Add(new Player { ID = 3, Name = "Jugador", IsNpc = false }, true);
        user.Boxes.Add(new Box("h1", 0, 0, "BONUS_BOX"));
        user.Boxes.Add(new Box("h2", 0, 0, "CARGO"));
        observations.Sample(user);

        // Moving to map 2 keeps what was seen on map 1.
        user.UserData.MapId = 2;
        user.Players.Clear();
        user.Players.Add(new Player { ID = 4, Name = "-=[ Lordakia ]=-", IsNpc = true }, true);
        observations.Sample(user);

        Assert.Equal(new[] { "-=[ Streuner ]=-" }, observations.NpcNames(1));
        Assert.Equal(new[] { "BONUS_BOX", "CARGO" }, observations.BoxTypes(1));
        Assert.Contains("-=[ Lordakia ]=-", observations.NpcNames(2));
        Assert.Empty(observations.NpcNames(99));
    }

    [Fact]
    public void UnknownMapIsIgnored()
    {
        var observations = new MapObservations();
        var user = UserOnMap(0);
        user.Players.Add(new Player { ID = 1, Name = "x", IsNpc = true }, true);
        observations.Sample(user);
        Assert.Empty(observations.NpcNames(0));
    }

    [Fact]
    public void NpcTargetsReplyIsANumberedChecklist()
    {
        int mapId = FunnyProject.Navigation.MapGraph.Default.Maps.Values.Single(m => m.name == "1-1").id;
        Assert.Equal("🎯 NPC Killer — Piloto\nMapa: 1-1\nMunicion: MCB-50" +
            "\n1. ✅ A\n2. ⬜ b\n3. ✅ C" +
            "\n\nMarca o desmarca con /npc 2 <numeros>, por ejemplo /npc 2 1 3. Tambien: /npc 2 todos | ninguno.",
            BotStatusText.NpcTargets("Piloto", 2, mapId, "mcb-50", new[] { "C", "A" }, new[] { "b", "A" }));
        Assert.Equal("🎯 NPC Killer — Piloto\nMapa: sin seleccionar\nMunicion: UCB-100" +
            "\nTodavia no hay NPCs vistos en el mapa (se descubren al estar en el mapa).",
            BotStatusText.NpcTargets("Piloto", 1, 0, "ucb-100", Array.Empty<string>(), Array.Empty<string>()));
    }
}
