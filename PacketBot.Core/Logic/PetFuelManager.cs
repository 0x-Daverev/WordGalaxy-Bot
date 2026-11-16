using FunnyProject.Commands.Write;
using System;

namespace FunnyProject.Logic
{
    // Never purchase from a guessed value: the incoming obfuscated packet 22037
    // still requires a verified byte-level decoder for the _-fP field.
    public sealed class PetFuelManager
    {
        private readonly Api api;
        private readonly object sync = new();
        private int? fuel;
        private int? fuelAtPurchase;
        private DateTime purchaseUtc = DateTime.MinValue;
        public PetFuelManager(Api api) => this.api = api;
        public int? CurrentFuel { get { lock (sync) return fuel; } }

        public void OnFuelUpdate(int amount)
        {
            if (amount < 0) return;
            lock (sync)
            {
                fuel = amount;
                if (fuelAtPurchase.HasValue && amount > fuelAtPurchase.Value)
                {
                    fuelAtPurchase = null;
                }
                CheckAndBuyLocked();
            }
        }

        public void CheckAndBuy()
        {
            lock (sync) CheckAndBuyLocked();
        }

        private void CheckAndBuyLocked()
        {
            if (!fuel.HasValue || fuel.Value >= 1000 || fuelAtPurchase.HasValue) return;
            if (api.User.PacketManager?.Connected != true) return;
            // Only one request until an observed fuel increase confirms purchase.
            fuelAtPurchase = fuel.Value;
            purchaseUtc = DateTime.UtcNow;
            api.User.PacketManager.Send(new PetFuelPurchase());
        }
    }
}
