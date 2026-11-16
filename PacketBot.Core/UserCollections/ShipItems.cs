using System;
using System.Collections.Generic;
using System.Linq;
using FunnyProject.Commands.Read;

namespace FunnyProject.UserCollections
{
    // One item of the ship, as the item bar shows it. Maximum is NaN when
    // the server did not send one that could be read.
    public sealed record ShipItem(string Id, string Category, string Name, double Amount, double Maximum,
        DateTime UpdatedUtc);

    // What the ship has: ammunition, rockets, mines, extras, techs,
    // formations… with their amounts. Filled from packet 30106 (whole item
    // bar at login) and kept up to date by 12754 (one item).
    public sealed class ShipItems
    {
        private readonly object sync = new();
        private readonly Dictionary<string, ShipItem> items = new(StringComparer.Ordinal);

        public DateTime LoadedUtc { get; private set; }

        public void Update(ItemStatus status, DateTime utc)
        {
            if (!status.Decoded) return;
            var (category, name) = Describe(status.ItemId);
            lock (sync)
            {
                // Keep a known maximum when an update does not carry one.
                double max = status.Maximum;
                if (double.IsNaN(max) && items.TryGetValue(status.ItemId, out var old)) max = old.Maximum;
                items[status.ItemId] = new ShipItem(status.ItemId, category, name, status.Amount, max, utc);
            }
        }

        // Packet 30106: the whole item bar.
        public int Load(IEnumerable<ItemStatus> all, DateTime utc)
        {
            int n = 0;
            foreach (var s in all) { Update(s, utc); n++; }
            if (n > 0) LoadedUtc = utc;
            return n;
        }

        public ShipItem[] Snapshot()
        {
            lock (sync)
                return items.Values.OrderBy(i => CategoryOrder(i.Category)).ThenBy(i => i.Category)
                    .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        // /nave N (Telegram), the same on Windows and on the server.
        public string Text(string instanceName)
        {
            var all = Snapshot();
            if (all.Length == 0)
                return "🚀 " + instanceName + ": todavía no ha llegado la barra de objetos (llega al iniciar sesión).";
            var text = new System.Text.StringBuilder("🚀 Lo que tiene la nave — " + instanceName);
            foreach (var group in all.GroupBy(i => i.Category))
            {
                text.Append("\n\n").Append(group.Key).Append(':');
                foreach (var i in group)
                    text.Append("\n• ").Append(i.Name).Append(": ").Append(Amount(i));
            }
            return text.ToString();
        }

        public static string Amount(ShipItem i) =>
            i.Amount.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("es-ES")) +
            (double.IsNaN(i.Maximum) || i.Maximum <= 0 || i.Maximum >= 99999 ? ""
                : " / " + i.Maximum.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("es-ES")));

        public bool TryGet(string id, out ShipItem item)
        {
            lock (sync) return items.TryGetValue(id, out item!);
        }

        // "ammunition_laser_ucb-100" -> ("Munición láser", "UCB-100").
        public static (string Category, string Name) Describe(string id)
        {
            foreach (var (prefix, category) in Categories)
                if (id.StartsWith(prefix, StringComparison.Ordinal))
                    return (category, id.Substring(prefix.Length).ToUpperInvariant());
            int cut = id.IndexOf('_');
            return cut > 0 ? (Capitalize(id[..cut]), id[(cut + 1)..]) : ("Otros", id);
        }

        private static readonly (string Prefix, string Category)[] Categories =
        {
            ("ammunition_laser_", "Munición láser"),
            ("ammunition_rocketlauncher_", "Lanzacohetes"),
            ("ammunition_rocket_", "Cohetes"),
            ("ammunition_mine_", "Minas"),
            ("ammunition_specialammo_", "Munición especial"),
            ("ammunition_", "Munición"),
            ("equipment_extra_cpu_", "CPU"),
            ("equipment_extra_", "Extras"),
            ("equipment_weapon_", "Armas"),
            ("equipment_", "Equipo"),
            ("tech_", "Techs"),
            ("ability_", "Habilidades"),
            ("drone_formation_", "Formaciones"),
            ("ship_", "Naves y diseños"),
        };

        private static int CategoryOrder(string category)
        {
            int i = Array.FindIndex(Categories, c => c.Category == category);
            return i < 0 ? Categories.Length : i;
        }

        private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
    }
}
