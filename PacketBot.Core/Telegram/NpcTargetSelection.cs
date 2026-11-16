using System;
using System.Collections.Generic;
using System.Linq;

namespace FunnyProject.Telegram
{
    // NPC Killer target list as shown by /npcs N and changed by /npc N ...
    // The list is the union of the saved targets and the NPCs seen on the map,
    // sorted as the Windows checklist, so the numbers are stable between the
    // two commands while nothing new is discovered.
    public static class NpcTargetSelection
    {
        // /npc N ...: the new targets of the saved NPC Killer map. Map is null
        // when no map is saved; the caller stores Selected and applies it live.
        internal static (FunnyProject.Navigation.MapGraph.Map? Map, List<string>? Selected, string? Error) Change(
            BotSettings settings, Func<int, IReadOnlyCollection<string>> observed, string argument)
        {
            int mapId = settings.NpcKillerMapId;
            if (!FunnyProject.Navigation.MapGraph.Default.Maps.TryGetValue(mapId, out var map))
                return (null, null, null);
            var saved = settings.NpcKillerTargetsByMap != null &&
                settings.NpcKillerTargetsByMap.TryGetValue(mapId, out var names) ? names : new List<string>();
            var (selected, error) = Apply(Build(saved, observed(mapId)), saved, argument);
            return (map, selected, error);
        }

        // Reply after a change; live: applied to a running NPC Killer.
        public static string Confirmation(string mapName, IReadOnlyCollection<string> selected, bool live) =>
            "Objetivos de NPC Killer en " + mapName + ": " +
            (selected.Count == 0 ? "ninguno" : string.Join(", ", selected)) + "." +
            (live ? " Aplicado al bot en marcha." : "");

        public static IReadOnlyList<string> Build(IEnumerable<string> selected, IEnumerable<string> observed) =>
            selected.Concat(observed)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToArray();

        // argument: numbers from /npcs separated by spaces or commas (each one
        // toggles that NPC), "todos" or "ninguno". Returns the new selection,
        // or null with an error message.
        public static (List<string>? Selected, string? Error) Apply(IReadOnlyList<string> list,
            IReadOnlyCollection<string> selected, string argument) =>
            Apply(list, selected, argument, "/npcs", "/npc", "NPCs vistos");

        // Same rules for any checklist (box types of Collect: /cajas and /caja).
        internal static (List<string>? Selected, string? Error) Apply(IReadOnlyList<string> list,
            IReadOnlyCollection<string> selected, string argument, string listCommand, string changeCommand,
            string seenItems)
        {
            string arg = (argument ?? "").Trim().ToLowerInvariant();
            if (arg.Length == 0)
                return (null, "Indica los numeros de " + listCommand + ", 'todos' o 'ninguno'. Ejemplo: " +
                    changeCommand + " 1 2 3.");
            if (arg == "ninguno") return (new List<string>(), null);
            if (arg == "todos")
            {
                if (list.Count == 0) return (null, "Todavia no hay " + seenItems + " en el mapa.");
                return (list.ToList(), null);
            }

            var current = new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase);
            var toggled = new HashSet<int>();
            foreach (string token in arg.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!int.TryParse(token, out int number) || number < 1 || number > list.Count)
                    return (null, list.Count == 0
                        ? "Todavia no hay " + seenItems + " en el mapa."
                        : "Numero no valido: " + token + ". Usa un numero de 1 a " + list.Count + " de " + listCommand + ".");
                if (!toggled.Add(number)) continue; // repeated number: toggle once
                string name = list[number - 1];
                if (!current.Remove(name)) current.Add(name);
            }
            return (list.Where(current.Contains).ToList(), null);
        }
    }
}
