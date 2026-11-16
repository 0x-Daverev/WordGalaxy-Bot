using System.Text.Json;
using FunnyProject.Services;
using Krypton_Core.Packets.Bytes;
using Xunit;

namespace PacketBot.Core.Tests;

[Collection("PacketTelemetry")] // Folder is static
public class PacketTelemetryTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "pb-telemetry-" + Guid.NewGuid().ToString("N"));
    private readonly string previous = PacketTelemetry.Folder;

    public PacketTelemetryTests() => PacketTelemetry.Folder = folder;

    public void Dispose()
    {
        PacketTelemetry.Folder = previous;
        try { Directory.Delete(folder, true); } catch (DirectoryNotFoundException) { }
    }

    [Theory]
    [InlineData("0|n|pvp|98765|0", "n|pvp")]
    [InlineData("0|A|STD|Has recibido 50 uridium", "A|STD")]
    [InlineData("0|URI|5|100|4000", "URI")]
    [InlineData("0|n|s|end", "n|s")]
    [InlineData("0|LM|ST|EP|1200", "LM|ST")]
    [InlineData("0|v|1234|5678", "v")]
    [InlineData("0|Un Jugador Con Espacios|x", null)]
    [InlineData("", null)]
    public void TextKeysKeepOnlyKeywords(string message, string? key) =>
        Assert.Equal(key, PacketTelemetry.TextKey(message));

    [Fact]
    public void UnknownPacketsAreCountedWithFewSamples()
    {
        var t = new PacketTelemetry();
        for (int i = 0; i < 5; i++) t.RecordUnknown(1234, new byte[] { 1, 2, (byte)i }, 307);
        t.RecordUnknown(1234, new byte[700], 1);
        var e = t.Snapshot().Packets[1234];
        Assert.Equal(6, e.Count);
        Assert.Equal((3, 700), (e.MinBytes, e.MaxBytes));
        Assert.Equal(5, e.Maps[307]);
        Assert.Equal(PacketTelemetry.MaxSamples, e.Samples.Count);
        Assert.Equal("010200", e.Samples[0]);
    }

    [Fact]
    public void SavingMergesIntoTodaysFile()
    {
        var day = new DateTime(2026, 11, 15, 10, 0, 0, DateTimeKind.Utc);
        var t = new PacketTelemetry { UserId = 42, Clock = () => day };
        t.RecordUnknown(1234, new byte[] { 9 }, 1);
        t.RecordText("0|n|pvp|98765|0");
        t.Save();
        t.RecordUnknown(1234, new byte[] { 9, 9 }, 1);
        t.RecordText("0|n|pvp|11111|1");
        t.Save();

        string path = Path.Combine(folder, "2026-11-15-42.json");
        var saved = JsonSerializer.Deserialize<PacketTelemetry.Report>(File.ReadAllText(path))!;
        Assert.Equal(2, saved.Packets[1234].Count);
        Assert.Equal(2, saved.TextKeys["n|pvp"]);
        Assert.DoesNotContain("98765", File.ReadAllText(path)); // no ids or names from texts
        Assert.Empty(t.Snapshot().Packets); // counting starts again
    }

    [Fact]
    public void PacketManagerRecordsWhatItDoesNotHandle()
    {
        var api = new FunnyProject.Api(new FunnyProject.User("t", "t", "t"));
        var manager = new FunnyProject.PacketHandlers.PacketManager(api);
        byte[] packet = { 0x7F, 0x00, 0xAA, 0xBB }; // id 32512, unknown
        manager.Parse(new EndianBinaryReader(EndianBitConverter.Big, new MemoryStream(packet)));
        var e = api.User.Telemetry.Snapshot().Packets[32512];
        Assert.Equal(1, e.Count);
        Assert.Equal("AABB", e.Samples[0]);
    }
}
