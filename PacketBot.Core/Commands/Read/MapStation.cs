using System;
using System.IO;
using System.Text;
using Krypton_Core.Packets.Bytes;

namespace FunnyProject.Commands.Read
{
    // Server packet 13718 (client §_-G4V§/§_-Uo§_2.as): a station of the map,
    // e.g. "HQ", "RepairDock" or "Hangar" (client hints: Asteroid,
    // BattleStation, BoosterStation).
    //
    // Layout checked against captures from map 1-1 (HQ 2000,2000; RepairDock
    // 2000,920; Hangar 3080,2000), with the rotations of the client's write():
    //   int x (rotl 10), int y (rotl 1), UTF name, vector of modules (int count
    //   + items), bool, int, int, bool, int faction (rotl 3), bool,
    //   short 24441 (padding), sub-packet 27378 (id + short), UTF,
    //   sub-packet 15758 (id + 3 shorts; the 3rd is the station type), int,
    //   bool, int.
    // The modules vector is variable, so the fields after it are found from
    // the padding 24441 followed by sub-packet 27378, not by offset.
    public sealed class MapStation
    {
        public const short ID = 13718;
        private const ushort Padding = 24441;  // 0x5F79
        private const ushort OwnerSub = 27378; // 0x6AF2
        private const ushort TypeSub = 15758;  // 0x3D8E

        public string Name { get; private set; } = "";
        public int X { get; private set; }
        public int Y { get; private set; }
        public int FactionId { get; private set; }
        public int Type { get; private set; } = -1;
        // Second text of the packet (empty on the stations seen so far).
        public string Label { get; private set; } = "";
        public bool Decoded { get; private set; }

        public MapStation(EndianBinaryReader read) : this(Body(read)) { }

        public MapStation(byte[] b)
        {
            if (b.Length < 10) return;
            X = Rotl(ReadInt(b, 0), 10);
            Y = Rotl(ReadInt(b, 4), 1);
            int len = (b[8] << 8) | b[9];
            if (len == 0 || len > 64 || 10 + len > b.Length) return;
            try { Name = new UTF8Encoding(false, true).GetString(b, 10, len); }
            catch (DecoderFallbackException) { return; }
            if (X < -1000 || X > 100000 || Y < -1000 || Y > 100000) return;
            Decoded = true;

            int pad = Find(b, 10 + len, Padding, OwnerSub);
            if (pad < 0) return;
            if (pad - 5 >= 0) FactionId = Rotl(ReadInt(b, pad - 5), 3);
            int utf = pad + 2 + 4;
            if (utf + 2 <= b.Length)
            {
                int labelLen = (b[utf] << 8) | b[utf + 1];
                if (utf + 2 + labelLen + 8 <= b.Length)
                {
                    try { Label = new UTF8Encoding(false, true).GetString(b, utf + 2, labelLen); }
                    catch (DecoderFallbackException) { }
                    int type = utf + 2 + labelLen;
                    if (ReadShort(b, type) == TypeSub) Type = ReadShort(b, type + 6);
                }
            }
        }

        // First position of the 16-bit value a directly followed by b, from start.
        private static int Find(byte[] data, int start, ushort a, ushort next)
        {
            for (int i = Math.Max(0, start); i + 4 <= data.Length; i++)
                if (ReadShort(data, i) == a && ReadShort(data, i + 2) == next) return i;
            return -1;
        }

        internal static byte[] Body(EndianBinaryReader read)
        {
            var stream = read.BaseStream;
            if (!stream.CanSeek) return Array.Empty<byte>();
            var body = new byte[Math.Max(0, stream.Length - stream.Position)];
            int done = 0, n;
            while (done < body.Length && (n = stream.Read(body, done, body.Length - done)) > 0) done += n;
            return body;
        }

        internal static int ReadInt(byte[] b, int p) => (b[p] << 24) | (b[p + 1] << 16) | (b[p + 2] << 8) | b[p + 3];
        internal static ushort ReadShort(byte[] b, int p) => (ushort)((b[p] << 8) | b[p + 1]);
        internal static int Rotl(int v, int bits) => (int)(((uint)v << bits) | ((uint)v >> (32 - bits)));
    }
}
