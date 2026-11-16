using FunnyProject.Commands.Read;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FunnyProject.UserCollections
{
    public enum PoiShape { Circle = 0, Polygon = 1, Rectangle = 2 }

    // One map zone from packet 7240. Only NoAccess zones (types 5 and 6 in the
    // client's table) block the ship. Like the client, a polygon or rectangle
    // collides as its axis-aligned bounding box, not as the polygon itself.
    public sealed class PoiZone
    {
        public string Id { get; }
        public int Type { get; }
        public PoiShape Shape { get; }
        public bool Active { get; }
        // Inverted: the ship is kept inside; a band of Margin around the
        // outside is blocked instead of the zone itself.
        public bool Inverted { get; }
        public IReadOnlyList<(int X, int Y)> Points { get; }
        public int Radius { get; }
        public int Left { get; }
        public int Top { get; }
        public int Width { get; }
        public int Height { get; }

        public bool Blocks => Type == 5 || Type == 6;
        // Client: 384 for circles, 512 for rectangles and polygons.
        public int Margin => Shape == PoiShape.Circle ? 384 : 512;
        public int CenterX => Shape == PoiShape.Circle ? Points[0].X : Left + Width / 2;
        public int CenterY => Shape == PoiShape.Circle ? Points[0].Y : Top + Height / 2;

        public PoiZone(string id, int type, PoiShape shape, bool active, bool inverted,
            IReadOnlyList<(int X, int Y)> points, int radius)
        {
            if (points.Count == 0) throw new ArgumentException("A zone needs at least one point.", nameof(points));
            Id = id; Type = type; Shape = shape; Active = active; Inverted = inverted;
            Points = points; Radius = radius;
            if (shape == PoiShape.Circle)
            {
                Left = points[0].X - radius; Top = points[0].Y - radius;
                Width = Height = radius * 2;
            }
            else
            {
                Left = points.Min(p => p.X); Top = points.Min(p => p.Y);
                Width = points.Max(p => p.X) - Left; Height = points.Max(p => p.Y) - Top;
            }
        }

        // From a decoded 7240. Coordinates are x,y pairs (circle: x, y, radius).
        // If the pairs only fit the map swapped, they are read as y,x.
        public static PoiZone? From(MapAddPoi poi, int mapWidth = 21000, int mapHeight = 13100)
        {
            if (!poi.Decoded) return null;
            var c = poi.Coordinates;
            var shape = (PoiShape)poi.Shape;
            if (shape == PoiShape.Circle)
            {
                if (c.Length < 3 || c[2] <= 0) return null;
                var center = Fit(new[] { (c[0], c[1]) }, mapWidth, mapHeight);
                return new PoiZone(poi.Id, poi.PoiType, shape, poi.Active, poi.Inverted, center, c[2]);
            }
            if (c.Length < 4) return null;
            var pairs = new List<(int, int)>();
            for (int i = 0; i + 1 < c.Length; i += 2) pairs.Add((c[i], c[i + 1]));
            return new PoiZone(poi.Id, poi.PoiType, shape, poi.Active, poi.Inverted,
                Fit(pairs, mapWidth, mapHeight), 0);
        }

        private static (int X, int Y)[] Fit(IList<(int, int)> pairs, int w, int h)
        {
            const int slack = 1000;
            bool Inside(int x, int y) => x >= -slack && x <= w + slack && y >= -slack && y <= h + slack;
            bool straight = pairs.All(p => Inside(p.Item1, p.Item2));
            bool swapped = pairs.All(p => Inside(p.Item2, p.Item1));
            return !straight && swapped
                ? pairs.Select(p => (p.Item2, p.Item1)).ToArray()
                : pairs.Select(p => (p.Item1, p.Item2)).ToArray();
        }

        // Client contains(): is this point blocked by the zone?
        public bool Contains(double x, double y)
        {
            if (Shape == PoiShape.Circle)
            {
                double d = Sq(x - Points[0].X) + Sq(y - Points[0].Y);
                return Inverted
                    ? d > Sq(Radius) && d < Sq(Radius + Margin)
                    : d < Sq(Radius);
            }
            bool inside = InRect(x, y, Left, Top, Width, Height);
            if (!Inverted) return inside;
            return !inside && InRect(x, y, Left - Margin, Top - Margin, Width + 2 * Margin, Height + 2 * Margin);
        }

        // Client hitTest(): first point where from->to enters the zone, with
        // the push the client applies (40 units back toward the start side).
        internal bool HitTest(double fx, double fy, double tx, double ty, out Hit hit)
        {
            hit = default;
            if (Contains(fx, fy)) return false; // the client lets a ship leave
            var hits = new List<Hit>();
            if (Shape == PoiShape.Circle)
            {
                int cx = Points[0].X, cy = Points[0].Y;
                if (Inverted)
                {
                    AddCircle(hits, fx, fy, tx, ty, cx, cy, Radius, exit: true);
                    AddCircle(hits, fx, fy, tx, ty, cx, cy, Radius + Margin, exit: false);
                }
                else AddCircle(hits, fx, fy, tx, ty, cx, cy, Radius, exit: false);
            }
            else
            {
                AddRect(hits, fx, fy, tx, ty, Left, Top, Width, Height);
                if (Inverted)
                    AddRect(hits, fx, fy, tx, ty, Left - Margin, Top - Margin, Width + 2 * Margin, Height + 2 * Margin);
            }
            if (hits.Count == 0) return false;
            hit = hits.OrderBy(h => h.Distance).First();
            return true;
        }

        private static void AddRect(List<Hit> hits, double fx, double fy, double tx, double ty,
            double l, double t, double w, double h)
        {
            AddEdge(hits, fx, fy, tx, ty, l, t, l + w, t);
            AddEdge(hits, fx, fy, tx, ty, l + w, t, l + w, t + h);
            AddEdge(hits, fx, fy, tx, ty, l + w, t + h, l, t + h);
            AddEdge(hits, fx, fy, tx, ty, l, t + h, l, t);
        }

        private static void AddEdge(List<Hit> hits, double x1, double y1, double x2, double y2,
            double x3, double y3, double x4, double y4)
        {
            double dx = x2 - x1, dy = y2 - y1, ex = x4 - x3, ey = y4 - y3;
            double den = dx * ey - dy * ex;
            if (den == 0) return;
            double ax = x3 - x1, ay = y3 - y1;
            double u = (ax * ey - ay * ex) / den;
            double v = (ax * dy - ay * dx) / den;
            if (u < 0 || u > 1 || v < 0 || v > 1) return;
            double hx = x1 + dx * u, hy = y1 + dy * u;
            // Unit normal of the edge, pointing to the side the ship comes from.
            double nx = ey, ny = -ex, len = Math.Sqrt(nx * nx + ny * ny);
            nx /= len; ny /= len;
            if (nx * (x1 - hx) + ny * (y1 - hy) < 0) { nx = -nx; ny = -ny; }
            hits.Add(new Hit(hx, hy, Sq(hx - x1) + Sq(hy - y1), nx, ny));
        }

        private static void AddCircle(List<Hit> hits, double fx, double fy, double tx, double ty,
            double cx, double cy, double r, bool exit)
        {
            double dx = tx - fx, dy = ty - fy;
            double a = dx * dx + dy * dy;
            if (a == 0) return;
            double b = 2 * (dx * (fx - cx) + dy * (fy - cy));
            double c = Sq(fx - cx) + Sq(fy - cy) - r * r;
            double disc = b * b - 4 * a * c;
            if (disc <= 0) return;
            double sq = Math.Sqrt(disc);
            double t = exit ? (-b + sq) / (2 * a) : (-b - sq) / (2 * a);
            if (t < 0 || t > 1) return;
            double hx = fx + dx * t, hy = fy + dy * t;
            double nx = hx - cx, ny = hy - cy, len = Math.Sqrt(nx * nx + ny * ny);
            if (len == 0) return;
            nx /= len; ny /= len;
            if (exit) { nx = -nx; ny = -ny; } // push back inside the circle
            hits.Add(new Hit(hx, hy, Sq(hx - fx) + Sq(hy - fy), nx, ny));
        }

        private static bool InRect(double x, double y, double l, double t, double w, double h) =>
            x >= l && x < l + w && y >= t && y < t + h;

        private static double Sq(double v) => v * v;

        internal readonly record struct Hit(double X, double Y, double Distance, double NormalX, double NormalY);
    }

    // Zones of the current map. Filled from packets 7240 / 27912 and cleared
    // when the hero enters another map.
    public sealed class MapZones
    {
        // Same as the client: stop 40 units before a barrier.
        public const int Push = 40;
        // Detour corners keep this much distance from a barrier.
        public const int Clearance = 80;

        private readonly object sync = new();
        private readonly Dictionary<string, PoiZone> zones = new();

        // Size of the current map: detour corners must lie inside it.
        public int MapWidth { get; set; } = 21000;
        public int MapHeight { get; set; } = 13100;

        // This server sends its zones with an empty id, and sends them all
        // again when the ship respawns on the same map. A zone without id is
        // keyed by what it is (type, shape, points), so a repeat replaces it
        // instead of piling up.
        public void Add(PoiZone zone)
        {
            lock (sync) zones[Key(zone)] = zone;
        }

        internal static string Key(PoiZone z) => z.Id.Length > 0 ? z.Id
            : "#" + z.Type + "|" + (int)z.Shape + "|" + (z.Inverted ? 1 : 0) + "|" + z.Radius + "|" +
              string.Join(";", z.Points.Select(p => p.X + "," + p.Y));

        // A zone without id cannot be told apart from the others: keep them.
        public bool Remove(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            lock (sync) return zones.Remove(id);
        }

        public void Clear() { lock (sync) zones.Clear(); }
        public PoiZone[] Snapshot() { lock (sync) return zones.Values.ToArray(); }
        public PoiZone[] Barriers() { lock (sync) return zones.Values.Where(z => z.Blocks).ToArray(); }

        public bool IsBlocked(double x, double y) => Barriers().Any(z => z.Contains(x, y));

        // Packet 7240 handler. Undecodable packets and zones without valid
        // coordinates are ignored.
        public PoiZone? OnServerZone(MapAddPoi poi, int mapId)
        {
            if (!poi.Decoded) return null;
            int w = 21000, h = 13100;
            if (FunnyProject.Navigation.MapGraph.Default.Maps.TryGetValue(mapId, out var map))
            { w = map.width; h = map.height; }
            MapWidth = w; MapHeight = h;
            var zone = PoiZone.From(poi, w, h);
            if (zone != null) Add(zone);
            return zone;
        }

        // Where the game really stops a straight move from -> to (the client
        // clamps the destination before sending it to the server).
        public (int X, int Y) ClampMove(int fromX, int fromY, int toX, int toY) =>
            Clamp(Barriers(), fromX, fromY, toX, toY);

        // Destination to send for a move to (toX, toY): the target itself if
        // the straight path is free; otherwise the first corner of a detour
        // around the barriers; if there is none, the clamped point.
        public (int X, int Y) PlanMove(int fromX, int fromY, int toX, int toY)
        {
            var barriers = Barriers();
            if (barriers.Length == 0) return (toX, toY);
            (toX, toY) = PushOut(barriers, toX, toY);
            if (!Blocked(barriers, fromX, fromY, toX, toY)) return (toX, toY);

            var nodes = new List<(double X, double Y)> { (fromX, fromY), (toX, toY) };
            foreach (var z in barriers)
                nodes.AddRange(Corners(z).Where(p =>
                    p.X >= 0 && p.Y >= 0 && p.X <= MapWidth && p.Y <= MapHeight &&
                    !barriers.Any(b => b.Contains(p.X, p.Y))));

            // Dijkstra over the visibility graph (few nodes: 4-8 per barrier).
            int n = nodes.Count;
            var dist = Enumerable.Repeat(double.MaxValue, n).ToArray();
            var prev = Enumerable.Repeat(-1, n).ToArray();
            var done = new bool[n];
            dist[0] = 0;
            for (int iter = 0; iter < n; iter++)
            {
                int u = -1;
                for (int i = 0; i < n; i++)
                    if (!done[i] && dist[i] < double.MaxValue && (u < 0 || dist[i] < dist[u])) u = i;
                if (u < 0 || u == 1) break;
                done[u] = true;
                for (int v = 0; v < n; v++)
                {
                    if (done[v]) continue;
                    double d = dist[u] + Math.Sqrt(Math.Pow(nodes[u].X - nodes[v].X, 2) + Math.Pow(nodes[u].Y - nodes[v].Y, 2));
                    if (d >= dist[v] || Blocked(barriers, nodes[u].X, nodes[u].Y, nodes[v].X, nodes[v].Y)) continue;
                    dist[v] = d; prev[v] = u;
                }
            }
            if (prev[1] < 0) return Clamp(barriers, fromX, fromY, toX, toY);
            int step = 1;
            while (prev[step] != 0) step = prev[step];
            return ((int)Math.Round(nodes[step].X), (int)Math.Round(nodes[step].Y));
        }

        private static bool Blocked(PoiZone[] barriers, double fx, double fy, double tx, double ty) =>
            barriers.Any(z => z.HitTest(fx, fy, tx, ty, out _));

        private static (int X, int Y) Clamp(PoiZone[] barriers, int fromX, int fromY, int toX, int toY)
        {
            PoiZone.Hit best = default;
            bool any = false;
            foreach (var z in barriers)
                if (z.HitTest(fromX, fromY, toX, toY, out var hit) && (!any || hit.Distance < best.Distance))
                { best = hit; any = true; }
            if (!any) return (toX, toY);
            return ((int)Math.Round(best.X + best.NormalX * Push), (int)Math.Round(best.Y + best.NormalY * Push));
        }

        // A destination inside a barrier is moved just outside it.
        private static (int X, int Y) PushOut(PoiZone[] barriers, int x, int y)
        {
            for (int pass = 0; pass < 3; pass++)
            {
                var z = barriers.FirstOrDefault(b => b.Contains(x, y));
                if (z == null) break;
                (x, y) = NearestOutside(z, x, y);
            }
            return (x, y);
        }

        private static (int X, int Y) NearestOutside(PoiZone z, int x, int y)
        {
            int gap = Push + 1;
            if (z.Shape == PoiShape.Circle)
            {
                int cx = z.Points[0].X, cy = z.Points[0].Y;
                double dx = x - cx, dy = y - cy, len = Math.Sqrt(dx * dx + dy * dy);
                if (len == 0) { dx = 1; dy = 0; len = 1; }
                // Inverted: the band between Radius and Radius + Margin is
                // blocked; go to its nearer side (inside only if there is room).
                double r = z.Radius + (z.Inverted ? z.Margin : 0) + gap;
                if (z.Inverted && z.Radius > gap && len - z.Radius < z.Radius + z.Margin - len)
                    r = z.Radius - gap;
                return ((int)Math.Round(cx + dx / len * r), (int)Math.Round(cy + dy / len * r));
            }
            int m = z.Inverted ? z.Margin : 0;
            int l = z.Left - m, t = z.Top - m, rr = z.Left + z.Width + m, b = z.Top + z.Height + m;
            var options = new List<(int X, int Y)>
            {
                (l - gap, y), (rr + gap, y), (x, t - gap), (x, b + gap)
            };
            // Inverted zone with room inside: back inside is also an exit.
            if (z.Inverted && z.Width > 2 * gap && z.Height > 2 * gap)
                options.Add((Math.Clamp(x, z.Left + gap, z.Left + z.Width - gap),
                             Math.Clamp(y, z.Top + gap, z.Top + z.Height - gap)));
            return options.OrderBy(o => Math.Abs(o.X - x) + Math.Abs(o.Y - y)).First();
        }

        private static IEnumerable<(double X, double Y)> Corners(PoiZone z)
        {
            if (z.Shape == PoiShape.Circle)
            {
                // Octagon around the circle (its edges stay outside the circle).
                double r = (z.Radius + (z.Inverted ? z.Margin : 0) + Clearance) / Math.Cos(Math.PI / 8);
                for (int i = 0; i < 8; i++)
                {
                    double a = Math.PI / 4 * i + Math.PI / 8;
                    yield return (z.Points[0].X + r * Math.Cos(a), z.Points[0].Y + r * Math.Sin(a));
                }
                yield break;
            }
            // Inverted: the blocked band reaches Margin outside the box.
            int m = z.Inverted ? z.Margin : 0;
            double l = z.Left - m - Clearance, t = z.Top - m - Clearance;
            double r2 = z.Left + z.Width + m + Clearance, b = z.Top + z.Height + m + Clearance;
            yield return (l, t); yield return (r2, t); yield return (r2, b); yield return (l, b);
        }
    }
}
