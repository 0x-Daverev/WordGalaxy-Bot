using System;
using System.Threading;
using System.Threading.Tasks;
using FunnyProject.Commands.Write;

namespace FunnyProject.Services
{
    // Passive WG_BOOSTERS polling: open the booster window once after each TCP (re)initialization, then
    // request the snapshot every minute. Both command bodies and lengths come
    // from a real TCP capture. Purchases are a separate, opt-in service.
    public sealed class BoosterPollingService
    {
        private readonly Api api;
        private bool boosterWindowOpened;
        private DateTime nextRequestUtc = DateTime.MinValue;

        public BoosterPollingService(Api api) => this.api = api;

        public void Tick(DateTime nowUtc)
        {
            var manager = api.User.PacketManager;
            bool ready = manager?.Connected == true && api.User.BoosterRequestsReady;
            if (!ready)
            {
                boosterWindowOpened = false;
                nextRequestUtc = DateTime.MinValue;
                return;
            }
            if (nowUtc < nextRequestUtc) return;
            if (!boosterWindowOpened)
            {
                manager!.Send(new OpenBoosters());
                boosterWindowOpened = true;
            }
            manager!.Send(new RequestBoosters());
            nextRequestUtc = nowUtc.AddMinutes(1);
        }

        // Headless hosts; Windows calls Tick from its 1 s UI timer.
        public async Task RunAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try { Tick(DateTime.UtcNow); }
                catch (Exception ex) { Console.WriteLine("[Boosters] " + ex.GetType().Name + ": " + ex.Message); }
                try { await Task.Delay(1000, token); }
                catch (OperationCanceledException) { break; }
            }
        }
    }
}
