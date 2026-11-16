using Krypton_Core.Packets.Bytes;

namespace FunnyProject.Commands.Read
{
    // WordGalaxy_main.swf: §_-G4V§/§_-bG§_2.as, command 11468.
    public sealed class NpcHealthUpdate
    {
        public const short ID = 11468;
        public int PlayerID { get; }
        public int HP { get; }
        public int MaxHP { get; }
        public int Shield { get; }
        public int MaxShield { get; }
        public uint[] RawWords { get; } = new uint[8];

        private static int Rol(int value, int bits) =>
            (int)(((uint)value << bits) | ((uint)value >> (32 - bits)));
        private static int Ror(int value, int bits) =>
            (int)(((uint)value >> bits) | ((uint)value << (32 - bits)));


        public NpcHealthUpdate(EndianBinaryReader reader)
        {
            // Retain the eight network words for comparison with the AS dumper.
            // Do not assume the obfuscated AS source's textual order is the wire order.
            for (int i = 0; i < RawWords.Length; i++)
                RawWords[i] = unchecked((uint)reader.ReadInt32());
            int jd = Rol(unchecked((int)RawWords[0]), 10);
            int b12 = Ror(unchecked((int)RawWords[1]), 7);
            HP = Ror(unchecked((int)RawWords[2]), 8);
            // The fourth wire word is the current shield (AS field 'shield').
            // Verified against captures: 8C000000 -> 2240, 00000000 -> 0.
            Shield = Rol(unchecked((int)RawWords[3]), 12);
            MaxShield = Rol(unchecked((int)RawWords[4]), 9);
            MaxHP = Ror(unchecked((int)RawWords[5]), 13);
            PlayerID = Ror(unchecked((int)RawWords[6]), 9);
            int k3e = Ror(unchecked((int)RawWords[7]), 6);
            bool flag = reader.ReadBoolean();
        }

    }
}
