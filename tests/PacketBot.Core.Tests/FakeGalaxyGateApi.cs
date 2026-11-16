using FunnyProject.Logic;
using Xunit;

namespace PacketBot.Core.Tests;

// Shared with PacketBot.Server.Tests (linked file).
// Fake api_gg.php: one gate whose parts grow with each spin.
internal sealed class FakeGalaxyGateApi : IGalaxyGateApi
{
    private readonly object sync = new();
    public GalaxyGateInfo Gate { get; } = new() { id = 3, name = "Beta", partsOwned = 0, maxParts = 2 };
    public long Uridium { get; set; } = 100_000;
    public int SpinCost { get; set; } = 100;
    public bool PrepareAppliedOnServer { get; set; } = true;
    public bool PrepareThrowsTimeout { get; set; }
    public List<string> Calls { get; } = new();

    public Task<GalaxyGateResponse> ReadAsync(CancellationToken token = default)
    {
        lock (sync)
        {
            Calls.Add("read");
            return Task.FromResult(new GalaxyGateResponse
            {
                status = true, lifeCsrf = "csrf", lifeRequestId = "req", uridium = Uridium, spinCost = SpinCost,
                gates = new()
                {
                    new GalaxyGateInfo
                    {
                        id = Gate.id, name = Gate.name, partsOwned = Gate.partsOwned,
                        maxParts = Gate.maxParts, prepared = Gate.prepared, wave = Gate.wave
                    }
                }
            });
        }
    }

    public Task PrepareAsync(int gateId, CancellationToken token = default)
    {
        lock (sync)
        {
            Calls.Add("prepare " + gateId);
            if (PrepareAppliedOnServer) Gate.prepared = true;
        }
        if (PrepareThrowsTimeout) throw new TaskCanceledException("timeout");
        if (!PrepareAppliedOnServer) throw new InvalidOperationException("Operacion GG rechazada por el servidor.");
        return Task.CompletedTask;
    }

    public Task SpinAsync(int gateId, int spins, string requestId, string csrf, CancellationToken token = default)
    {
        lock (sync)
        {
            Assert.Equal(("req", "csrf"), (requestId, csrf));
            Calls.Add("spin " + gateId + " x" + spins);
            Gate.partsOwned = Math.Min(Gate.maxParts, Gate.partsOwned + 1);
            Uridium -= (long)spins * SpinCost;
        }
        return Task.CompletedTask;
    }

    public string[] Writes()
    {
        lock (sync) return Calls.Where(c => c != "read").ToArray();
    }
}
