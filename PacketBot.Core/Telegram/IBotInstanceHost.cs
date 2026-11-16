using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;

namespace FunnyProject.Telegram
{
    // One game instance as Telegram sees it. Implemented by the Windows tab
    // (UserWindow) and by the headless server session.
    public interface IBotInstance
    {
        // Stable identity across calls (the window or the session object),
        // used to track state changes for alerts.
        object Key { get; }
        string DisplayName { get; }
        // Never logged; only returned to the authorised chat by /phpsessid.
        string PhpSessionId { get; }
        bool BotRunning { get; }
        // Running or restoring TCP before starting.
        bool BotActive { get; }
        // Why the bot last stopped, and the error if it failed.
        FunnyProject.Runtime.BotStopReason LastStopReason { get; }
        // BotRunner.StopCount: changes on every stop.
        int BotStopCount { get; }
        string? LastError { get; }
        bool TcpConnected { get; }
        int MapId { get; }
        // Lines after Bot/TCP/Mapa in /status.
        string DetailedStatus { get; }
        // /npcs N: NPC Killer map, ammunition and numbered target list.
        string NpcTargetsStatus(int instanceNumber);
        // /npc N <numeros|todos|ninguno>: change and save the targets of the
        // NPC Killer map; applies at once if NPC Killer is running.
        string ChangeNpcTargets(int instanceNumber, string argument);
        // /nave N: ammunition, extras, techs… and their amounts.
        string ShipItemsStatus(int instanceNumber);
        // /zonas N: preferred zones of the NPC Killer map.
        string NpcZonesStatus(int instanceNumber);
        // /zona N borrar <numero|todas>: applies at once if NPC Killer runs.
        string ChangeNpcZones(int instanceNumber, string argument);
        // Returns the reply for /startbot and /stopbot.
        string StartBot();
        string StopBot();
        Task RestartTcpAsync();
    }

    // The set of open instances, numbered from 1 in this order by /status.
    public interface IBotInstanceHost
    {
        IReadOnlyList<IBotInstance> Instances { get; }
        // /restart N: re-validate /map and replace the instance's session.
        Task RestartInstanceAsync(IBotInstance instance);
    }

    public static class BotStatusText
    {
        private static readonly CultureInfo Spanish = CultureInfo.GetCultureInfo("es-ES");

        // Reply of /npcs N: numbered list used by /npc N <numeros>.
        public static string NpcTargets(string instanceName, int instanceNumber, int npcKillerMapId,
            string ammunition, IReadOnlyCollection<string> selected, IReadOnlyCollection<string> observed)
        {
            string map = FunnyProject.Navigation.MapGraph.Default.Maps.TryGetValue(npcKillerMapId, out var m)
                ? m.name : "sin seleccionar";
            var list = NpcTargetSelection.Build(selected, observed);
            var chosen = new HashSet<string>(selected, System.StringComparer.OrdinalIgnoreCase);
            string text = "🎯 NPC Killer — " + instanceName +
                "\nMapa: " + map +
                "\nMunicion: " + ammunition.ToUpperInvariant();
            if (list.Count == 0)
                return text + "\nTodavia no hay NPCs vistos en el mapa (se descubren al estar en el mapa).";
            for (int i = 0; i < list.Count; i++)
                text += "\n" + (i + 1) + ". " + (chosen.Contains(list[i]) ? "✅ " : "⬜ ") + list[i];
            return text + "\n\nMarca o desmarca con /npc " + instanceNumber + " <numeros>, por ejemplo /npc " +
                instanceNumber + " 1 3. Tambien: /npc " + instanceNumber + " todos | ninguno.";
        }

        // Lines of /status after Bot/TCP/Mapa, the same on Windows and the
        // server. gateName/attackMode/ammunition only matter for GGs.
        public static string Details(bool running, string action, string module, string activity,
            string? gateName, string attackMode, string ammunition, int hp, int maxHp)
        {
            string details = "Accion: " + (running ? action : "Idle") +
                "\nModulo: " + module +
                "\nActividad: " + activity.Replace("\r", "").Replace("\n", " | ");
            if (module == "GGs")
            {
                details += "\nPuerta: " + (string.IsNullOrWhiteSpace(gateName) ? "No seleccionada" : gateName);
                details += "\nAtaque: " + (attackMode switch
                {
                    "laser" => "Laser", "kami" => "Kamikaze", _ => "Laser + Kamikaze"
                });
                if (attackMode != "kami")
                    details += "\nMunicion: " + ammunition.ToUpperInvariant();
            }
            return details + "\n" + Life(hp, maxHp);
        }

        // "Vida: 9.500/10.000 (95%)" as shown by /status.
        public static string Life(int hp, int maxHp) =>
            "Vida: " + (maxHp > 0
                ? hp.ToString("N0", Spanish) + "/" + maxHp.ToString("N0", Spanish) +
                  " (" + (100d * hp / maxHp).ToString("F0", Spanish) + "%)"
                : "Sin datos");
    }
}
