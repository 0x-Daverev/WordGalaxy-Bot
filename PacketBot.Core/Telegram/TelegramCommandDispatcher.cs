using System;
using System.Linq;
using System.Threading.Tasks;
using FunnyProject.Services;

namespace FunnyProject.Telegram
{
    // Telegram commands, shared by Windows and the server. The caller decides the thread: Windows runs it on the UI thread, the
    // server on the thread pool.
    public sealed class TelegramCommandDispatcher
    {
        public const string HelpText =
            "Comandos: /status, /startbot N, /stopbot N, /tcp N, /restart N, /phpsessid N, /rank N, /npcs N, /npc N <numeros>, /nave N, /zonas N y /zona N borrar <numero|todas>." +
            " En el servidor tambien: /ajustes N, /ajuste N <clave> <valor>, /cajas N y /caja N <numeros>.";

        private const string OnlyServer =
            "Este comando solo esta disponible en el servidor Linux; en Windows cambia los ajustes en la ventana.";

        // Menu registered with setMyCommands.
        public static readonly (string Command, string Description)[] Menu =
        {
            ("status", "Ver estado de las instancias"),
            ("startbot", "Iniciar bot de una instancia: /startbot N"),
            ("stopbot", "Parar bot de una instancia: /stopbot N"),
            ("tcp", "Reiniciar TCP de una instancia: /tcp N"),
            ("restart", "Reiniciar instancia: /restart N"),
            ("phpsessid", "Ver PHPSESSID de una instancia: /phpsessid N"),
            ("rank", "Ver rango de una instancia: /rank N"),
            ("npcs", "Ver objetivos y NPCs vistos de NPC Killer: /npcs N"),
            ("npc", "Elegir objetivos de NPC Killer: /npc N 1 3 | todos | ninguno"),
            ("nave", "Ver lo que tiene la nave (municiones, extras, techs…): /nave N"),
            ("zonas", "Ver zonas preferidas de NPC Killer: /zonas N"),
            ("zona", "Borrar zonas preferidas: /zona N borrar 2 | todas"),
            ("ajustes", "Ver ajustes de una instancia (servidor): /ajustes N"),
            ("ajuste", "Cambiar un ajuste (servidor): /ajuste N <clave> <valor>"),
            ("cajas", "Ver tipos de caja de Collect (servidor): /cajas N"),
            ("caja", "Elegir cajas de Collect (servidor): /caja N 1 3 | todos | ninguno"),
            ("help", "Mostrar los comandos disponibles")
        };

        private readonly IBotInstanceHost host;
        private readonly Func<string, int, Task<string>> fetchRank;

        public TelegramCommandDispatcher(IBotInstanceHost host,
            Func<string, int, Task<string>>? fetchRank = null)
        {
            this.host = host ?? throw new ArgumentNullException(nameof(host));
            this.fetchRank = fetchRank ?? ((phpSessionId, index) => RankService.FetchAsync(phpSessionId, index));
        }

        public async Task<string> HandleAsync(string command)
        {
            var parts = (command ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return "Usa /help para ver los comandos.";
            string op = parts[0].Split('@')[0].ToLowerInvariant();
            var instances = host.Instances;

            if (op == "/start" || op == "/help")
                return HelpText + "\n\nPacketBot " + BotVersion.Current;
            if (op == "/status")
            {
                if (instances.Count == 0) return "No hay instancias abiertas.";
                return string.Join("\n\n", instances.Select((instance, i) =>
                    "🚀 Instancia " + (i + 1) + " — " + instance.DisplayName +
                    "\n━━━━━━━━━━━━━━━━" +
                    "\nBot: " + (instance.BotRunning ? "🟢 Activo" : "🔴 Parado") +
                    "\nTCP: " + (instance.TcpConnected ? "🟢 Conectado" : "🔴 Desconectado") +
                    "\nMapa: " + instance.MapId +
                    "\n" + instance.DetailedStatus));
            }
            if (op == "/npc")
            {
                if (parts.Length < 3 || !int.TryParse(parts[1], out int npcIndex) ||
                    npcIndex < 1 || npcIndex > instances.Count)
                    return "Uso: /npc N <numeros de /npcs N>, /npc N todos o /npc N ninguno.";
                return instances[npcIndex - 1].ChangeNpcTargets(npcIndex, string.Join(' ', parts.Skip(2)));
            }
            if (op == "/zona")
            {
                if (parts.Length < 3 || !int.TryParse(parts[1], out int zoneIndex) ||
                    zoneIndex < 1 || zoneIndex > instances.Count)
                    return "Uso: /zona N borrar <numero de /zonas N> o /zona N borrar todas.";
                return instances[zoneIndex - 1].ChangeNpcZones(zoneIndex, string.Join(' ', parts.Skip(2)));
            }
            if (op == "/ajuste" || op == "/caja")
            {
                if (parts.Length < 3 || !int.TryParse(parts[1], out int n) || n < 1 || n > instances.Count)
                    return op == "/caja"
                        ? "Uso: /caja N <numeros de /cajas N>, /caja N todos o /caja N ninguno."
                        : "Uso: /ajuste N <clave> <valor>. " + SettingsCommands.KeysHelp;
                if (op == "/ajuste" && parts.Length < 4)
                    return "Uso: /ajuste N <clave> <valor>. " + SettingsCommands.KeysHelp;
                if (instances[n - 1] is not IConfigurableBotInstance configurable) return OnlyServer;
                if (op == "/caja") return configurable.ChangeBoxTypes(n, string.Join(' ', parts.Skip(2)));
                return configurable.ChangeSetting(n, parts[2], string.Join(' ', parts.Skip(3)));
            }
            if (op == "/ajustes" || op == "/cajas")
            {
                if (parts.Length != 2 || !int.TryParse(parts[1], out int n) || n < 1 || n > instances.Count)
                    return "Indica un numero valido de /status. Ejemplo: " + op + " 1.";
                if (instances[n - 1] is not IConfigurableBotInstance configurable) return OnlyServer;
                return op == "/cajas" ? configurable.BoxTypesStatus(n) : configurable.SettingsStatus(n);
            }
            if (op != "/tcp" && op != "/restart" && op != "/startbot" && op != "/stopbot" && op != "/phpsessid" && op != "/rank" && op != "/npcs" && op != "/zonas" && op != "/nave")
                return "Comando desconocido. Usa /help.";
            if (parts.Length != 2 || !int.TryParse(parts[1], out int index) ||
                index < 1 || index > instances.Count)
                return "Indica un numero valido de /status. Ejemplo: /rank 1.";
            var selected = instances[index - 1];
            if (op == "/phpsessid")
            {
                string sessionId = selected.PhpSessionId;
                if (string.IsNullOrWhiteSpace(sessionId))
                    return "La instancia " + index + " no tiene un PHPSESSID disponible.";
                return "PHPSESSID de la instancia " + index + ": " + sessionId;
            }
            if (op == "/rank")
                return await fetchRank(selected.PhpSessionId, index);
            if (op == "/npcs") return selected.NpcTargetsStatus(index);
            if (op == "/zonas") return selected.NpcZonesStatus(index);
            if (op == "/nave") return selected.ShipItemsStatus(index);
            if (op == "/startbot") return selected.StartBot();
            if (op == "/stopbot") return selected.StopBot();
            if (op == "/tcp")
            {
                await selected.RestartTcpAsync();
                return "Reinicio TCP solicitado para " + selected.DisplayName + ".";
            }
            string name = selected.DisplayName;
            await host.RestartInstanceAsync(selected);
            return "Instancia reiniciada: " + name + ".";
        }
    }
}
