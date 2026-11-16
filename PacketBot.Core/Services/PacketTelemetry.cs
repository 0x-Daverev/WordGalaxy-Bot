using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace FunnyProject.Services
{
    // What the server sends that the bot does not handle, to decide what to
    // implement next (tools/protocol_catalog.py reads these files).
    //  - Unhandled binary packets: count, sizes, maps and up to 3 samples.
    //  - Text commands (4224): only their keys, e.g. "n|pvp", never values
    //    (they can carry player names).
    // Never contains the PHPSESSID, the login packet or anything the bot sends.
    // Saved to <LocalAppData>/WordGalaxyPacketBot/telemetry/<date>-<userId>.json
    // at most once a minute and when the connection closes; a file of the
    // same day is merged.
    public sealed class PacketTelemetry
    {
        public const int MaxSamples = 3;
        public const int MaxSampleBytes = 512;
        public static readonly TimeSpan SaveEvery = TimeSpan.FromMinutes(1);

        public sealed class PacketEntry
        {
            public int Id { get; set; }
            public long Count { get; set; }
            public int MinBytes { get; set; } = int.MaxValue;
            public int MaxBytes { get; set; }
            public Dictionary<int, long> Maps { get; set; } = new();
            public List<string> Samples { get; set; } = new();
        }

        public sealed class Report
        {
            public DateTime FirstUtc { get; set; }
            public DateTime LastUtc { get; set; }
            public Dictionary<int, PacketEntry> Packets { get; set; } = new();
            public Dictionary<string, long> TextKeys { get; set; } = new();
        }

        private readonly object sync = new();
        private Report report = new();
        private DateTime lastSaveUtc = DateTime.UtcNow;
        private bool dirty;

        // Folder of the files; tests point it elsewhere.
        public static string Folder { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WordGalaxyPacketBot", "telemetry");
        internal Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;
        public int UserId { get; set; }

        public void RecordUnknown(int id, byte[] body, int mapId)
        {
            var now = Clock();
            lock (sync)
            {
                Touch(now);
                if (!report.Packets.TryGetValue(id, out var e))
                    report.Packets[id] = e = new PacketEntry { Id = id };
                e.Count++;
                e.MinBytes = Math.Min(e.MinBytes, body.Length);
                e.MaxBytes = Math.Max(e.MaxBytes, body.Length);
                e.Maps[mapId] = e.Maps.TryGetValue(mapId, out long n) ? n + 1 : 1;
                if (e.Samples.Count < MaxSamples)
                    e.Samples.Add(Convert.ToHexString(body, 0, Math.Min(body.Length, MaxSampleBytes)));
            }
            SaveIfDue(now);
        }

        public void RecordText(string message)
        {
            var now = Clock();
            string? key = TextKey(message);
            if (key == null) return;
            lock (sync)
            {
                Touch(now);
                report.TextKeys[key] = report.TextKeys.TryGetValue(key, out long n) ? n + 1 : 1;
            }
            SaveIfDue(now);
        }

        // "0|n|pvp|1234|0" -> "n|pvp"; "0|A|STD|text" -> "A|STD"; "0|URI|..." -> "URI".
        // The second word is kept only if it looks like a keyword (letters,
        // digits, _ or -, up to 16), so names and numbers are never stored.
        public static string? TextKey(string message)
        {
            var parts = (message ?? "").Split('|');
            int first = parts.Length > 1 && parts[0] == "0" ? 1 : 0;
            if (parts.Length <= first || !IsKeyword(parts[first])) return null;
            string key = parts[first];
            if (parts.Length > first + 1 && IsKeyword(parts[first + 1]) && !parts[first + 1].All(char.IsDigit))
                key += "|" + parts[first + 1];
            return key;
        }

        private static bool IsKeyword(string s) =>
            s.Length is > 0 and <= 16 && s.All(c => char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-');

        private void Touch(DateTime now)
        {
            if (report.FirstUtc == default) report.FirstUtc = now;
            report.LastUtc = now;
            dirty = true;
        }

        public Report Snapshot()
        {
            lock (sync)
                return JsonSerializer.Deserialize<Report>(JsonSerializer.Serialize(report))!;
        }

        private void SaveIfDue(DateTime now)
        {
            if (now - lastSaveUtc < SaveEvery) return;
            lastSaveUtc = now;
            _ = Task.Run(Save);
        }

        // Merges what was recorded into today's file and starts counting again.
        public void Save()
        {
            Report pending;
            lock (sync)
            {
                if (!dirty) return;
                pending = report;
                report = new Report();
                dirty = false;
            }
            try
            {
                Directory.CreateDirectory(Folder);
                string path = Path.Combine(Folder, pending.FirstUtc.ToString("yyyy-MM-dd") + "-" + UserId + ".json");
                lock (FileSync)
                {
                    var merged = File.Exists(path)
                        ? JsonSerializer.Deserialize<Report>(File.ReadAllText(path)) ?? new Report()
                        : new Report();
                    Merge(merged, pending);
                    string temp = path + ".tmp";
                    File.WriteAllText(temp, JsonSerializer.Serialize(merged, new JsonSerializerOptions { WriteIndented = true }));
                    File.Move(temp, path, overwrite: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // Telemetry must never disturb the bot.
            }
        }

        private static readonly object FileSync = new();

        internal static void Merge(Report into, Report add)
        {
            if (into.FirstUtc == default || (add.FirstUtc != default && add.FirstUtc < into.FirstUtc))
                into.FirstUtc = add.FirstUtc;
            if (add.LastUtc > into.LastUtc) into.LastUtc = add.LastUtc;
            foreach (var (id, e) in add.Packets)
            {
                if (!into.Packets.TryGetValue(id, out var t)) { into.Packets[id] = e; continue; }
                t.Count += e.Count;
                t.MinBytes = Math.Min(t.MinBytes, e.MinBytes);
                t.MaxBytes = Math.Max(t.MaxBytes, e.MaxBytes);
                foreach (var (map, n) in e.Maps) t.Maps[map] = t.Maps.TryGetValue(map, out long m) ? m + n : n;
                foreach (var s in e.Samples)
                    if (t.Samples.Count < MaxSamples && !t.Samples.Contains(s)) t.Samples.Add(s);
            }
            foreach (var (key, n) in add.TextKeys)
                into.TextKeys[key] = into.TextKeys.TryGetValue(key, out long m) ? m + n : n;
        }
    }
}
