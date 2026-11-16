using Krypton_Core.Packets.Bytes;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace FunnyProject.Commands.Read
{
    // Server packet 7240: adds a map zone ("POI"). Zones of type NoAccess are
    // the barriers the ship cannot cross (e.g. map 2BL).
    //
    // Fields, from the client (§_-G4V§/§_-O2§.as): shape (short), poiType
    // (sub-packet 29441), design (sub-packet 20767), active (bool), inverted
    // (bool), typeKey (UTF), id (UTF), shapeCoordinates (int count + ints,
    // each rotated: client writes v>>>7 | v<<25) and one padding short.
    // The client's read/write code is flow-obfuscated, so the exact wire
    // order cannot be taken from it. Only "shape first" is certain (it runs
    // before any obfuscated branch). The rest is found structurally: the
    // decoder tries the remaining fields at each position and keeps the
    // layout that consumes the whole body with valid values.
    public sealed class MapAddPoi
    {
        public const short ID = 7240;
        public const short PoiTypeId = 29441;
        public const short PoiDesignId = 20767;

        public string Id { get; private set; } = "";
        public string TypeKey { get; private set; } = "";
        public int PoiType { get; private set; } = -1;
        public int Design { get; private set; }
        public int Shape { get; private set; } = -1;
        public bool Active { get; private set; }
        public bool Inverted { get; private set; }
        public int[] Coordinates { get; private set; } = Array.Empty<int>();
        // False when no layout fits; the packet is then ignored.
        public bool Decoded { get; private set; }
        // Field order that matched, for diagnostics ("shape,type,pad,...").
        public string Layout { get; private set; } = "";

        public MapAddPoi(EndianBinaryReader read)
        {
            var stream = read.BaseStream;
            var body = new byte[stream.Length - stream.Position];
            int done = 0;
            while (done < body.Length)
            {
                int n = stream.Read(body, done, body.Length - done);
                if (n <= 0) break;
                done += n;
            }
            Decode(body);
        }

        // Body after the 2-byte packet ID.
        public MapAddPoi(byte[] body) => Decode(body);

        private enum Field { Type, Design, Pad, Active, Inverted, TypeKey, Id, Coordinates }

        // Preferred order (the client's write order). Searching in this order
        // makes the first fitting layout the one closest to it.
        private static readonly Field[] Preferred =
        {
            Field.Type, Field.Pad, Field.Active, Field.TypeKey, Field.Inverted,
            Field.Id, Field.Coordinates, Field.Design
        };

        private void Decode(byte[] body)
        {
            if (body.Length < 2) return;
            int shape = ReadShort(body, 0);
            if (shape < 0 || shape > 2) return;
            var values = new Dictionary<Field, object>();
            var order = new List<Field>();
            Dictionary<Field, object>? best = null;
            List<Field>? bestOrder = null;
            Dictionary<Field, object>? fallback = null;
            List<Field>? fallbackOrder = null;

            void Search(int pos)
            {
                if (best != null) return;
                if (order.Count == Preferred.Length)
                {
                    if (pos != body.Length) return;
                    var coords = (int[])values[Field.Coordinates];
                    bool consistent = shape == 0 ? coords.Length == 3
                        : coords.Length >= 4 && coords.Length % 2 == 0;
                    if (consistent)
                    {
                        best = new(values); bestOrder = new(order);
                    }
                    else if (fallback == null)
                    {
                        fallback = new(values); fallbackOrder = new(order);
                    }
                    return;
                }
                foreach (var field in Preferred)
                {
                    if (values.ContainsKey(field)) continue;
                    // Same-kind fields keep the client's relative order.
                    if (field == Field.Inverted && !values.ContainsKey(Field.Active)) continue;
                    if (field == Field.Id && !values.ContainsKey(Field.TypeKey)) continue;
                    if (!TryRead(body, pos, field, out object value, out int next)) continue;
                    values[field] = value;
                    order.Add(field);
                    Search(next);
                    order.RemoveAt(order.Count - 1);
                    values.Remove(field);
                    if (best != null) return;
                }
            }

            Search(2);
            var result = best ?? fallback;
            var resultOrder = bestOrder ?? fallbackOrder;
            if (result == null || resultOrder == null) return;

            Shape = shape;
            PoiType = (int)result[Field.Type];
            Design = (int)result[Field.Design];
            Active = (bool)result[Field.Active];
            Inverted = (bool)result[Field.Inverted];
            TypeKey = (string)result[Field.TypeKey];
            Id = (string)result[Field.Id];
            Coordinates = DecodeCoordinates((int[])result[Field.Coordinates]);
            Layout = "shape," + string.Join(",", resultOrder.Select(f => f.ToString()));
            Decoded = true;
        }

        private static bool TryRead(byte[] b, int pos, Field field, out object value, out int next)
        {
            value = 0; next = pos;
            switch (field)
            {
                case Field.Type:
                case Field.Design:
                    {
                        // Sub-packet: its ID, then three shorts. The client's
                        // read is (pad, pad, value); keep that, but accept the
                        // single in-range short if the third is not.
                        if (pos + 8 > b.Length) return false;
                        short expected = field == Field.Type ? PoiTypeId : PoiDesignId;
                        if (ReadShort(b, pos) != expected) return false;
                        int s1 = ReadShort(b, pos + 2), s2 = ReadShort(b, pos + 4), s3 = ReadShort(b, pos + 6);
                        int v = s3;
                        if (field == Field.Type && (s3 < 0 || s3 > 16))
                        {
                            var inRange = new[] { s1, s2 }.Where(s => s >= 0 && s <= 16).ToArray();
                            if (inRange.Length != 1) return false;
                            v = inRange[0];
                        }
                        value = v; next = pos + 8;
                        return true;
                    }
                case Field.Pad:
                    if (pos + 2 > b.Length) return false;
                    value = ReadShort(b, pos); next = pos + 2;
                    return true;
                case Field.Active:
                case Field.Inverted:
                    if (pos + 1 > b.Length || b[pos] > 1) return false;
                    value = b[pos] == 1; next = pos + 1;
                    return true;
                case Field.TypeKey:
                case Field.Id:
                    {
                        if (pos + 2 > b.Length) return false;
                        int len = (b[pos] << 8) | b[pos + 1];
                        if (len > 128 || pos + 2 + len > b.Length) return false;
                        for (int i = 0; i < len; i++)
                            if (b[pos + 2 + i] < 0x20) return false;
                        string s;
                        try { s = new UTF8Encoding(false, true).GetString(b, pos + 2, len); }
                        catch (DecoderFallbackException) { return false; }
                        value = s; next = pos + 2 + len;
                        return true;
                    }
                case Field.Coordinates:
                    {
                        if (pos + 4 > b.Length) return false;
                        int count = ReadInt(b, pos);
                        if (count < 0 || count > 512 || pos + 4 + count * 4 > b.Length) return false;
                        var raw = new int[count];
                        for (int i = 0; i < count; i++) raw[i] = ReadInt(b, pos + 4 + i * 4);
                        value = raw; next = pos + 4 + count * 4;
                        return true;
                    }
            }
            return false;
        }

        // The client writes each value as (v >>> 7) | (v << 25), so reading is
        // a 7-bit left rotation. Kept raw only if rotating gives no sane map
        // coordinates and the raw values do.
        private static int[] DecodeCoordinates(int[] raw)
        {
            var rotated = raw.Select(v => (int)((uint)v << 7 | (uint)v >> 25)).ToArray();
            static bool Sane(int[] a) => a.All(v => v >= -5000 && v <= 100000);
            return Sane(rotated) || !Sane(raw) ? rotated : raw;
        }

        private static short ReadShort(byte[] b, int pos) => (short)((b[pos] << 8) | b[pos + 1]);

        private static int ReadInt(byte[] b, int pos) =>
            (b[pos] << 24) | (b[pos + 1] << 16) | (b[pos + 2] << 8) | b[pos + 3];
    }

    // Server packet 27912: removes the zone with this id.
    public sealed class MapRemovePoi
    {
        public const short ID = 27912;
        public string Id { get; }

        public MapRemovePoi(EndianBinaryReader read)
        {
            read.ReadInt16();
            Id = read.ReadString();
        }
    }
}
