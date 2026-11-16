using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FunnyProject.Logic;
using FunnyProject.Runtime;
using FunnyProject.UserCollections.Objects;

namespace FunnyProject.Modules
{
    // Galaxy Gates settings, explicit instead of read from UI controls.
    // GateId 0: resolved from GateName on the first API read (server).
    public sealed record GalaxyGateOptions(
        int GateId,
        string GateName,
        int Spins,
        string Ammunition = "ucb-100",
        string AttackMode = "both",
        string FinalFormation = "");

    // Galaxy Gates loop: read the gate state, spin until
    // all parts are owned, prepare (verifying the server state, never blindly
    // re-posting), travel to X-1, jump to the gate portal and fight the waves
    // (GalaxyGateCombatModule) with the wave monitor and the final formation.
    // If the ship is already inside a GG (reconnect, restart), it resumes the
    // combat directly. Runs until stopped; errors inside a cycle are retried.
    public sealed class GalaxyGatesBotModule : IBotModule, FunnyProject.Services.IFinalFormationSource
    {
        private readonly MapTravelModule travel;
        private readonly IGalaxyGateApi gg;
        private GalaxyGateOptions options;
        private Api api = null!;
        private BotModuleContext context = null!;

        // Wave monitor and final formation state (read by the Windows formation timer).
        private volatile int currentWave;
        private DateTime waveUpdatedUtc = DateTime.MinValue;
        private int expectedFinalWave;
        private int finalNpcLowCountSamples;
        private int finalWavePeakNpcCount;
        private volatile bool finalFormationActive;
        private string finalFormationStatus = "";

        // Raised after a spin and after leaving a GG (Windows refreshes its gate list).
        public event System.Action? GatesChanged;
        // Raised when the final formation order has been sent.
        public event System.Action<string>? FinalFormationSent;

        public GalaxyGatesBotModule(MapTravelModule travel, IGalaxyGateApi gg, GalaxyGateOptions options)
        {
            this.travel = travel ?? throw new ArgumentNullException(nameof(travel));
            this.gg = gg ?? throw new ArgumentNullException(nameof(gg));
            if (options.Spins is not (1 or 5 or 10 or 100))
                throw new ArgumentException("Spins validos: 1, 5, 10 o 100.", nameof(options));
            if (options.GateId <= 0 && string.IsNullOrWhiteSpace(options.GateName))
                throw new ArgumentException("Selecciona una GG.", nameof(options));
            this.options = options with
            {
                Ammunition = BotSettings.IsValidLaser(options.Ammunition) ? options.Ammunition : "ucb-100",
                AttackMode = options.AttackMode is "laser" or "kami" ? options.AttackMode : "both"
            };
        }

        public string Name => "GGs";
        public GalaxyGateOptions Options => options;
        string FunnyProject.Services.IFinalFormationSource.FinalFormation => options.FinalFormation;
        public int CurrentWave => currentWave;
        public bool FinalFormationActive => finalFormationActive;

        public async Task RunAsync(BotModuleContext context, CancellationToken token)
        {
            this.context = context;
            api = context.Api;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    // When reconnecting inside a GG, resume combat directly.
                    // GG maps are not present in the normal inter-map route graph.
                    if (GalaxyGatePortalTypes.IsGalaxyGateMap(api.User.UserData.MapId))
                    {
                        context.Report("Ya dentro de GG. Reanudando combate...");
                        await RunCombatAsync(options.GateName, token);
                        continue;
                    }
                    var state = await gg.ReadAsync(token);
                    var gate = FindGate(state)
                        ?? throw new InvalidOperationException("Puerta no encontrada.");
                    if (gate.prepared)
                    {
                        await TravelAndJumpAsync(gate.name, token);
                        if (!token.IsCancellationRequested) GatesChanged?.Invoke();
                        continue;
                    }
                    if (gate.partsOwned >= gate.maxParts)
                    {
                        // A rejected/timeout POST can still be processed by the server.
                        // Read the authoritative state before trying prepare again.
                        bool prepared = false;
                        try
                        {
                            context.Report("Preparando GG...");
                            await gg.PrepareAsync(options.GateId, token);
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex) when (ex is HttpRequestException ||
                                                   ex is TaskCanceledException ||
                                                   ex is InvalidOperationException)
                        {
                            Console.WriteLine("[GG] Preparacion no confirmada: " + ex.Message);
                        }

                        try
                        {
                            var confirmed = await gg.ReadAsync(token);
                            var latest = confirmed.gates.Find(g => g.id == options.GateId);
                            prepared = latest?.prepared == true;
                            // Do not resend prepare if the gate has changed state.
                            if (!prepared && latest != null && latest.partsOwned < latest.maxParts)
                            {
                                context.Report("Estado GG cambiado. Consultando de nuevo...");
                                await Task.Delay(3000, token);
                                continue;
                            }
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine("[GG] No se pudo verificar preparacion: " + ex.Message);
                        }

                        if (!prepared)
                        {
                            context.Report("Preparacion pendiente/rechazada. Reintentando en 15 s...");
                            await Task.Delay(15000, token);
                            continue;
                        }
                        context.Report("GG preparada. Iniciando viaje a X-1...");
                        await TravelAndJumpAsync(gate.name, token);
                        if (!token.IsCancellationRequested) GatesChanged?.Invoke();
                        continue;
                    }
                    if (string.IsNullOrWhiteSpace(state.lifeCsrf) || string.IsNullOrWhiteSpace(state.lifeRequestId))
                        throw new InvalidOperationException("Faltan csrf y requestId de la API.");
                    if (state.uridium < (long)options.Spins * state.spinCost)
                        throw new InvalidOperationException("Uridium insuficiente para los spins configurados.");
                    context.Report("Girando x" + options.Spins);
                    // Not retried blindly: after an error the next cycle reads the
                    // state again and only spins while parts are still missing.
                    await gg.SpinAsync(options.GateId, options.Spins, state.lifeRequestId, state.lifeCsrf, token);
                    GatesChanged?.Invoke();
                    await Task.Delay(100, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    Console.WriteLine("[GG] Error recuperable, reintentando: " + ex);
                    context.Report("GG: " + ex.Message + "\nReintentando en 5 s...");
                    await Task.Delay(5000, token);
                }
            }
        }

        // The Windows module passes the gate id; the server only knows the
        // saved gate name and resolves the id on the first read.
        private GalaxyGateInfo? FindGate(GalaxyGateResponse state)
        {
            var gate = options.GateId > 0
                ? state.gates.Find(g => g.id == options.GateId)
                : state.gates.Find(g => string.Equals(g.name, options.GateName, StringComparison.OrdinalIgnoreCase));
            if (gate != null && (options.GateId <= 0 || string.IsNullOrWhiteSpace(options.GateName)))
                options = options with { GateId = gate.id, GateName = gate.name };
            return gate;
        }

        private async Task TravelAndJumpAsync(string gateName, CancellationToken token)
        {
            if (!GalaxyGatePortalTypes.TryGetType(gateName, out int type))
                throw new InvalidOperationException("Tipo de portal GG desconocido: " + gateName);
            await TravelToCompanyHomeAsync(token);
            token.ThrowIfCancellationRequested();
            context.Report("Buscando " + gateName + " (tipo " + type + ")");
            await travel.JumpByPortalTypeAsync(type, token);
            await RunCombatAsync(gateName, token);
        }

        private async Task MonitorWaveAsync(int gateId, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var response = await gg.ReadAsync(token);
                    var gate = response.gates.FirstOrDefault(g => g.id == gateId);
                    // A missing gate or invalid value is NOT a confirmed round.
                    currentWave = gate != null && gate.wave > 0 ? gate.wave : 0;
                    waveUpdatedUtc = currentWave > 0 ? DateTime.UtcNow : DateTime.MinValue;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception)
                {
                    currentWave = 0;
                    waveUpdatedUtc = DateTime.MinValue;
                }
                await Task.Delay(3000, token);
            }
        }

        private void TryApplyFinalNpcFormation(Player[] npcs)
        {
            if (finalFormationActive ||
                expectedFinalWave <= 0 ||
                currentWave != expectedFinalWave ||
                waveUpdatedUtc == DateTime.MinValue ||
                DateTime.UtcNow - waveUpdatedUtc > TimeSpan.FromSeconds(10) ||
                string.IsNullOrWhiteSpace(options.FinalFormation))
            {
                finalNpcLowCountSamples = 0;
                return;
            }
            // A single TCP snapshot can briefly contain fewer NPCs while new
            // spawns arrive. Require two consecutive combat snapshots.
            int count = npcs.Count(n => n.ID > 0);
            finalWavePeakNpcCount = Math.Max(finalWavePeakNpcCount, count);
            if (count < 1 || count > 3)
            {
                finalNpcLowCountSamples = 0;
                return;
            }
            if (finalWavePeakNpcCount <= 3) return;
            if (++finalNpcLowCountSamples < 2) return;

            string formation = options.FinalFormation;
            if (api.User.PacketManager?.Connected != true ||
                !api.User.AvailableDroneFormations.Contains(formation, StringComparer.Ordinal))
                return;
            api.User.PacketManager.Send(new FunnyProject.Commands.Write.Action(formation));
            api.User.RecordFormationRequest(formation);
            finalFormationActive = true;
            finalFormationStatus = "Orden de formacion final enviada (sin confirmar). Oleada " +
                currentWave + "; NPCs visibles: " + count;
            FinalFormationSent?.Invoke(formation);
        }

        private async Task RunCombatAsync(string gateName, CancellationToken token)
        {
            if (!GalaxyGatePortalTypes.TryGetType(gateName, out int portalType))
                throw new InvalidOperationException("Tipo de GG desconocido: " + gateName);
            int home = CompanyHome();
            if (home == 0) throw new InvalidOperationException("Empresa no detectada.");
            int gateId = options.GateId;
            expectedFinalWave = GalaxyGateFinalWaves.TryGet(gateName, out int finalWave) ? finalWave : 0;
            waveUpdatedUtc = DateTime.MinValue;
            finalNpcLowCountSamples = 0;
            finalWavePeakNpcCount = 0;
            using var monitorCancel = CancellationTokenSource.CreateLinkedTokenSource(token);
            currentWave = 0;
            Task monitor = gateId > 0 ? MonitorWaveAsync(gateId, monitorCancel.Token) : Task.CompletedTask;
            finalFormationActive = false;
            finalFormationStatus = "";
            try
            {
                while (!token.IsCancellationRequested && api.User.UserData.MapId != home)
                {
                    context.Report("GG " + gateName + ": combate activo.");
                    var combat = new GalaxyGateCombatModule(api, home, portalType, travel, api.User.PetCooldown,
                        msg => context.Report(msg + "\nOleada API: " +
                            (currentWave > 0 ? currentWave.ToString() : "desconocida") +
                            (finalFormationActive ? "\n" + finalFormationStatus : "") +
                            (options.AttackMode == "laser" ? "" : "\nKamikaze: " + api.User.PetCooldown.CooldownStatus)),
                        options.Ammunition, options.AttackMode,
                        onNpcSnapshot: TryApplyFinalNpcFormation);
                    await combat.RunAsync(token);
                    if (api.User.UserData.MapId != home)
                        await Task.Delay(1200, token);
                }
            }
            finally
            {
                monitorCancel.Cancel();
                try { await monitor; }
                catch (OperationCanceledException) { }
                finalFormationActive = false;
                currentWave = 0;
                expectedFinalWave = 0;
                waveUpdatedUtc = DateTime.MinValue;
                finalNpcLowCountSamples = 0;
                finalWavePeakNpcCount = 0;
            }
            context.Report("Regreso a X-1. Consultando API...");
        }

        // Faction 1 = MMO, 2 = EIC, 3 = VRU.
        private int CompanyHome() => api.User.UserData.FactionId switch { 1 => 1, 2 => 5, 3 => 9, _ => 0 };

        private async Task TravelToCompanyHomeAsync(CancellationToken token)
        {
            if (GalaxyGatePortalTypes.IsGalaxyGateMap(api.User.UserData.MapId))
                throw new InvalidOperationException("Ya estas dentro de una GG; no se debe viajar hacia X-1.");
            int destination = CompanyHome();
            if (destination == 0)
                throw new InvalidOperationException("Empresa no detectada; no se puede calcular X-1.");
            string mapName = travel.Graph.Maps[destination].name;
            travel.StopOnArrival = true;
            travel.SetDestination(destination);
            travel.Start();
            try
            {
                while (!token.IsCancellationRequested)
                {
                    if (travel.CurrentMap == destination)
                    {
                        travel.Stop();
                        context.Report("En " + mapName + ". Portal GG pendiente de identificar.");
                        return;
                    }
                    context.Report("Viajando a " + mapName + ": " + travel.Status);
                    await Task.Delay(500, token);
                }
            }
            finally
            {
                travel.Stop();
            }
        }
    }
}
