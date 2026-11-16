using System;
using System.Collections.Generic;

namespace FunnyProject.Logic
{
    // Entry portal types from DarkBot StarManager.java (accessBy), also
    // confirmed against WordGalaxy LIB_PORTALS_XML and captured spawn packets.
    // Map IDs of GG instances are NOT portal type IDs.
    public static class GalaxyGatePortalTypes
    {
        private static readonly Dictionary<string, int> ByName =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["Alpha"] = 2, ["Alfa"] = 2, ["GG α"] = 2,
                ["Beta"] = 3, ["GG β"] = 3,
                ["Gamma"] = 4, ["GG γ"] = 4,
                ["Delta"] = 5, ["GG δ"] = 5,
                ["Epsilon"] = 53, ["GG ε"] = 53,
                ["Zeta"] = 54, ["GG ζ"] = 54,
                ["Kappa"] = 70, ["GG κ"] = 70,
                ["Lambda"] = 71, ["GG λ"] = 71,
                ["Kronos"] = 72, ["GG K"] = 72,
                ["Hades"] = 74, ["GG Hades"] = 74
            };

        // GG instance map IDs from DarkBot StarManager. Do not confuse with
        // PortalType: these are map IDs returned by HeroInit.
        private static readonly HashSet<int> GalaxyGateMapIds = new()
        {
            51, 52, 53, 55, 70, 71, 72, 73, 74, 75, 76, 203
        };

        public static bool IsGalaxyGateMap(int mapId) =>
            GalaxyGateMapIds.Contains(mapId);

        public static bool TryGetType(string? gateName, out int type)
        {
            type = 0;
            if (string.IsNullOrWhiteSpace(gateName)) return false;
            string name = gateName.Trim();
            if (ByName.TryGetValue(name, out type)) return true;
            if (name.StartsWith("GG ", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(3).Trim();
            return ByName.TryGetValue(name, out type);
        }
    }
}
