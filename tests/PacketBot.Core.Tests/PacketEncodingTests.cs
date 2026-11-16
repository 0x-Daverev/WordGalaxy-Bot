using System.Buffers.Binary;
using FunnyProject.Commands.Write;
using Krypton_Core.Packets.Bytes;
using Xunit;

namespace PacketBot.Core.Tests;

// Golden tests for the wire format. Client.Send transmits param1.Message as is.
public class PacketEncodingTests
{
    private static byte[] BigEndian(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        return bytes;
    }

    [Fact]
    public void ByteArrayWritesNumbersBigEndian()
    {
        var array = new ByteArray();
        array.writeShort(0x1234);
        array.writeInt(0x0A0B0C0D);
        array.writeBoolean(true);
        Assert.Equal(new byte[] { 0x12, 0x34, 0x0A, 0x0B, 0x0C, 0x0D, 0x01 }, array.Message);
    }

    [Fact]
    public void MovePacketMatchesWireFormat()
    {
        int fromX = 1000, fromY = 2000, toX = 3000, toY = 4000;
        var move = new Move(fromX, fromY, toX, toY);

        var expected = new List<byte> { 0x00, 0x14, 0x42, 0xA7 }; // length 20, ID 17063
        expected.AddRange(BigEndian((int)((uint)toY >> 8 | (uint)toY << 24)));
        expected.AddRange(BigEndian((int)((uint)fromX >> 15 | (uint)fromX << 17)));
        expected.AddRange(new byte[] { 0xF9, 0x7D }); // -1667
        expected.AddRange(BigEndian((int)((uint)toX << 4 | (uint)toX >> 28)));
        expected.AddRange(BigEndian((int)((uint)fromY << 5 | (uint)fromY >> 27)));

        Assert.Equal(expected, move.param1.Message);
        Assert.Equal(22, move.param1.Message.Count); // 2-byte length + 20-byte body
    }

    [Fact]
    public void PacketIdIsReadFromBytesTwoAndThree()
    {
        // Same framing rule Client.Send uses for diagnostics.
        byte[] bytes = new Move(0, 0, 0, 0).param1.Message.ToArray();
        short id = (short)((bytes[2] << 8) | bytes[3]);
        Assert.Equal(Move.ID, id);
    }

    [Fact]
    public void EnergyLeechPacketMatchesTheGameCapture()
    {
        // Captured from the game client when buying Energy Leech.
        byte[] capture = Convert.FromHexString("001B49C9000100010011" +
            Convert.ToHexString(System.Text.Encoding.ASCII.GetBytes("tech_energy-leech")) + "55A2");
        var action = new FunnyProject.Commands.Write.Action(FunnyProject.Services.EnergyLeechService.ItemId);
        Assert.Equal(capture, action.param1.Message.ToArray());
    }
}
