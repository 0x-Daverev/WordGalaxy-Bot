using System;
using System.Collections.Generic;
using FunnyProject.Logic;
using FunnyProject.Navigation;
using FunnyProject.Runtime;

namespace FunnyProject.Modules
{
    // The one place where a module is built from the saved settings, shared
    // by the Windows window, the server and the web panel. The window first
    // copies its selections into BotSettings, so both read the same values.
    internal static class BotModuleFactory
    {
        public const string Collect = "Collect";
        public const string NpcKiller = "NPC Killer";
        public const string GalaxyGates = "GGs";

        public sealed record Result(IBotModule? Module, string? Error, string? Warning)
        {
            public static Result Fail(string error) => new(null, error, null);
        }

        // "collect" / "npc" / "gg" (command line, PACKETBOT_START) to the
        // module names stored in SelectedModule; anything else unchanged.
        public static string Normalize(string module) => module.Trim().ToLowerInvariant() switch
        {
            "collect" => Collect,
            "npc" or "npc killer" => NpcKiller,
            "gg" or "ggs" => GalaxyGates,
            _ => module
        };

        // galaxyGateId: id of the gate chosen in the Windows list; 0 resolves
        // it from GalaxyGateName on the first API read.
        public static Result Create(string module, BotSettings settings, MapTravelModule travel,
            Func<IGalaxyGateApi> galaxyGateApi, string? phpSessionId, int galaxyGateId = 0,
            string settingsLocation = "los ajustes")
        {
            switch (Normalize(module))
            {
                case Collect:
                {
                    if (!travel.Graph.Maps.TryGetValue(settings.CollectMapId, out var map))
                        return Result.Fail("Selecciona el mapa antes de iniciar Collect (no hay mapa guardado en " +
                            settingsLocation + ").");
                    var types = Saved(settings.CollectTypesByMap, map.id);
                    return new Result(new CollectBotModule(travel, map, types), null,
                        types.Count == 0 ? "no hay tipos de caja seleccionados para " + map.name + "." : null);
                }
                case NpcKiller:
                {
                    if (!travel.Graph.Maps.TryGetValue(settings.NpcKillerMapId, out var map))
                        return Result.Fail("Selecciona el mapa antes de iniciar NPC Killer (no hay mapa guardado en " +
                            settingsLocation + ").");
                    var targets = Saved(settings.NpcKillerTargetsByMap, map.id);
                    return new Result(new NpcKillerBotModule(travel, map, targets, settings.NpcKillerLaser,
                        PreferredZones.For(settings, map.id)), null,
                        targets.Count == 0 ? "no hay NPCs objetivo para " + map.name + "; usa /npcs y /npc." : null);
                }
                case GalaxyGates:
                {
                    if (galaxyGateId <= 0 && string.IsNullOrWhiteSpace(settings.GalaxyGateName))
                        return Result.Fail("Selecciona una GG y los spins antes de iniciarlo (GalaxyGateName en " +
                            settingsLocation + ").");
                    if (string.IsNullOrWhiteSpace(phpSessionId))
                        return Result.Fail("PHPSESSID no disponible.");
                    return new Result(new GalaxyGatesBotModule(travel, galaxyGateApi(), new GalaxyGateOptions(
                        galaxyGateId, settings.GalaxyGateName, settings.Spins, settings.Laser,
                        settings.AttackMode, settings.GgFinalNpcFormation)), null, null);
                }
                default:
                    return Result.Fail("Modulo desconocido: " + module + ". Usa \"Collect\", \"NPC Killer\" o \"GGs\" en " +
                        settingsLocation + ".");
            }
        }

        private static List<string> Saved(Dictionary<int, List<string>>? byMap, int mapId) =>
            byMap != null && byMap.TryGetValue(mapId, out var list) ? new List<string>(list) : new List<string>();
    }
}
