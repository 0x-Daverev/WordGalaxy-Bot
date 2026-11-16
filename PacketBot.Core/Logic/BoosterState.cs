using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace FunnyProject.Logic
{
    // Passive snapshot received from the game's custom WG_BOOSTERS protocol.
    // It is not the legacy BoosterStation effect packet.
    public sealed class BoosterState
    {
        public sealed record PersonalBoost(string Name, string RawTypeName, string DisplayTypeName,
            double Percent, long RemainingSeconds, long ExpiresUnix, string TimerMode, bool Active);

        public sealed record Category(int Attribute, double TotalPercent, double PersonalPercent,
            double SharedPercent, double ClanBasePercent, IReadOnlyList<PersonalBoost> Personal);

        private readonly object sync = new();
        private Category[] categories = Array.Empty<Category>();
        private DateTime receivedUtc = DateTime.MinValue;
        private long serverSampleUnix;
        private long serverCompletedUnix;
        private int? transportPacketId;

        public static readonly string[] Names = {
            "Experiencia", "Honor", "Daño", "Escudo", "Reparación",
            "Recarga escudo", "Recursos", "PV máximos", "Recarga habilidad"
        };

        public DateTime LastUpdatedUtc { get { lock (sync) return receivedUtc; } }
        public int? TransportPacketId { get { lock (sync) return transportPacketId; } }

        public void Clear()
        {
            lock (sync)
            {
                categories = Array.Empty<Category>();
                receivedUtc = DateTime.MinValue;
                transportPacketId = null;
                serverSampleUnix = serverCompletedUnix = 0;
            }
        }

        // Return true only for a valid booster message. Invalid or partial
        // messages never replace the last known snapshot.
        public bool TryReceive(string message, int packetId, int expectedAccountId = 0)
        {
            const string prefix = "0|WG_BOOSTERS|v1|";
            if (string.IsNullOrEmpty(message) || !message.StartsWith(prefix, StringComparison.Ordinal)
                || message.Length > 60000) return false;
            try
            {
                string json = Uri.UnescapeDataString(message.Substring(prefix.Length));
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.GetProperty("version").GetInt32() != 1 ||
                    !root.GetProperty("ok").GetBoolean()) return false;
                int accountId = root.GetProperty("accountId").GetInt32();
                if (accountId <= 0 || (expectedAccountId > 0 && accountId != expectedAccountId))
                    return false;
                var array = root.GetProperty("categories");
                if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() != 9) return false;
                long sample = root.GetProperty("serverSampleUnix").GetInt64();
                long completed = root.GetProperty("serverCompletedUnix").GetInt64();
                if (sample <= 0 || completed < sample) return false;
                var parsed = new List<Category>(9);
                var seen = new HashSet<int>();
                foreach (var item in array.EnumerateArray())
                {
                    int attribute = item.GetProperty("attribute").GetInt32();
                    if (attribute < 0 || attribute >= Names.Length || !seen.Add(attribute)) return false;
                    double total = item.GetProperty("totalPercent").GetDouble();
                    double personalPercent = item.GetProperty("personalPercent").GetDouble();
                    double shared = item.GetProperty("shared").GetProperty("percent").GetDouble();
                    double clan = item.GetProperty("cbs").GetProperty("percent").GetDouble();
                    if (!ValidPercent(total) || !ValidPercent(personalPercent) ||
                        !ValidPercent(shared) || !ValidPercent(clan)) return false;
                    var personal = new List<PersonalBoost>();
                    foreach (var boost in item.GetProperty("personal").EnumerateArray())
                    {
                        double percent = boost.GetProperty("percent").GetDouble();
                        long remaining = boost.GetProperty("remainingSeconds").GetInt64();
                        long expires = boost.GetProperty("expiresUnix").GetInt64();
                        if (!ValidPercent(percent) || remaining < 0 || expires < 0 ||
                            personal.Count >= 128) return false;
                        string rawTypeName = boost.GetProperty("rawTypeName").GetString() ?? "";
                        string displayTypeName = boost.GetProperty("displayTypeName").GetString() ?? "";
                        string label = boost.TryGetProperty("custom", out var custom) &&
                            custom.ValueKind == JsonValueKind.True
                            ? displayTypeName : rawTypeName;
                        string timerMode = boost.GetProperty("timerMode").GetString() ?? "";
                        if (timerMode != "wall_clock" && timerMode != "online_tick") return false;
                        personal.Add(new PersonalBoost(label, rawTypeName, displayTypeName, percent,
                            remaining, expires, timerMode, boost.GetProperty("active").GetBoolean()));
                    }
                    parsed.Add(new Category(attribute, total, personalPercent, shared, clan, personal));
                }
                lock (sync)
                {
                    if (sample < serverSampleUnix) return false;
                    categories = parsed.OrderBy(x => x.Attribute).ToArray();
                    receivedUtc = DateTime.UtcNow;
                    serverSampleUnix = sample;
                    serverCompletedUnix = completed;
                    transportPacketId = packetId;
                }
                return true;
            }
            catch (Exception ex) when (ex is JsonException || ex is FormatException ||
                                       ex is InvalidOperationException || ex is KeyNotFoundException ||
                                       ex is ArgumentException || ex is OverflowException)
            {
                return false;
            }
        }

        // Only exact booster name matches can authorize a purchase. A category
        // total includes group and clan effects and cannot identify the item.
        public bool TryGetRemainingSeconds(string boosterCode, int category, out long seconds)
        {
            seconds = 0;
            lock (sync)
            {
                if (receivedUtc == DateTime.MinValue ||
                    DateTime.UtcNow - receivedUtc > TimeSpan.FromSeconds(75))
                    return false;
                var matching = categories.Where(x => x.Attribute == category)
                    .SelectMany(x => x.Personal)
                    .Where(p => p.Active &&
                        (MatchBoosterName(p.RawTypeName, boosterCode) ||
                         MatchBoosterName(p.DisplayTypeName, boosterCode)))
                    .ToArray();
                if (matching.Length == 0) return false;
                long age = Math.Max(0, (long)(DateTime.UtcNow - receivedUtc).TotalSeconds);
                // Multiple instances of the same code: use the longest remaining one.
                // A shorter instance must not trigger an unnecessary purchase.
                seconds = matching.Max(p => p.TimerMode == "wall_clock"
                    ? Math.Max(0, p.RemainingSeconds -
                        Math.Max(0, serverCompletedUnix - serverSampleUnix) - age)
                    : p.RemainingSeconds);
                return true;
            }
        }

        private static bool MatchBoosterName(string name, string code) =>
            string.Equals(name.Replace("-", "_"), code, StringComparison.OrdinalIgnoreCase);

        private static bool ValidPercent(double p) => double.IsFinite(p) && p >= 0 && p <= 100000;

        public string Summary()
        {
            lock (sync)
            {
                if (receivedUtc == DateTime.MinValue)
                    return "Boosters: esperando respuesta WG_BOOSTERS";
                var age = DateTime.UtcNow - receivedUtc;
                string status = age > TimeSpan.FromSeconds(75) ? " (datos antiguos)" : "";
                var active = categories.Where(x => x.TotalPercent > 0).ToArray();
                if (active.Length == 0) return "Boosters: ninguno activo" + status;
                var lines = new List<string> { "Boosters" + status + " [TCP " + transportPacketId + "]" };
                foreach (var c in active)
                {
                    lines.Add(Names[c.Attribute] + ": +" +
                        c.TotalPercent.ToString("0.##") + "%");
                    if (c.SharedPercent > 0)
                        lines.Add("  Grupo: +" + c.SharedPercent.ToString("0.##") + "%");
                    if (c.ClanBasePercent > 0)
                        lines.Add("  Base: +" + c.ClanBasePercent.ToString("0.##") + "%");
                    foreach (var p in c.Personal.Where(x => x.Active))
                    {
                        long left = p.TimerMode == "wall_clock"
                            ? Math.Max(0, p.RemainingSeconds -
                                Math.Max(0, serverCompletedUnix - serverSampleUnix) -
                                (long)age.TotalSeconds)
                            : p.RemainingSeconds;
                        string duration = TimeSpan.FromSeconds(left).ToString(@"d\.hh\:mm\:ss");
                        lines.Add("  " + (string.IsNullOrWhiteSpace(p.Name) ? "Propio" : p.Name) +
                            " +" + p.Percent.ToString("0.##") + "%: " + duration +
                            (p.TimerMode == "online_tick" ? " (tiempo online)" : ""));
                    }
                }
                return string.Join(Environment.NewLine, lines);
            }
        }
    }
}
