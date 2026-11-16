using System;
using System.Collections.Generic;
using System.Linq;

namespace FunnyProject.UserCollections
{
    // NPC names and box types seen on each map during this session, so the
    // user can pick NPC Killer targets and Collect types. Sampled every
    // second by Api.
    public sealed class MapObservations
    {
        private readonly object sync = new();
        private readonly Dictionary<int, HashSet<string>> npcNames = new();
        private readonly Dictionary<int, HashSet<string>> boxTypes = new();

        public void Sample(User user)
        {
            int mapId = user.UserData.MapId;
            if (mapId <= 0) return;
            string[] names;
            lock (user.Players.Npcs)
                names = user.Players.Npcs
                    .Where(n => n.IsNpc && !string.IsNullOrWhiteSpace(n.Name))
                    .Select(n => n.Name!.Trim()).ToArray();
            string[] types = user.Boxes.Snapshot()
                .Select(box => box.Type)
                .Where(type => !string.IsNullOrWhiteSpace(type))
                .ToArray();
            lock (sync)
            {
                Add(npcNames, mapId, names);
                Add(boxTypes, mapId, types);
            }
        }

        public IReadOnlyList<string> NpcNames(int mapId) => Get(npcNames, mapId);
        public IReadOnlyList<string> BoxTypes(int mapId) => Get(boxTypes, mapId);

        private static void Add(Dictionary<int, HashSet<string>> byMap, int mapId, IEnumerable<string> values)
        {
            if (!byMap.TryGetValue(mapId, out var known))
                byMap[mapId] = known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            known.UnionWith(values);
        }

        private IReadOnlyList<string> Get(Dictionary<int, HashSet<string>> byMap, int mapId)
        {
            lock (sync)
                return byMap.TryGetValue(mapId, out var known)
                    ? known.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray()
                    : Array.Empty<string>();
        }
    }
}
