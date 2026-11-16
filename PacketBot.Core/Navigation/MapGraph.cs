using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace FunnyProject.Navigation
{
    // Runtime loads Navigation/maps.json from the build output.
    public sealed class MapGraph
    {
        public sealed class Portal { public int x { get; set; } public int y { get; set; } public int target_map { get; set; } public int? faction_id { get; set; } }
        public sealed class Map { public int id { get; set; } public string name { get; set; } = ""; public int width { get; set; } = 21000; public int height { get; set; } = 13100; public List<Portal> portals { get; set; } = new(); }
        private sealed class Root { public List<Map> maps { get; set; } = new(); }
        public IReadOnlyDictionary<int, Map> Maps { get; }
        // Read-only shared instance for map names and sizes.
        private static readonly Lazy<MapGraph> shared = new(() => new MapGraph());
        public static MapGraph Default => shared.Value;
        public MapGraph()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Navigation", "maps.json");
            var data = JsonSerializer.Deserialize<Root>(File.ReadAllText(path)) ?? throw new InvalidDataException(path);
            Maps = data.maps.ToDictionary(m => m.id);
        }
        public IReadOnlyList<Map> SortedMaps => Maps.Values.OrderBy(m => m.name).ToList();
        public Portal? Next(int start, int destination, int faction)
        {
            if (start == destination) return null;
            if (!Maps.ContainsKey(start) || !Maps.ContainsKey(destination)) throw new ArgumentException("Unknown map");
            var queue = new Queue<int>(); queue.Enqueue(start);
            var previous = new Dictionary<int, (int from, Portal portal)>();
            var seen = new HashSet<int> { start };
            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                foreach (var portal in Maps[current].portals)
                {
                    if (portal.faction_id.HasValue && portal.faction_id.Value != faction) continue;
                    int next = portal.target_map;
                    if (!Maps.ContainsKey(next) || !seen.Add(next)) continue;
                    previous[next] = (current, portal);
                    if (next == destination)
                    {
                        int node = destination;
                        while (previous[node].from != start) node = previous[node].from;
                        return previous[node].portal;
                    }
                    queue.Enqueue(next);
                }
            }
            throw new InvalidOperationException("No route between maps");
        }
    }
}
