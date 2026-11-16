using System;
using System.IO;
using Krypton_Core.Packets.Bytes;

namespace FunnyProject.Commands.Read
{
    public sealed class GroupInvitation : EventArgs
    {
        public const short ID = 596;
        public int InviterId { get; }
        public int AcceptanceId { get; }
        public string InviterName { get; }
        public string InviteeName { get; }

        // Verified against the packet 596 capture:
        // 6BE3 7471 3B7B 0018 | 6B800791 | 678001E4 |
        // 6BE3 7471 3B7B 0018 | UTF(NebulaFox) | UTF(darkvader) | F460 9300
        //
        // Nested _-71b, _-A4d rotated right 9, _-H28 rotated right 11,
        // nested _-71b, _-94n, _-K45, trailing markers.
        public GroupInvitation(EndianBinaryReader reader)
        {
            ReadNested(reader);
            InviterId = (int)RotateLeft((uint)reader.ReadInt32(), 9);
            AcceptanceId = (int)RotateLeft((uint)reader.ReadInt32(), 11);
            ReadNested(reader);
            InviteeName = reader.ReadString();
            InviterName = reader.ReadString();

            short marker = reader.ReadInt16();
            short end = reader.ReadInt16();
            if (marker != unchecked((short)0xF460) || end != unchecked((short)0x9300))
                throw new FormatException($"Unexpected invitation footer: {marker:X4} {end:X4}");
            if (AcceptanceId <= 0 || InviterId <= 0)
                throw new FormatException("Invalid group invitation IDs");
            if (reader.BaseStream.CanSeek && reader.BaseStream.Position != reader.BaseStream.Length)
                throw new FormatException("Unexpected trailing bytes in invitation 596");
        }

        private static void ReadNested(EndianBinaryReader reader)
        {
            if (reader.ReadUInt16() != 27619 ||
                reader.ReadUInt16() != 29809 ||
                reader.ReadUInt16() != 15227)
                throw new FormatException("Unexpected _-71b header in invitation 596");
            reader.ReadInt16(); // _-02S, observed value 24
        }

        private static uint RotateLeft(uint value, int shift) =>
            (value << shift) | (value >> (32 - shift));
    }
}
