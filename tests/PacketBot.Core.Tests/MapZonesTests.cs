using System.Text;
using FunnyProject.Commands.Read;
using FunnyProject.UserCollections;
using Krypton_Core.Packets.Bytes;
using Xunit;

namespace PacketBot.Core.Tests;

public class MapZonesTests
{
    // ---- packet 7240 / 27912 ----

    private sealed class Body
    {
        private readonly List<byte> bytes = new();
        public Body Short(int v) { bytes.Add((byte)(v >> 8)); bytes.Add((byte)v); return this; }
        public Body Int(int v) { bytes.AddRange(new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v }); return this; }
        public Body Bool(bool v) { bytes.Add(v ? (byte)1 : (byte)0); return this; }
        public Body Utf(string s) { var b = Encoding.UTF8.GetBytes(s); Short(b.Length); bytes.AddRange(b); return this; }
        public Body Sub(int id, int value) => Short(id).Short(0).Short(0).Short(value);
        // Client write: (v >>> 7) | (v << 25).
        public Body Coords(params int[] values)
        {
            Int(values.Length);
            foreach (int v in values) Int((int)((uint)v >> 7 | (uint)v << 25));
            return this;
        }
        public byte[] ToArray() => bytes.ToArray();
    }

    [Fact]
    public void DecodesZoneInTheClientWriteOrder()
    {
        byte[] body = new Body().Short(2).Sub(MapAddPoi.PoiTypeId, 5).Short(10959).Bool(true)
            .Utf("barrier").Bool(false).Utf("poi_17").Coords(1000, 2000, 3000, 2500).Sub(MapAddPoi.PoiDesignId, 3)
            .ToArray();

        var poi = new MapAddPoi(body);

        Assert.True(poi.Decoded);
        Assert.Equal(2, poi.Shape);
        Assert.Equal(5, poi.PoiType);
        Assert.Equal(3, poi.Design);
        Assert.True(poi.Active);
        Assert.False(poi.Inverted);
        Assert.Equal("barrier", poi.TypeKey);
        Assert.Equal("poi_17", poi.Id);
        Assert.Equal(new[] { 1000, 2000, 3000, 2500 }, poi.Coordinates);
    }

    [Fact]
    public void DecodesZoneWhateverTheFieldOrderAfterShape()
    {
        // Read-code order, with the vector and design elsewhere.
        byte[] body = new Body().Short(0).Coords(5000, 6000, 700).Sub(MapAddPoi.PoiDesignId, 1)
            .Sub(MapAddPoi.PoiTypeId, 6).Bool(true).Short(-3).Utf("").Utf("c1").Bool(true)
            .ToArray();

        var poi = new MapAddPoi(body);

        Assert.True(poi.Decoded);
        Assert.Equal(0, poi.Shape);
        Assert.Equal(6, poi.PoiType);
        Assert.Equal("c1", poi.Id);
        Assert.True(poi.Inverted);
        Assert.Equal(new[] { 5000, 6000, 700 }, poi.Coordinates);
    }

    [Fact]
    public void RejectsBodyThatFitsNoLayout()
    {
        Assert.False(new MapAddPoi(new byte[] { 0, 9, 1, 2, 3 }).Decoded);
        Assert.False(new MapAddPoi(new Body().Short(1).Sub(MapAddPoi.PoiTypeId, 5).ToArray()).Decoded);
    }

    [Fact]
    public void RemovePacketReadsPaddingThenId()
    {
        byte[] body = new Body().Short(0).Utf("poi_17").ToArray();
        var reader = new EndianBinaryReader(EndianBitConverter.Big, new MemoryStream(body));
        Assert.Equal("poi_17", new MapRemovePoi(reader).Id);
    }

    [Fact]
    public void ServerZoneIsStoredAndRemovedById()
    {
        var zones = new MapZones();
        byte[] body = new Body().Short(1).Sub(MapAddPoi.PoiTypeId, 5).Short(0).Bool(true).Utf("k")
            .Bool(false).Utf("wall").Coords(1000, 1000, 2000, 1000, 2000, 3000, 1000, 3000)
            .Sub(MapAddPoi.PoiDesignId, 0).ToArray();

        var zone = zones.OnServerZone(new MapAddPoi(body), mapId: 0);

        Assert.NotNull(zone);
        Assert.Equal((1000, 1000, 1000, 2000), (zone!.Left, zone.Top, zone.Width, zone.Height));
        Assert.Single(zones.Barriers());
        Assert.True(zones.Remove("wall"));
        Assert.Empty(zones.Barriers());
    }

    [Fact]
    public void SwapsPairsThatOnlyFitTheMapAsYX()
    {
        var poi = new MapAddPoi(new Body().Short(2).Sub(MapAddPoi.PoiTypeId, 5).Short(0).Bool(true).Utf("")
            .Bool(false).Utf("z").Coords(2000, 18000, 3000, 19000).Sub(MapAddPoi.PoiDesignId, 0).ToArray());
        var zone = PoiZone.From(poi, 21000, 13100)!;
        Assert.Equal((18000, 2000), (zone.Left, zone.Top));
    }

    // ---- collision, as the client does it ----

    private static PoiZone Box(int l, int t, int r, int b, bool inverted = false, int type = 5) =>
        new("box", type, PoiShape.Rectangle, true, inverted, new[] { (l, t), (r, b) }, 0);

    private static MapZones With(params PoiZone[] list)
    {
        var zones = new MapZones();
        foreach (var z in list) zones.Add(z);
        return zones;
    }

    [Fact]
    public void StraightMoveStopsFortyUnitsBeforeTheBarrier()
    {
        var zones = With(Box(5000, 0, 6000, 13100));
        Assert.Equal((4960, 5000), zones.ClampMove(1000, 5000, 9000, 5000));
        Assert.True(zones.IsBlocked(5500, 5000));
    }

    [Fact]
    public void FreePathIsNotChanged()
    {
        var zones = With(Box(5000, 0, 6000, 4000));
        Assert.Equal((9000, 6000), zones.ClampMove(1000, 6000, 9000, 6000));
        Assert.Equal((9000, 6000), zones.PlanMove(1000, 6000, 9000, 6000));
    }

    [Fact]
    public void OnlyNoAccessZonesBlock()
    {
        var zones = With(Box(5000, 0, 6000, 13100, type: 8)); // radiation
        Assert.Empty(zones.Barriers());
        Assert.Equal((9000, 5000), zones.PlanMove(1000, 5000, 9000, 5000));
    }

    [Fact]
    public void InvertedZoneKeepsTheShipInside()
    {
        var zones = With(Box(2000, 2000, 8000, 8000, inverted: true));
        Assert.False(zones.IsBlocked(5000, 5000));
        Assert.True(zones.IsBlocked(8200, 5000));
        Assert.Equal((7960, 5000), zones.ClampMove(5000, 5000, 12000, 5000));
    }

    [Fact]
    public void CircleBlocksAtItsRadius()
    {
        var zones = With(new PoiZone("c", 5, PoiShape.Circle, true, false, new[] { (10000, 5000) }, 1000));
        Assert.Equal((8960, 5000), zones.ClampMove(1000, 5000, 20000, 5000));
    }

    [Fact]
    public void ShipAlreadyInsideMayLeave()
    {
        var zones = With(Box(5000, 0, 6000, 13100));
        Assert.Equal((1000, 5000), zones.ClampMove(5500, 5000, 1000, 5000));
    }

    [Fact]
    public void PlanGoesAroundTheBarrier()
    {
        // Wall from y=0 to 8000: the way round is below it.
        var zones = With(Box(5000, 0, 6000, 8000));
        var first = zones.PlanMove(1000, 5000, 9000, 5000);

        Assert.Equal((5000 - MapZones.Clearance, 8000 + MapZones.Clearance), first);
        Assert.Equal(first, zones.ClampMove(1000, 5000, first.X, first.Y));
        var second = zones.PlanMove(first.X, first.Y, 9000, 5000);
        Assert.Equal((6000 + MapZones.Clearance, 8000 + MapZones.Clearance), second);
        Assert.Equal((9000, 5000), zones.PlanMove(second.X, second.Y, 9000, 5000));
    }

    [Fact]
    public void TargetInsideABarrierIsMovedOutside()
    {
        var zones = With(Box(5000, 0, 6000, 13100));
        var dest = zones.PlanMove(1000, 5000, 5100, 5000);
        Assert.Equal((5000 - MapZones.Push - 1, 5000), dest);
        Assert.False(zones.IsBlocked(dest.X, dest.Y));
    }

    [Fact]
    public void FullWallWithoutWayRoundClampsAtTheWall()
    {
        var zones = With(Box(5000, -500, 6000, 14000));
        Assert.Equal((4960, 5000), zones.PlanMove(1000, 5000, 9000, 5000));
    }

    // ---- zones as map 307 sends them: no id, inverted lines and points ----

    private static PoiZone Wall(int x1, int y1, int x2, int y2) =>
        new("", 5, PoiShape.Rectangle, true, true, new[] { (x1, y1), (x2, y2) }, 0);

    [Fact]
    public void ZonesWithoutIdDoNotReplaceEachOther()
    {
        var zones = With(Wall(1000, 10800, 1600, 10800), Wall(14400, 5300, 14400, 5300), Wall(9700, -1, 9700, 5400));
        Assert.Equal(3, zones.Barriers().Length);
        Assert.False(zones.Remove(""));
        Assert.Equal(3, zones.Barriers().Length);
    }

    [Fact]
    public void ZonesSentAgainOnRespawnDoNotPileUp()
    {
        var zones = With(Wall(1000, 10800, 1600, 10800), Wall(14400, 5300, 14400, 5300));
        // Respawn on 2BL: the server sends the same zones again.
        zones.Add(Wall(1000, 10800, 1600, 10800));
        zones.Add(Wall(14400, 5300, 14400, 5300));
        Assert.Equal(2, zones.Barriers().Length);
        zones.Add(Wall(14400, 5300, 14500, 5300)); // a different one
        Assert.Equal(3, zones.Barriers().Length);
    }

    [Fact]
    public void InvertedLineIsAWallOfMarginThickness()
    {
        var zones = With(Wall(1000, 10800, 1600, 10800));
        Assert.True(zones.IsBlocked(1300, 10800));
        Assert.True(zones.IsBlocked(1300, 10800 - 500));
        Assert.False(zones.IsBlocked(1300, 10800 - 600));
        Assert.Equal((1300, 10800 - 512 - MapZones.Push), zones.ClampMove(1300, 9000, 1300, 12500));
    }

    [Fact]
    public void InvertedPointIsASquareBlockToGoAround()
    {
        var zones = With(Wall(14400, 5300, 14400, 5300));
        Assert.True(zones.IsBlocked(14400, 5300));
        var first = zones.PlanMove(12000, 5300, 17000, 5300);
        Assert.NotEqual((17000, 5300), first);
        Assert.Equal(first, zones.ClampMove(12000, 5300, first.X, first.Y));
        var second = zones.PlanMove(first.X, first.Y, 17000, 5300);
        var third = zones.PlanMove(second.X, second.Y, 17000, 5300);
        Assert.Equal((17000, 5300), third);
    }

    [Fact]
    public void TargetInsideAThinInvertedWallIsMovedOut()
    {
        var zones = With(Wall(9700, -1, 9700, 5400));
        var dest = zones.PlanMove(5000, 3000, 9700, 3000);
        Assert.False(zones.IsBlocked(dest.X, dest.Y));
        Assert.Equal((9700 - 512 - MapZones.Push - 1, 3000), dest);
    }
}
