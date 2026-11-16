using System.Text.Json;
using FunnyProject;
using FunnyProject.Logic;
using FunnyProject.UserCollections;
using Xunit;

namespace PacketBot.Core.Tests;

public class PreferredZonesTests
{
    private const int Map = 1; // 1-1, 21000x13100

    [Fact]
    public void ZoneIsAddedFromAnyTwoCornersAndClampedToTheMap()
    {
        var s = new BotSettings();
        var (ok, reply) = PreferredZones.Add(s, Map, 5000, 4000, -800, 1000);
        Assert.True(ok, reply);
        var z = Assert.Single(PreferredZones.For(s, Map));
        Assert.Equal((0, 1000, 5000, 3000), (z.X, z.Y, z.Width, z.Height));
        Assert.Empty(PreferredZones.For(s, 2)); // per map
    }

    [Fact]
    public void TooSmallOrTooManyZonesAreRejected()
    {
        var s = new BotSettings();
        Assert.False(PreferredZones.Add(s, Map, 100, 100, 200, 5000).Added);
        Assert.False(PreferredZones.Add(s, 0, 0, 0, 5000, 5000).Added);
        for (int i = 0; i < PreferredZones.MaxPerMap; i++)
            Assert.True(PreferredZones.Add(s, Map, 0, 0, 1000, 1000).Added);
        Assert.False(PreferredZones.Add(s, Map, 0, 0, 1000, 1000).Added);
    }

    [Fact]
    public void ClickRemovesTheTopZoneUnderThePoint()
    {
        var s = new BotSettings();
        PreferredZones.Add(s, Map, 0, 0, 4000, 4000);
        PreferredZones.Add(s, Map, 2000, 2000, 6000, 6000);
        Assert.False(PreferredZones.RemoveAt(s, Map, 9000, 9000).Removed);
        Assert.True(PreferredZones.RemoveAt(s, Map, 3000, 3000).Removed); // both: the last one goes
        var left = Assert.Single(PreferredZones.For(s, Map));
        Assert.Equal(0, left.X);
        Assert.StartsWith("Borradas 1", PreferredZones.Clear(s, Map));
        Assert.Empty(PreferredZones.For(s, Map));
    }

    [Fact]
    public void TelegramDeletesByNumberOrAllOnTheNpcKillerMap()
    {
        var s = new BotSettings { NpcKillerMapId = Map };
        PreferredZones.Add(s, Map, 0, 0, 1000, 1000);
        PreferredZones.Add(s, Map, 5000, 5000, 7000, 7000);
        Assert.Contains("2. (5000, 5000) 2000x2000", PreferredZones.Status(s, "Cuenta", 1));

        var (change, _) = PreferredZones.ParseCommand(s, "borrar 1");
        change!(s);
        Assert.Equal(5000, Assert.Single(PreferredZones.For(s, Map)).X);
        Assert.Null(PreferredZones.ParseCommand(s, "borrar 5").Change);
        Assert.Null(PreferredZones.ParseCommand(s, "quitar 1").Change);
        PreferredZones.ParseCommand(s, "borrar todas").Change!(s);
        Assert.Empty(PreferredZones.For(s, Map));
        Assert.Null(PreferredZones.ParseCommand(new BotSettings(), "borrar todas").Change); // no NPC Killer map
    }

    [Fact]
    public void ReturnPointIsInTheNearestZone()
    {
        var near = new PreferredZone { X = 2000, Y = 2000, Width = 2000, Height = 2000 };
        var far = new PreferredZone { X = 15000, Y = 9000, Width = 4000, Height = 3000 };
        var random = new Random(3);
        for (int i = 0; i < 50; i++)
        {
            var p = PreferredZones.ReturnPoint(new[] { far, near }, new MapZones(), random, 500, 500);
            Assert.True(near.Contains(p!.Value.X, p.Value.Y));
        }
        Assert.Null(PreferredZones.ReturnPoint(Array.Empty<PreferredZone>(), new MapZones(), random, 0, 0));
    }

    [Fact]
    public void RoamPointsStayInsideZonesAndOutOfBarriers()
    {
        var zones = new[] { new PreferredZone { X = 1000, Y = 1000, Width = 4000, Height = 4000 } };
        var barriers = new MapZones();
        // Left half of the zone is a barrier.
        barriers.Add(new PoiZone("b", 5, PoiShape.Rectangle, true, false, new[] { (0, 0), (3000, 13100) }, 0));
        var random = new Random(7);
        for (int i = 0; i < 200; i++)
        {
            var p = PreferredZones.RoamPoint(zones, barriers, random);
            Assert.NotNull(p);
            Assert.True(zones[0].Contains(p.Value.X, p.Value.Y));
            Assert.False(barriers.IsBlocked(p.Value.X, p.Value.Y));
        }
        Assert.Null(PreferredZones.RoamPoint(Array.Empty<PreferredZone>(), barriers, random));
    }

    [Fact]
    public void ZonesSurviveTheSettingsFile()
    {
        var s = new BotSettings();
        PreferredZones.Add(s, 307, 1000, 2000, 4000, 5000);
        var loaded = JsonSerializer.Deserialize<BotSettings>(JsonSerializer.Serialize(s))!;
        var z = Assert.Single(PreferredZones.For(loaded, 307));
        Assert.Equal((1000, 2000, 3000, 3000), (z.X, z.Y, z.Width, z.Height));
    }
}
