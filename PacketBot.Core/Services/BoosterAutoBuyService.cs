using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FunnyProject.Logic;

namespace FunnyProject.Services
{
    // Opt-in booster auto-buy:
    //  - Only boosters ticked in BotSettings.AutoBuyBoosters (none by default).
    //  - Only with a fresh WG_BOOSTERS snapshot that names exactly that booster
    //    with less than 6 h left (no snapshot, no purchase).
    //  - At most one purchase per booster every 6 h - 5 min, and the attempt is
    //    persisted BEFORE the POST, so retries, errors or restarts never buy twice.
    //  - At most one purchase per check (every 5 s).
    internal sealed class BoosterAutoBuyService
    {
        public static readonly TimeSpan RenewBelow = TimeSpan.FromHours(6);
        public static readonly TimeSpan MinimumInterval = TimeSpan.FromHours(6) - TimeSpan.FromMinutes(5);

        private readonly Api api;
        private readonly IBotSettingsAccess settings;
        private readonly Func<string, IBoosterPurchaseApi> purchaseFactory;
        private readonly Func<DateTime> clock;
        private int busy;

        public BoosterAutoBuyService(Api api, IBotSettingsAccess settings,
            Func<string, IBoosterPurchaseApi>? purchaseFactory = null, Func<DateTime>? clock = null)
        {
            this.api = api;
            this.settings = settings;
            this.purchaseFactory = purchaseFactory ?? (cookie => new BoosterPurchaseService(cookie));
            this.clock = clock ?? (() => DateTime.UtcNow);
        }

        public string Status { get; private set; } = "Compras automaticas: desactivadas";

        public async Task CheckAsync(CancellationToken token)
        {
            if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) return;
            try
            {
                await CheckOnceAsync(token);
            }
            finally
            {
                Interlocked.Exchange(ref busy, 0);
            }
        }

        private async Task CheckOnceAsync(CancellationToken token)
        {
            if (token.IsCancellationRequested || !api.User.BoosterRequestsReady ||
                api.User.PacketManager?.Connected != true) return;
            var current = settings.Read();
            var enabled = BoosterPurchaseService.Items.Where(x =>
                current.AutoBuyBoosters != null &&
                current.AutoBuyBoosters.TryGetValue(x.Id, out bool selected) && selected).ToArray();
            if (enabled.Length == 0)
            {
                Status = "Compras automaticas: desactivadas";
                return;
            }
            string cookie = api.User.UserData.PHPSESSID ?? "";
            if (string.IsNullOrWhiteSpace(cookie))
            {
                Status = "Autocompra: falta PHPSESSID";
                return;
            }
            foreach (var item in enabled)
            {
                if (!api.User.Boosters.TryGetRemainingSeconds(item.Code, item.Category,
                        out long remaining) || remaining >= RenewBelow.TotalSeconds)
                    continue;
                var now = clock();
                // Six-hour boosters remain below the six-hour threshold right after
                // renewal. Allow a new purchase near the next expiry, not every 5 s.
                if (current.BoosterPurchaseAttemptUtc != null &&
                    current.BoosterPurchaseAttemptUtc.TryGetValue(item.Id, out var last) &&
                    now - last < MinimumInterval)
                    continue;
                try
                {
                    // Persist the attempt before the POST. An unknown response
                    // cannot accidentally trigger another purchase after restart.
                    settings.Update(s =>
                    {
                        s.BoosterPurchaseAttemptUtc ??= new Dictionary<int, DateTime>();
                        s.BoosterPurchaseAttemptUtc[item.Id] = now;
                    });
                    using var service = purchaseFactory(cookie);
                    Status = item.Code + ": enviando compra de 1 unidad...";
                    string result = await service.BuyOneAsync(item.Id, token);
                    Status = item.Code + ": " + result;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    Status = "Autocompra cancelada";
                }
                catch (Exception ex)
                {
                    Status = item.Code + ": " + ex.Message;
                }
                Console.WriteLine("[Boosters] " + Status);
                // Never send more than one purchase per check.
                return;
            }
        }

        // Headless hosts; Windows calls CheckAsync from its 5 s UI timer.
        public async Task RunAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try { await Task.Delay(5000, token); }
                catch (OperationCanceledException) { break; }
                try { await CheckAsync(token); }
                catch (Exception ex) { Console.WriteLine("[Boosters] " + ex.GetType().Name + ": " + ex.Message); }
            }
        }
    }
}
