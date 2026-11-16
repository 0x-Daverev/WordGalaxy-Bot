using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FunnyProject.Logic;
using FunnyProject.Navigation;

namespace FunnyProject.Telegram
{
    // An instance whose settings can be changed from Telegram (the headless
    // server; on Windows the window is the place to change them).
    internal interface IConfigurableBotInstance
    {
        // /ajustes N
        string SettingsStatus(int instanceNumber);
        // /ajuste N <clave> <valor>
        string ChangeSetting(int instanceNumber, string key, string value);
        // /cajas N and /caja N <numeros|todos|ninguno>: Collect box types.
        string BoxTypesStatus(int instanceNumber);
        string ChangeBoxTypes(int instanceNumber, string argument);
    }

    // Text and validation of /ajustes and /ajuste, independent of where the
    // settings are stored. Values are checked here so the file never gets a
    // setting the bot would reject or silently replace.
    internal static class SettingsCommands
    {
        public static readonly string[] GateNames =
            { "Alpha", "Beta", "Gamma", "Delta", "Epsilon", "Zeta", "Kappa", "Lambda", "Kronos", "Hades" };

        public const string KeysHelp =
            "Claves: modulo (collect|npc|gg), mapa_collect, mapa_npc (p. ej. 1-1, 5-2, 1BL), " +
            "municion_npc, municion_gg (lcb-10|mcb-25|mcb-50|ucb-100), gg (" + "Alpha…Hades" + "), " +
            "spins (1|5|10|100), ataque (laser|kami|ambos), formacion_mover, formacion_atacar, " +
            "formacion_final (id o ninguna), leech (si|no), autocompra <codigo> (si|no), invitaciones (si|no).";

        public static string Describe(BotSettings s, string instanceName, int n,
            IReadOnlyCollection<string> availableFormations, string path)
        {
            string MapName(int id) => MapGraph.Default.Maps.TryGetValue(id, out var m) ? m.name : "sin seleccionar";
            List<string> For(Dictionary<int, List<string>> byMap, int id) =>
                byMap != null && byMap.TryGetValue(id, out var v) ? v : new List<string>();
            string Formation(string id) => string.IsNullOrWhiteSpace(id) ? "ninguna" : id;
            var boxes = For(s.CollectTypesByMap, s.CollectMapId);
            var npcs = For(s.NpcKillerTargetsByMap, s.NpcKillerMapId);
            var autoBuy = BoosterPurchaseService.Items
                .Where(i => s.AutoBuyBoosters != null && s.AutoBuyBoosters.TryGetValue(i.Id, out bool on) && on)
                .Select(i => i.Code).ToArray();
            return "⚙️ Ajustes — " + instanceName +
                "\nModulo: " + s.SelectedModule +
                "\nCollect: mapa " + MapName(s.CollectMapId) + "; cajas: " +
                    (boxes.Count == 0 ? "ninguna" : string.Join(", ", boxes)) + " (/cajas " + n + ")" +
                "\nNPC Killer: mapa " + MapName(s.NpcKillerMapId) + "; " + npcs.Count + " objetivos; municion " +
                    s.NpcKillerLaser.ToUpperInvariant() + "; Energy Leech " + (s.NpcKillerEnergyLeech ? "si" : "no") +
                    " (/npcs " + n + ")" +
                "\nGG: " + (string.IsNullOrWhiteSpace(s.GalaxyGateName) ? "sin seleccionar" : s.GalaxyGateName) +
                    "; spins " + s.Spins + "; municion " + s.Laser.ToUpperInvariant() + "; ataque " + Attack(s.AttackMode) +
                    "; formacion final " + Formation(s.GgFinalNpcFormation) +
                "\nFormaciones: mover " + Formation(s.RunFormation) + ", atacar " + Formation(s.AttackFormation) +
                    ". Disponibles: " + (availableFormations.Count == 0
                        ? "todavia no recibidas del juego"
                        : string.Join(", ", availableFormations)) +
                "\nAutocompra de boosters: " + (autoBuy.Length == 0 ? "ninguna" : string.Join(", ", autoBuy)) +
                    " (codigos: " + string.Join(", ", BoosterPurchaseService.Items.Select(i => i.Code)) + ")" +
                "\nInvitaciones de grupo: " + (s.AutoAcceptInvites ? "aceptar" : "ignorar") +
                "\nFichero: " + path +
                "\n\nCambiar: /ajuste " + n + " <clave> <valor>. " + KeysHelp;
        }

        private static string Attack(string mode) => mode switch
        {
            "laser" => "Laser", "kami" => "Kamikaze", _ => "Laser + Kamikaze"
        };

        // Returns the change to apply and the reply, or a null change and an
        // error message.
        public static (Action<BotSettings>? Change, string Reply) Parse(string key, string value,
            IReadOnlyCollection<string> availableFormations)
        {
            key = (key ?? "").Trim().ToLowerInvariant();
            value = (value ?? "").Trim();
            string lower = value.ToLowerInvariant();
            if (key.Length == 0 || value.Length == 0)
                return (null, "Uso: /ajuste N <clave> <valor>. " + KeysHelp);
            switch (key)
            {
                case "modulo":
                    string? module = lower switch
                    {
                        "collect" => "Collect",
                        "npc" or "npc killer" or "npckiller" => "NPC Killer",
                        "gg" or "ggs" => "GGs",
                        _ => null
                    };
                    if (module == null) return (null, "Modulo no valido: " + value + ". Usa collect, npc o gg.");
                    return (s => s.SelectedModule = module, "Modulo: " + module + ".");
                case "mapa_collect":
                case "mapa_npc":
                    var map = FindMap(value);
                    if (map == null)
                        return (null, "Mapa no valido: " + value + ". Mapas: " +
                            string.Join(", ", MapGraph.Default.SortedMaps.Select(m => m.name)) + ".");
                    return key == "mapa_collect"
                        ? (s => s.CollectMapId = map.id, "Mapa de Collect: " + map.name + ".")
                        : (s => s.NpcKillerMapId = map.id, "Mapa de NPC Killer: " + map.name + ".");
                case "municion_npc":
                case "municion_gg":
                    if (!BotSettings.IsValidLaser(lower))
                        return (null, "Municion no valida: " + value + ". Usa " + string.Join(", ", BotSettings.LaserAmmunition) + ".");
                    return key == "municion_npc"
                        ? (s => s.NpcKillerLaser = lower, "Municion de NPC Killer: " + lower.ToUpperInvariant() + ".")
                        : (s => s.Laser = lower, "Municion de GG: " + lower.ToUpperInvariant() + ".");
                case "gg":
                    string? gate = GateNames.FirstOrDefault(g => string.Equals(g, value, StringComparison.OrdinalIgnoreCase))
                        ?? (lower == "alfa" ? "Alpha" : null);
                    if (gate == null) return (null, "GG no valida: " + value + ". Usa " + string.Join(", ", GateNames) + ".");
                    return (s => s.GalaxyGateName = gate, "GG: " + gate + ".");
                case "spins":
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int spins) ||
                        spins is not (1 or 5 or 10 or 100))
                        return (null, "Spins no validos: " + value + ". Usa 1, 5, 10 o 100.");
                    return (s => s.Spins = spins, "Spins: " + spins + ".");
                case "ataque":
                    string? attack = lower switch { "laser" => "laser", "kami" or "kamikaze" => "kami", "ambos" or "both" => "both", _ => null };
                    if (attack == null) return (null, "Ataque no valido: " + value + ". Usa laser, kami o ambos.");
                    return (s => s.AttackMode = attack, "Ataque: " + Attack(attack) + ".");
                case "formacion_mover":
                case "formacion_atacar":
                case "formacion_final":
                    string formation = lower is "ninguna" or "-" ? "" : value;
                    if (formation.Length > 0 && availableFormations.Count > 0 &&
                        !availableFormations.Contains(formation, StringComparer.Ordinal))
                        return (null, "Formacion no disponible: " + value + ". Disponibles: " +
                            string.Join(", ", availableFormations) + " o ninguna.");
                    string shown = formation.Length == 0 ? "ninguna" : formation;
                    return key switch
                    {
                        "formacion_mover" => (s => s.RunFormation = formation, "Formacion al moverse: " + shown + "."),
                        "formacion_atacar" => (s => s.AttackFormation = formation, "Formacion al atacar: " + shown + "."),
                        _ => (s => s.GgFinalNpcFormation = formation, "Formacion final de GG: " + shown + ".")
                    };
                case "autocompra":
                    var words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    var item = words.Length == 2
                        ? BoosterPurchaseService.Items.FirstOrDefault(i => string.Equals(i.Code, words[0], StringComparison.OrdinalIgnoreCase))
                        : default;
                    bool? on = words.Length == 2 ? YesNo(words[1]) : null;
                    if (item.Code == null || on == null)
                        return (null, "Uso: /ajuste N autocompra <codigo> si|no. Codigos: " +
                            string.Join(", ", BoosterPurchaseService.Items.Select(i => i.Code)) + ".");
                    return (s =>
                    {
                        s.AutoBuyBoosters ??= new Dictionary<int, bool>();
                        s.AutoBuyBoosters[item.Id] = on.Value;
                    }, "Autocompra de " + item.Code + ": " + (on.Value
                        ? "activada (compra 1 unidad cuando le queden menos de 6 h)."
                        : "desactivada."));
                case "leech":
                    bool? leech = YesNo(lower);
                    if (leech == null) return (null, "Usa: /ajuste N leech si|no.");
                    return (s => s.NpcKillerEnergyLeech = leech.Value,
                        "Energy Leech en NPC Killer: " + (leech.Value
                            ? "activado (lo compra con el bot en marcha: dura 30 min y 5 min de enfriamiento)."
                            : "desactivado."));
                case "invitaciones":
                    bool? accept = YesNo(lower);
                    if (accept == null) return (null, "Usa: /ajuste N invitaciones si|no.");
                    return (s => s.AutoAcceptInvites = accept.Value,
                        "Invitaciones de grupo: " + (accept.Value ? "aceptar." : "ignorar."));
                default:
                    return (null, "Clave desconocida: " + key + ". " + KeysHelp);
            }
        }

        private static bool? YesNo(string text) => text.Trim().ToLowerInvariant() switch
        {
            "si" or "sí" or "on" or "true" or "1" => true,
            "no" or "off" or "false" or "0" => false,
            _ => null
        };

        internal static MapGraph.Map? FindMap(string value) =>
            MapGraph.Default.Maps.Values.FirstOrDefault(m => string.Equals(m.name, value, StringComparison.OrdinalIgnoreCase))
            ?? (int.TryParse(value, out int id) && MapGraph.Default.Maps.TryGetValue(id, out var byId) ? byId : null);

        // The usual box is always offered, even before any box has been seen;
        // other types appear once the bot has seen them on the map.
        public const string DefaultBoxType = "BONUS_BOX";

        public static IReadOnlyList<string> BoxList(IEnumerable<string> selected, IEnumerable<string> observed) =>
            NpcTargetSelection.Build(selected, observed.Append(DefaultBoxType));

        // /cajas N: numbered box types (saved + seen on the Collect map).
        public static string BoxTypes(string instanceName, int n, int collectMapId,
            IReadOnlyCollection<string> selected, IReadOnlyCollection<string> observed)
        {
            string map = MapGraph.Default.Maps.TryGetValue(collectMapId, out var m) ? m.name : "sin seleccionar";
            var list = BoxList(selected, observed);
            var chosen = new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase);
            string text = "📦 Collect — " + instanceName + "\nMapa: " + map;
            for (int i = 0; i < list.Count; i++)
                text += "\n" + (i + 1) + ". " + (chosen.Contains(list[i]) ? "✅ " : "⬜ ") + list[i];
            return text + "\n\nMarca o desmarca con /caja " + n + " <numeros>, por ejemplo /caja " + n +
                " 1 3. Tambien: /caja " + n + " todos | ninguno.";
        }
    }
}
