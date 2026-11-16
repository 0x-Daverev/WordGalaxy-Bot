using FunnyProject.Navigation;
using Xunit;

namespace PacketBot.Core.Tests;

// Ported from the orphaned Python tests in tests/test_navigation.py, which
// import a `navigation` package that does not exist in this repository.
public class MapGraphTests
{
    private static readonly MapGraph Graph = new();

    private static int IdOf(string name) => Graph.Maps.Values.Single(m => m.name == name).id;

    // Follows MapGraph.Next hop by hop, exactly as MapTravelModule does.
    private static List<MapGraph.Portal> Route(int from, int to, int faction)
    {
        var route = new List<MapGraph.Portal>();
        int current = from;
        while (Graph.Next(current, to, faction) is { } portal)
        {
            route.Add(portal);
            current = portal.target_map;
            Assert.True(route.Count < Graph.Maps.Count, "Route does not converge");
        }
        return route;
    }

    [Fact]
    public void MapsJsonIsLoadedFromOutputDirectory()
    {
        Assert.NotEmpty(Graph.Maps);
        Assert.Contains(Graph.Maps.Values, m => m.name == "1-1");
    }

    [Fact]
    public void SameMapHasNoNextPortal() => Assert.Null(Graph.Next(1, 1, 1));

    [Fact]
    public void DirectRouteReturnsPortalCoordinates()
    {
        var portal = Graph.Next(IdOf("1-1"), IdOf("1-2"), 1);
        Assert.NotNull(portal);
        Assert.Equal((2, 18500, 11500), (portal!.target_map, portal.x, portal.y));
    }

    [Fact]
    public void RouteToBattleLineEndsInTargetMap()
    {
        var route = Route(IdOf("1-1"), IdOf("3BL"), 1);
        Assert.NotEmpty(route);
        Assert.Equal(308, route[^1].target_map);
    }

    [Fact]
    public void UnknownMapThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => Graph.Next(-123, 1, 1));
        Assert.Throws<ArgumentException>(() => Graph.Next(1, -123, 1));
    }

    [Fact]
    public void FactionRestrictedPortalsAreRespected()
    {
        int from = IdOf("5-4");
        // Each faction leaves 5-4 through its own portal to map 92.
        Assert.Equal(92, Graph.Next(from, IdOf("1-1"), 1)!.target_map);
        // A faction without a matching portal has no route out of 5-4.
        Assert.Throws<InvalidOperationException>(() => Graph.Next(from, IdOf("1-1"), 99));
    }
}
