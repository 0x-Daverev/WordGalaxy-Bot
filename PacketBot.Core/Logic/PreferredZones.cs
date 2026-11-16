using System;
using System.Collections.Generic;
using System.Linq;
using FunnyProject.Navigation;
using FunnyProject.UserCollections;

namespace FunnyProject.Logic
{
    // A preferred (not mandatory) NPC Killer work area, in game coordinates.
    public sealed class PreferredZone
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }

        public bool Contains(double x, double y) =>
            x >= X && x <= X + Width && y >= Y && y <= Y + Height;

        public PreferredZone Copy() => new() { X = X, Y = Y, Width = Width, Height = Height };
    }

    // Preferred zones of NPC Killer, saved per map in
    // BotSettings.NpcKillerZonesByMap. Shared by the Windows window, the
    // server, the web panel and Telegram. With zones on a map, NPC Killer
    // roams inside them when it sees no target and prefers targets inside
    // them; it still attacks targets outside when there are none inside.
    internal static class PreferredZones
    {
        public const int MaxPerMap = 12;
        public const int MinSize = 300;
        // Roam points keep this distance from the zone's edges.
        private const int Inset = 150;

        public static List<PreferredZone> For(BotSettings s, int mapId) =>
            s.NpcKillerZonesByMap != null && s.NpcKillerZonesByMap.TryGetValue(mapId, out var list)
                ? list.Select(z => z.Copy()).ToList()
                : new List<PreferredZone>();

        private static (int W, int H) MapSize(int mapId) =>
            MapGraph.Default.Maps.TryGetValue(mapId, out var m) ? (m.width, m.height) : (21000, 13100);

        // Adds the rectangle between two corners, clamped to the map. Returns
        // the reply; Added is false when the zone is rejected.
        public static (bool Added, string Reply) Add(BotSettings s, int mapId, int x1, int y1, int x2, int y2)
        {
            if (mapId <= 0) return (false, "Mapa desconocido: espera a que la nave entre en un mapa.");
            var (w, h) = MapSize(mapId);
            int left = Math.Clamp(Math.Min(x1, x2), 0, w), right = Math.Clamp(Math.Max(x1, x2), 0, w);
            int top = Math.Clamp(Math.Min(y1, y2), 0, h), bottom = Math.Clamp(Math.Max(y1, y2), 0, h);
            if (right - left < MinSize || bottom - top < MinSize)
                return (false, "Zona demasiado pequena (minimo " + MinSize + "x" + MinSize + ").");
            s.NpcKillerZonesByMap ??= new Dictionary<int, List<PreferredZone>>();
            if (!s.NpcKillerZonesByMap.TryGetValue(mapId, out var list))
                s.NpcKillerZonesByMap[mapId] = list = new List<PreferredZone>();
            if (list.Count >= MaxPerMap)
                return (false, "Maximo " + MaxPerMap + " zonas por mapa. Borra alguna antes.");
            list.Add(new PreferredZone { X = left, Y = top, Width = right - left, Height = bottom - top });
            return (true, "Zona " + list.Count + " anadida en " + MapName(mapId) + ".");
        }

        // Removes the last-drawn zone containing the point (the one on top).
        public static (bool Removed, string Reply) RemoveAt(BotSettings s, int mapId, int x, int y)
        {
            if (s.NpcKillerZonesByMap == null || !s.NpcKillerZonesByMap.TryGetValue(mapId, out var list))
                return (false, "No hay zonas en ese punto.");
            int index = list.FindLastIndex(z => z.Contains(x, y));
            if (index < 0) return (false, "No hay zonas en ese punto.");
            list.RemoveAt(index);
            return (true, "Zona " + (index + 1) + " borrada de " + MapName(mapId) + ".");
        }

        public static string Clear(BotSettings s, int mapId)
        {
            int count = s.NpcKillerZonesByMap != null && s.NpcKillerZonesByMap.Remove(mapId, out var list) ? list.Count : 0;
            return count == 0 ? "No habia zonas en " + MapName(mapId) + "."
                : "Borradas " + count + " zonas de " + MapName(mapId) + ".";
        }

        // /zonas N: zones of the NPC Killer map.
        public static string Status(BotSettings s, string instanceName, int n)
        {
            int mapId = s.NpcKillerMapId;
            if (mapId <= 0) return "Selecciona primero el mapa de NPC Killer.";
            var list = For(s, mapId);
            string text = "📍 Zonas preferidas de NPC Killer — " + instanceName + " (" + MapName(mapId) + ")";
            if (list.Count == 0)
                return text + "\nNinguna: busca NPCs por todo el mapa." +
                    "\nSe dibujan arrastrando en el minimapa (Windows o panel web) con \"Dibujar zonas\" activado.";
            for (int i = 0; i < list.Count; i++)
                text += "\n" + (i + 1) + ". (" + list[i].X + ", " + list[i].Y + ") " + list[i].Width + "x" + list[i].Height;
            return text + "\nBorrar: /zona " + n + " borrar <numero> o /zona " + n + " borrar todas.";
        }

        // /zona N borrar <numero|todas>, on the NPC Killer map.
        public static (Action<BotSettings>? Change, string Reply) ParseCommand(BotSettings current, string argument)
        {
            var parts = (argument ?? "").Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            const string usage = "Uso: /zona N borrar <numero de /zonas N> o /zona N borrar todas.";
            if (parts.Length != 2 || parts[0] != "borrar") return (null, usage);
            int mapId = current.NpcKillerMapId;
            if (mapId <= 0) return (null, "Selecciona primero el mapa de NPC Killer.");
            int count = For(current, mapId).Count;
            if (parts[1] is "todas" or "todo" or "todos")
                return (s => Clear(s, mapId), count == 0
                    ? "No habia zonas en " + MapName(mapId) + "."
                    : "Borradas " + count + " zonas de " + MapName(mapId) + ".");
            if (!int.TryParse(parts[1], out int number) || number < 1 || number > count)
                return (null, count == 0 ? "No hay zonas en " + MapName(mapId) + "." : usage);
            return (s => s.NpcKillerZonesByMap![mapId].RemoveAt(number - 1),
                "Zona " + number + " borrada de " + MapName(mapId) + ".");
        }

        public static string MapName(int mapId) =>
            MapGraph.Default.Maps.TryGetValue(mapId, out var m) ? m.name : mapId.ToString();

        // ---- NPC Killer behaviour ----

        public static bool InAny(IReadOnlyList<PreferredZone> zones, double x, double y) =>
            zones.Any(z => z.Contains(x, y));

        // A free point in the zone nearest to (x, y): where to go back to
        // after a kill outside the zones.
        public static (int X, int Y)? ReturnPoint(IReadOnlyList<PreferredZone> zones, MapZones barriers,
            Random random, int x, int y)
        {
            if (zones.Count == 0) return null;
            static double Distance(PreferredZone z, int px, int py)
            {
                double dx = Math.Max(Math.Max(z.X - px, 0), px - (z.X + z.Width));
                double dy = Math.Max(Math.Max(z.Y - py, 0), py - (z.Y + z.Height));
                return dx * dx + dy * dy;
            }
            var nearest = zones.OrderBy(z => Distance(z, x, y)).First();
            return RoamPoint(new[] { nearest }, barriers, random) ?? RoamPoint(zones, barriers, random);
        }

        // A random free point inside a zone (bigger zones are picked more
        // often), or null when there are no zones or no free point was found.
        public static (int X, int Y)? RoamPoint(IReadOnlyList<PreferredZone> zones, MapZones barriers, Random random)
        {
            if (zones.Count == 0) return null;
            double total = zones.Sum(z => (double)z.Width * z.Height);
            for (int attempt = 0; attempt < 20; attempt++)
            {
                double pick = random.NextDouble() * total;
                var zone = zones[^1];
                foreach (var z in zones)
                {
                    pick -= (double)z.Width * z.Height;
                    if (pick <= 0) { zone = z; break; }
                }
                int inset = Math.Min(Inset, Math.Min(zone.Width, zone.Height) / 4);
                int x = random.Next(zone.X + inset, zone.X + zone.Width - inset + 1);
                int y = random.Next(zone.Y + inset, zone.Y + zone.Height - inset + 1);
                if (!barriers.IsBlocked(x, y)) return (x, y);
            }
            return null;
        }
    }
}

namespace FunnyProject.Logic
{
    public sealed record PreferredZoneReply(bool Ok, string Message);
}
