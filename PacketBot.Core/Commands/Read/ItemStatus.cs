using System;
using System.Collections.Generic;
using System.Text;
using Krypton_Core.Packets.Bytes;

namespace FunnyProject.Commands.Read
{
    // State of one item of the item bar (client §_-G4V§/§_-a3G§.as): its id
    // (e.g. "ammunition_laser_ucb-100"), the amount (counterValue) and the
    // maximum (maxCounterValue).
    //  - Packet 12754 sends one when it changes.
    //  - Packet 30106 (§_-V4m§, item bar at login) has one inside each item of
    //    each category (§_-C11§ = this state + timer + style).
    //
    // Layout checked against captures (cloak CPU 12 of 99999, rocket
    // launcher 1 and 2 of 5):
    //   bool, double amount, short 5539 (padding), bool, UTF id,
    //   bool, bool, short, tooltip (11246 ... -21605), short 12379,
    //   tooltip (11246 ... -21605), double maximum, bool, UTF icon, bool.
    // Tooltips vary in size; the maximum is found right after the end mark
    // (-21605) of the second one.
    public sealed class ItemStatus
    {
        public const short ID = 12754;
        private const ushort Padding = 5539;       // 0x15A3
        private const ushort TooltipEnd = 0xAB9B;  // -21605
        private const ushort BetweenTooltips = 12379; // 0x305B
        private const ushort Tooltip = 11246;      // 0x2BEE

        public string ItemId { get; private set; } = "";
        public double Amount { get; private set; }
        // NaN when it could not be read.
        public double Maximum { get; private set; } = double.NaN;
        public bool Decoded { get; private set; }
        // Bytes used from the start, when Maximum was read.
        internal int Length { get; private set; }

        public ItemStatus(EndianBinaryReader read) : this(MapStation.Body(read), 0) { }

        public ItemStatus(byte[] b, int start)
        {
            int p = start;
            if (p + 14 > b.Length || b[p] > 1 || b[p + 11] > 1) return;
            if (MapStation.ReadShort(b, p + 9) != Padding) return;
            double amount = BitConverter.Int64BitsToDouble(ReadLong(b, p + 1));
            if (!Sane(amount)) return;
            int len = (b[p + 12] << 8) | b[p + 13];
            if (len == 0 || len > 96 || p + 14 + len > b.Length) return;
            string? id = Identifier(b, p + 14, len);
            if (id == null) return;
            ItemId = id;
            Amount = amount;
            Decoded = true;

            // Second tooltip: after "end, 12379, 11246"; its end mark is the
            // first one followed by a sane double, a bool, an identifier and a bool.
            int after = p + 14 + len;
            int second = FindTriple(b, after, TooltipEnd, BetweenTooltips, Tooltip);
            if (second < 0) return;
            for (int e = second + 6; e + 2 + 8 + 1 + 2 + 1 <= b.Length; e++)
            {
                if (MapStation.ReadShort(b, e) != TooltipEnd) continue;
                int d = e + 2;
                double max = BitConverter.Int64BitsToDouble(ReadLong(b, d));
                if (!Sane(max) || b[d + 8] > 1) continue;
                int iconLen = (b[d + 9] << 8) | b[d + 10];
                int end = d + 11 + iconLen;
                if (end + 1 > b.Length || b[end] > 1 || (iconLen > 0 && Identifier(b, d + 11, iconLen) == null)) continue;
                Maximum = max;
                Length = end + 1 - start;
                return;
            }
        }

        // Every item state inside a packet 30106 (item bar at login).
        public static List<ItemStatus> FindAll(byte[] b)
        {
            var found = new List<ItemStatus>();
            for (int i = 0; i + 2 + 14 <= b.Length; i++)
            {
                if (MapStation.ReadShort(b, i) != (ushort)ID) continue;
                var item = new ItemStatus(b, i + 2);
                if (!item.Decoded) continue;
                found.Add(item);
                if (item.Length > 0) i += 1 + item.Length;
            }
            return found;
        }

        private static bool Sane(double v) => !double.IsNaN(v) && !double.IsInfinity(v) && v >= 0 && v < 1e12;

        // Item ids are ASCII words like "ammunition_laser_ucb-100".
        private static string? Identifier(byte[] b, int p, int len)
        {
            for (int i = p; i < p + len; i++)
            {
                byte c = b[i];
                bool ok = c is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9'
                    or (byte)'_' or (byte)'-' or (byte)'.';
                if (!ok) return null;
            }
            return Encoding.ASCII.GetString(b, p, len);
        }

        private static int FindTriple(byte[] b, int start, ushort x, ushort y, ushort z)
        {
            for (int i = start; i + 6 <= b.Length; i++)
                if (MapStation.ReadShort(b, i) == x && MapStation.ReadShort(b, i + 2) == y &&
                    MapStation.ReadShort(b, i + 4) == z)
                    return i;
            return -1;
        }

        private static long ReadLong(byte[] b, int p)
        {
            long v = 0;
            for (int i = 0; i < 8; i++) v = (v << 8) | b[p + i];
            return v;
        }
    }
}
