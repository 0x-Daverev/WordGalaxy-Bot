using FunnyProject.Commands.Read;
using FunnyProject.UserCollections;
using Xunit;

namespace PacketBot.Core.Tests;

[Collection("Stations")] // Stations.File is static
public class StationsAndItemsTests : IDisposable
{
    // Captured on map 1-1 (packet 13718).
    private const string Hq = "F4000001000003E800024851000000000100000000000000000120000000015F796AF2000000003D8EF819BAB6002E000000000108007A12";
    private const string RepairDock = "F4000001000001CC000A526570616972446F636B000000000100000000000000000120000000015F796AF2000000003D8EF819BAB60035000000000110007A12";
    private const string Hangar = "02000003000003E8000648616E676172000000000100000000000000000120000000015F796AF2000000003D8EF819BAB60030000000000118007A12";
    // Captured item states (packet 12754): cloak CPU 12 of 99999; rocket launcher 1 of 5.
    private const string CloakCpu = "00402800000000000015A300001C65717569706D656E745F65787472615F6370755F636C30346B2D786C010100042BEE00000003488C7B610000000E747469705F636C6F616B5F637075613C00056712000000010423001C65717569706D656E745F65787472615F6370755F636C30346B2D786C613C000167126F500006255459504525765C488C7B610000001C65717569706D656E745F65787472615F6370755F636C30346B2D786C613C0007671200000000765C488C7B6100000019747469705F646F75626C655F636C69636B5F746F5F66697265613C0005671200000000765CAB9B305B2BEE00000002488C7B610000000E747469705F636C6F616B5F637075613C00056712000000010423001C65717569706D656E745F65787472615F6370755F636C30346B2D786C613C000167126F500006255459504525765C488C7B610000001C65717569706D656E745F65787472615F6370755F636C30346B2D786C613C0007671200000000765CAB9B40F869F00000000001001C65717569706D656E745F65787472615F6370755F636C30346B2D786C01";
    private const string RocketLauncher = "003FF000000000000015A300002565717569706D656E745F776561706F6E5F726F636B65746C61756E636865725F6873742D32010100002BEE00000001488C7B6100000013747469705F726F636B65746C61756E63686572613C00056712000000010423002565717569706D656E745F776561706F6E5F726F636B65746C61756E636865725F6873742D32613C000167126F500006255459504525765CAB9B305B2BEE00000001488C7B6100000013747469705F726F636B65746C61756E63686572613C00056712000000010423002565717569706D656E745F776561706F6E5F726F636B65746C61756E636865725F6873742D32613C000167126F500006255459504525765CAB9B401400000000000000002565717569706D656E745F776561706F6E5F726F636B65746C61756E636865725F6873742D3201";

    private readonly string file = Path.Combine(Path.GetTempPath(), "pb-stations-" + Guid.NewGuid().ToString("N") + ".json");
    private readonly string previous = Stations.File;

    public StationsAndItemsTests() => Stations.File = file;

    public void Dispose()
    {
        Stations.File = previous;
        if (File.Exists(file)) File.Delete(file);
    }

    [Theory]
    [InlineData(Hq, "HQ", 2000, 2000, 46)]
    [InlineData(RepairDock, "RepairDock", 2000, 920, 53)]
    [InlineData(Hangar, "Hangar", 3080, 2000, 48)]
    public void StationsOfMap11AreDecoded(string hex, string name, int x, int y, int type)
    {
        var s = new MapStation(Convert.FromHexString(hex));
        Assert.True(s.Decoded);
        Assert.Equal((name, x, y, 1, type), (s.Name, s.X, s.Y, s.FactionId, s.Type));
    }

    [Fact]
    public void StationsAreKeptForTheMapAndRemembered()
    {
        var stations = new Stations();
        stations.OnServerStation(new MapStation(Convert.FromHexString(Hq)), 1);
        stations.OnServerStation(new MapStation(Convert.FromHexString(Hq)), 1); // sent again: one
        stations.OnServerStation(new MapStation(Convert.FromHexString(Hangar)), 1);
        Assert.Equal(2, stations.Current().Length);

        stations.Clear(); // another map
        Assert.Empty(stations.Current());
        var known = new Stations().For(1); // a fresh instance reads the file
        Assert.Equal(new[] { "HQ", "Hangar" }, known.Select(s => s.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Empty(new Stations().For(2));
    }

    [Fact]
    public void ItemStatesGiveAmountAndMaximum()
    {
        var cpu = new ItemStatus(Convert.FromHexString(CloakCpu), 0);
        Assert.Equal(("equipment_extra_cpu_cl04k-xl", 12d, 99999d), (cpu.ItemId, cpu.Amount, cpu.Maximum));
        var rockets = new ItemStatus(Convert.FromHexString(RocketLauncher), 0);
        Assert.Equal(("equipment_weapon_rocketlauncher_hst-2", 1d, 5d), (rockets.ItemId, rockets.Amount, rockets.Maximum));
        Assert.Equal(Convert.FromHexString(RocketLauncher).Length, rockets.Length);
    }

    [Fact]
    public void TheWholeItemBarIsFoundInsideOtherData()
    {
        // As in packet 30106: other fields around each embedded item state.
        var bar = new List<byte> { 0, 0, 0, 3, 0x78, 0x15, 1 };
        foreach (var hex in new[] { CloakCpu, RocketLauncher })
        {
            bar.AddRange(new byte[] { 0x5A, 0xE5, 0x31, 0xD2 }); // item 23269, state 12754
            bar.AddRange(Convert.FromHexString(hex));
            bar.AddRange(new byte[] { 0, 1, 0, 0, 0x34, 0xA6 });
        }
        var items = new ShipItems();
        Assert.Equal(2, items.Load(ItemStatus.FindAll(bar.ToArray()), DateTime.UtcNow));
        var all = items.Snapshot();
        Assert.Equal(new[] { "Armas", "CPU" }, all.Select(i => i.Category).OrderBy(c => c));
        Assert.True(items.TryGet("equipment_extra_cpu_cl04k-xl", out var cpu));
        Assert.Equal((12d, 99999d, "CL04K-XL"), (cpu.Amount, cpu.Maximum, cpu.Name));
    }

    [Fact]
    public void AnUpdateWithoutMaximumKeepsTheKnownOne()
    {
        var items = new ShipItems();
        items.Update(new ItemStatus(Convert.FromHexString(CloakCpu), 0), DateTime.UtcNow);
        var cut = Convert.FromHexString(CloakCpu)[..60]; // id but no tooltips
        var partial = new ItemStatus(cut, 0);
        Assert.True(partial.Decoded);
        Assert.True(double.IsNaN(partial.Maximum));
        items.Update(partial, DateTime.UtcNow);
        Assert.True(items.TryGet("equipment_extra_cpu_cl04k-xl", out var cpu));
        Assert.Equal(99999d, cpu.Maximum);
    }

    [Theory]
    [InlineData("ammunition_laser_ucb-100", "Munición láser", "UCB-100")]
    [InlineData("ammunition_rocketlauncher_ubr-100", "Lanzacohetes", "UBR-100")]
    [InlineData("tech_energy-leech", "Techs", "ENERGY-LEECH")]
    [InlineData("drone_formation_f-3d-dr", "Formaciones", "F-3D-DR")]
    [InlineData("something_new", "Something", "new")]
    public void ItemIdsAreGrouped(string id, string category, string name) =>
        Assert.Equal((category, name), ShipItems.Describe(id));
}
