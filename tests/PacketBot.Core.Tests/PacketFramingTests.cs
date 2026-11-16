using FunnyProject;
using FunnyProject.Commands;
using FunnyProject.Commands.Write;
using Krypton_Core.Managers;
using Xunit;
using Action = FunnyProject.Commands.Write.Action;

namespace PacketBot.Core.Tests;

// Every outgoing command must be a whole number of frames
// [u16 length][length bytes]. A wrong length field desynchronises the game
// server's reader and it closes the connection (seen with CollectBox).
public class PacketFramingTests
{
    public static IEnumerable<object[]> Commands()
    {
        yield return new object[] { "Login", new Login(123456, "0123456789abcdef0123456789abcdef") };
        yield return new object[] { "Move", new Move(1000, 2000, 3000, 4000) };
        yield return new object[] { "CollectBox", new CollectBox("abcdef0123456789", 1000, 2000, 1100, 2100) };
        yield return new object[] { "CollectBox (hash vacio)", new CollectBox("", 1, 2, 3, 4) };
        yield return new object[] { "CollectBox (no ASCII)", new CollectBox("cajá", 1, 2, 3, 4) };
        yield return new object[] { "Action", new Action("ammunition_laser_ucb-100") };
        yield return new object[] { "AttackCommand", new AttackCommand(77, 1000, 2000) };
        yield return new object[] { "SelectShip", new SelectShip(77, 1000, 2000, 900, 1900) };
        yield return new object[] { "OpenBoosters", new OpenBoosters() };
        yield return new object[] { "RequestBoosters", new RequestBoosters() };
        yield return new object[] { "PortalSelect", new PortalSelect(12) };
        yield return new object[] { "PortalJump", new PortalJump() };
        yield return new object[] { "GroupAccept", new GroupAccept(42) };
        yield return new object[] { "PetFuelPurchase", new PetFuelPurchase() };
        yield return new object[] { "PetRepair", new PetRepair() };
    }

    [Theory]
    [MemberData(nameof(Commands))]
    public void CommandIsAWholeNumberOfFrames(string name, Command command)
    {
        byte[] bytes = command.param1.Message.ToArray();
        int offset = 0, frames = 0;
        while (offset < bytes.Length)
        {
            Assert.True(offset + 2 <= bytes.Length, name + ": cabecera incompleta");
            int length = (bytes[offset] << 8) | bytes[offset + 1];
            Assert.True(length >= 2, name + ": longitud " + length + " sin ID de paquete");
            offset += 2 + length;
            frames++;
        }
        Assert.True(offset == bytes.Length,
            $"{name}: la longitud declarada no coincide con los bytes escritos ({offset} != {bytes.Length})");
        Assert.True(frames >= 1);
    }

    [Fact]
    public void WriteUtfPrefixIsTheUtf8ByteCount()
    {
        var array = new Krypton_Core.Packets.Bytes.ByteArray();
        array.writeUTF("cajá");
        Assert.Equal(new byte[] { 0x00, 0x05, (byte)'c', (byte)'a', (byte)'j', 0xC3, 0xA1 }, array.Message);
    }

    [Fact]
    public void CollectBoxMatchesWireFormat()
    {
        const string hash = "abcdef0123456789";
        byte[] bytes = new CollectBox(hash, 1000, 2000, 1100, 2100).param1.Message.ToArray();
        // 2 ID + 4 x 4 coordinates + 2 string length + 16 hash bytes = 36.
        Assert.Equal(new byte[] { 0x00, 36, 0x19, 0x84 }, bytes[..4]); // length 36, ID 6532
        Assert.Equal(38, bytes.Length);
        Assert.Equal(new byte[] { 0x00, 16 }, bytes[20..22]);
        Assert.Equal(System.Text.Encoding.ASCII.GetBytes(hash), bytes[22..]);
    }
}
