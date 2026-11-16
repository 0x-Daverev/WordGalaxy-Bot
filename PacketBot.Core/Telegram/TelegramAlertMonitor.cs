using System;
using System.Collections.Generic;
using System.Linq;
using FunnyProject.Runtime;

namespace FunnyProject.Telegram
{
    // Telegram alerts only for situations that need the user, checked
    // periodically by the host (Windows: 1 s UI timer; server: background loop):
    //  - The bot stopped without being asked to: the module finished or failed,
    //    or TCP could not be restored when starting. Stopping it with /stopbot
    //    or the button sends nothing.
    //  - TCP has been down for longer than TcpGracePeriod while the bot is
    //    active (automatic reconnects keep failing). Short drops that recover on
    //    their own, such as the TCP restart after a death, send nothing. One
    //    alert per outage, plus one "recovered" message if it comes back later.
    public sealed class TelegramAlertMonitor
    {
        private sealed class Tracked
        {
            public int StopCount;
            public DateTime? TcpDownSince;
            public bool TcpAlertSent;
        }

        public static readonly TimeSpan DefaultTcpGracePeriod = TimeSpan.FromSeconds(60);

        private readonly Dictionary<object, Tracked> tracked = new();
        private readonly TimeSpan tcpGracePeriod;
        private readonly Func<DateTime> clock;

        public TelegramAlertMonitor(TimeSpan? tcpGracePeriod = null, Func<DateTime>? clock = null)
        {
            this.tcpGracePeriod = tcpGracePeriod ?? DefaultTcpGracePeriod;
            this.clock = clock ?? (() => DateTime.UtcNow);
        }

        public IReadOnlyList<string> Check(IBotInstanceHost host)
        {
            var now = clock();
            var alerts = new List<string>();
            var present = new HashSet<object>();
            var instances = host.Instances;
            for (int i = 0; i < instances.Count; i++)
            {
                var instance = instances[i];
                int number = i + 1;
                present.Add(instance.Key);
                bool active = instance.BotActive;
                int stops = instance.BotStopCount;
                bool firstSample = !tracked.TryGetValue(instance.Key, out var t);
                if (firstSample) tracked[instance.Key] = t = new Tracked { StopCount = stops };

                // A stop happened since the last check (even a run shorter than
                // the check interval) and the bot is still stopped.
                if (!firstSample && stops != t!.StopCount && !active)
                {
                    string? reason = instance.LastStopReason switch
                    {
                        BotStopReason.Completed => "el modulo termino por si solo",
                        BotStopReason.Faulted => "error: " + (instance.LastError ?? "desconocido"),
                        BotStopReason.StartFailed => "no se pudo restablecer TCP al iniciar" +
                            (instance.LastError == null ? "" : " (" + instance.LastError + ")"),
                        _ => null // stopped on request: no alert
                    };
                    if (reason != null)
                        alerts.Add("⚠️ PacketBot detenido: " + instance.DisplayName +
                            " (mapa " + instance.MapId + "): " + reason + ". Usa /startbot " + number + " para reanudarlo.");
                }
                t!.StopCount = stops;

                if (active && !instance.TcpConnected)
                {
                    t.TcpDownSince ??= now;
                    if (!t.TcpAlertSent && now - t.TcpDownSince.Value >= tcpGracePeriod)
                    {
                        t.TcpAlertSent = true;
                        alerts.Add("🔌 Sin conexion TCP desde hace " + Describe(now - t.TcpDownSince.Value) +
                            ": " + instance.DisplayName + " (mapa " + instance.MapId + "). El bot sigue reintentando; " +
                            "si no se recupera, prueba /restart " + number + " o revisa el PHPSESSID.");
                    }
                }
                else
                {
                    if (t.TcpAlertSent && instance.TcpConnected)
                        alerts.Add("✅ TCP recuperado: " + instance.DisplayName + ".");
                    t.TcpDownSince = null;
                    t.TcpAlertSent = false;
                }
            }
            foreach (var removed in tracked.Keys.Where(k => !present.Contains(k)).ToArray())
                tracked.Remove(removed);
            return alerts;
        }

        private static string Describe(TimeSpan span) =>
            span.TotalMinutes >= 1 ? (int)span.TotalMinutes + " min" : (int)span.TotalSeconds + " s";
    }
}
