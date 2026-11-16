using System;
using System.Collections.Generic;

namespace FunnyProject.Logic
{
    // Last waves independently verified in-game for WordGalaxy.
    // Unknown Galaxy Gates are intentionally excluded.
    public static class GalaxyGateFinalWaves
    {
        private static readonly IReadOnlyDictionary<string, int> Waves =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["Kronos"] = 13,
                ["Lambda"] = 9,
                ["Kappa"] = 11,
                ["Zeta"] = 10,
                ["Delta"] = 10,
                ["Gamma"] = 10,
                ["Beta"] = 10,
                ["Alpha"] = 10,
                ["Alfa"] = 10
            };

        public static bool TryGet(string? gateName, out int finalWave)
        {
            finalWave = 0;
            if (string.IsNullOrWhiteSpace(gateName)) return false;
            string name = gateName.Trim();
            if (name.StartsWith("GG ", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(3).Trim();
            return Waves.TryGetValue(name, out finalWave);
        }
    }
}
