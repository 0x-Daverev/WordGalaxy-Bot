using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FunnyProject.Commands.Read;

namespace FunnyProject.UserCollections
{
    public sealed record Station(string Name, int X, int Y, int FactionId, int Type)
    {
        // Minimap label: "HQ", "R" (RepairDock), "H" (Hangar)…
        public string Short => Name.Length <= 2 ? Name.ToUpperInvariant() : Name[..1].ToUpperInvariant();
    }

    // Stations of the map (bases, repair docks, hangars…) from packet 13718.
    //  - Current: what the server has sent for the map the ship is on;
    //    cleared when the ship enters another map.
    //  - Known: every station seen, per map, saved in
    //    <LocalAppData>/WordGalaxyPacketBot/maps/stations.json (shared by
    //    all accounts and by Windows and the server), so the bot knows where
    //    a base is before the server sends it.
    public sealed class Stations
    {
        public static string File { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WordGalaxyPacketBot", "maps", "stations.json");

        private static readonly object FileSync = new();
        private readonly object sync = new();
        private readonly Dictionary<string, Station> current = new();
        private Dictionary<int, List<Station>>? known;

        private static string Key(Station s) => s.Name + "|" + s.X + "|" + s.Y;

        // Packet 13718 handler.
        public Station? OnServerStation(MapStation packet, int mapId)
        {
            if (!packet.Decoded || string.IsNullOrWhiteSpace(packet.Name)) return null;
            var station = new Station(packet.Name, packet.X, packet.Y, packet.FactionId, packet.Type);
            lock (sync) current[Key(station)] = station;
            if (mapId > 0) Remember(mapId, station);
            return station;
        }

        public void Clear() { lock (sync) current.Clear(); }

        public Station[] Current() { lock (sync) return current.Values.ToArray(); }

        // What the ship knows of this map: the current ones plus the saved ones.
        public Station[] For(int mapId)
        {
            var list = Current().ToDictionary(Key);
            foreach (var s in Known(mapId)) list.TryAdd(Key(s), s);
            return list.Values.OrderBy(s => s.Name).ThenBy(s => s.X).ToArray();
        }

        public IReadOnlyList<Station> Known(int mapId)
        {
            lock (FileSync)
            {
                known ??= Load();
                return known.TryGetValue(mapId, out var list) ? list.ToArray() : Array.Empty<Station>();
            }
        }

        private void Remember(int mapId, Station station)
        {
            lock (FileSync)
            {
                known = Load(); // other instances may have written it
                if (!known.TryGetValue(mapId, out var list)) known[mapId] = list = new List<Station>();
                int i = list.FindIndex(s => Key(s) == Key(station));
                if (i >= 0 && list[i] == station) return;
                if (i >= 0) list[i] = station; else list.Add(station);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(File)!);
                    string temp = File + ".tmp";
                    System.IO.File.WriteAllText(temp, JsonSerializer.Serialize(known, new JsonSerializerOptions { WriteIndented = true }));
                    System.IO.File.Move(temp, File, overwrite: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }

        private static Dictionary<int, List<Station>> Load()
        {
            try
            {
                if (System.IO.File.Exists(File))
                    return JsonSerializer.Deserialize<Dictionary<int, List<Station>>>(System.IO.File.ReadAllText(File)) ?? new();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
            return new();
        }
    }
}
