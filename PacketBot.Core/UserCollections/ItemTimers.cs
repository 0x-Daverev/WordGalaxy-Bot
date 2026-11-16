using System;
using System.Collections.Generic;

namespace FunnyProject.UserCollections
{
    // States of the item bar timer (client: §_-G4V§/§_-E4x§.as).
    public enum ItemTimerState { Ready = 0, Active = 1, CoolingDown = 2 }

    // Last timer the server sent for an item of the item bar (packet 31208),
    // e.g. "tech_energy-leech". RemainingMs counts from ReceivedUtc.
    public sealed record ItemTimer(string ItemId, ItemTimerState State, double RemainingMs,
        double MaxMs, bool Activatable, DateTime ReceivedUtc)
    {
        public DateTime EndsUtc => ReceivedUtc.AddMilliseconds(Math.Max(0, RemainingMs));
    }

    public sealed class ItemTimers
    {
        private readonly object sync = new();
        private readonly Dictionary<string, ItemTimer> timers = new(StringComparer.Ordinal);

        public void Update(string itemId, int state, double remainingMs, double maxMs, bool activatable,
            DateTime receivedUtc)
        {
            if (string.IsNullOrEmpty(itemId) || double.IsNaN(remainingMs) || remainingMs < 0 ||
                state < 0 || state > 2)
                return;
            lock (sync)
                timers[itemId] = new ItemTimer(itemId, (ItemTimerState)state, remainingMs,
                    double.IsNaN(maxMs) ? 0 : maxMs, activatable, receivedUtc);
        }

        public bool TryGet(string itemId, out ItemTimer timer)
        {
            lock (sync) return timers.TryGetValue(itemId, out timer!);
        }
    }
}
