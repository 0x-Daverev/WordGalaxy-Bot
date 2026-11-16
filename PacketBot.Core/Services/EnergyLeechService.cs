using System;
using System.Threading;
using System.Threading.Tasks;
using FunnyProject.Modules;
using FunnyProject.UserCollections;

namespace FunnyProject.Services
{
    // Energy Leech for NPC Killer (opt-in, BotSettings.NpcKillerEnergyLeech).
    // Lasts 30 min, then 5 min of cooldown.
    //  - Only while NPC Killer is running and the hero is in the game.
    //  - The server's item timer (packet 31208, "tech_energy-leech") decides
    //    when it is ready, if it arrived after the last attempt.
    //  - Without it, the next attempt is 35 min after the last one.
    //  - The attempt is persisted BEFORE sending, so restarts never buy twice
    //    within a cycle; never more than one attempt per minute.
    internal sealed class EnergyLeechService
    {
        public const string ItemId = "tech_energy-leech";
        public static readonly TimeSpan Duration = TimeSpan.FromMinutes(30);
        public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(5);
        public static readonly TimeSpan MinimumRetry = TimeSpan.FromMinutes(1);

        private readonly Api api;
        private readonly IBotSettingsAccess settings;
        private readonly Func<DateTime> clock;
        private readonly object sync = new();

        public EnergyLeechService(Api api, IBotSettingsAccess settings, Func<DateTime>? clock = null)
        {
            this.api = api;
            this.settings = settings;
            this.clock = clock ?? (() => DateTime.UtcNow);
        }

        public string Status { get; private set; } = "Energy Leech: desactivado";

        // When the next activation is allowed, and where that comes from.
        internal static (DateTime? ReadyUtc, string Source) NextReady(DateTime? lastAttemptUtc, ItemTimer? timer)
        {
            if (timer != null && (lastAttemptUtc == null || timer.ReceivedUtc >= lastAttemptUtc))
            {
                return timer.State switch
                {
                    ItemTimerState.Active => (timer.EndsUtc + Cooldown, "activo"),
                    ItemTimerState.CoolingDown => (timer.EndsUtc, "enfriamiento"),
                    _ => timer.Activatable ? (timer.ReceivedUtc, "listo") : (null, "no disponible")
                };
            }
            return lastAttemptUtc == null
                ? (DateTime.MinValue, "sin datos")
                : (lastAttemptUtc.Value + Duration + Cooldown, "estimado");
        }

        public void Tick()
        {
            lock (sync)
            {
                var current = settings.Read();
                if (!current.NpcKillerEnergyLeech)
                {
                    Status = "Energy Leech: desactivado";
                    return;
                }
                if (api.User.PacketManager?.Connected != true || !api.User.BoosterRequestsReady ||
                    !api.Runner.IsRunning || api.Runner.ModuleName != BotModuleFactory.NpcKiller)
                {
                    Status = "Energy Leech: espera a NPC Killer";
                    return;
                }
                var now = clock();
                var last = current.EnergyLeechAttemptUtc;
                api.User.ItemTimers.TryGet(ItemId, out var timer);
                var (ready, source) = NextReady(last, timer);
                if (ready == null)
                {
                    Status = "Energy Leech: no disponible";
                    return;
                }
                if (last != null && now - last.Value < MinimumRetry) return;
                if (now < ready.Value)
                {
                    Status = "Energy Leech: " + source + ", siguiente en " +
                        Math.Ceiling((ready.Value - now).TotalMinutes) + " min";
                    return;
                }
                // Persist first: an unknown result never leads to a second purchase.
                settings.Update(s => s.EnergyLeechAttemptUtc = now);
                api.User.PacketManager!.Send(new FunnyProject.Commands.Write.Action(ItemId), "energy-leech");
                Status = "Energy Leech: comprado";
                Console.WriteLine("[NPC Killer] Energy Leech comprado.");
            }
        }

        // Headless hosts; Windows calls Tick from its 500 ms UI timer.
        public async Task RunAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try { Tick(); }
                catch (Exception ex) { Console.WriteLine("[Energy Leech] " + ex.GetType().Name + ": " + ex.Message); }
                try { await Task.Delay(1000, token); }
                catch (OperationCanceledException) { break; }
            }
        }
    }
}
