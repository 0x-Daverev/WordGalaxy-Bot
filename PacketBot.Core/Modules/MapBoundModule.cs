using System.Linq;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FunnyProject.Logic;
using FunnyProject.Logic.Interface;
using FunnyProject.Navigation;
using FunnyProject.Runtime;

namespace FunnyProject.Modules
{
    // Shared loop of Collect and NPC Killer: travel to the target map, run
    // the inner logic there, and restart it with back-off after errors.
    public abstract class MapBoundModule : IBotModule
    {
        private readonly MapTravelModule travel;

        protected MapBoundModule(MapTravelModule travel, MapGraph.Map map)
        {
            this.travel = travel ?? throw new ArgumentNullException(nameof(travel));
            Map = map ?? throw new ArgumentNullException(nameof(map));
        }

        public abstract string Name { get; }
        public MapGraph.Map Map { get; }

        // If the ship stays on another map this long while the inner logic runs
        // (e.g. respawn at base after a death, or an unexpected jump), the inner
        // logic is cancelled and the module travels back. The inner logics only
        // wait for the target map and would otherwise idle forever.
        public TimeSpan OffMapGrace { get; set; } = TimeSpan.FromSeconds(5);

        // Status shown while the inner logic runs on the target map.
        protected abstract string WorkingStatus { get; }

        // Runs the inner logic once; returns when it stops by itself.
        protected abstract Task RunOnMapAsync(Api api, CancellationToken token);

        // Called after each attempt so the inner logic object can be released.
        protected abstract void ReleaseInner();

        // Runs the inner logic; returns true if it was cancelled because the
        // ship left the target map for longer than OffMapGrace.
        private async Task<bool> RunWhileOnMapAsync(Api api, CancellationToken token)
        {
            using var inner = CancellationTokenSource.CreateLinkedTokenSource(token);
            var work = RunOnMapAsync(api, inner.Token);
            DateTime? offMapSince = null;
            while (!work.IsCompleted)
            {
                await Task.WhenAny(work, Task.Delay(250, token));
                if (work.IsCompleted || token.IsCancellationRequested) break;
                int mapId = api.User.UserData.MapId;
                if (mapId <= 0 || mapId == Map.id)
                {
                    offMapSince = null;
                    continue;
                }
                offMapSince ??= DateTime.UtcNow;
                if (DateTime.UtcNow - offMapSince.Value < OffMapGrace) continue;
                Console.WriteLine("[" + Name + "] La nave esta en el mapa " + mapId + " en lugar de " +
                    Map.name + "; volviendo.");
                inner.Cancel();
                try { await work; }
                catch (OperationCanceledException) { }
                return true;
            }
            await work; // propagate the inner result or error
            return false;
        }

        public async Task RunAsync(BotModuleContext context, CancellationToken token)
        {
            var api = context.Api;
            int failures = 0;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (api.User.UserData.MapId != Map.id)
                    {
                        travel.StopOnArrival = true;
                        travel.SetDestination(Map.id);
                        travel.Start();
                        while (!token.IsCancellationRequested && api.User.UserData.MapId != Map.id)
                        {
                            context.Report(Name + ": viajando a " + Map.name + "\n" + travel.Status);
                            await Task.Delay(500, token);
                        }
                        token.ThrowIfCancellationRequested();
                        travel.Stop();
                        travel.StopOnArrival = false;
                    }
                    context.Report(Name + ": " + Map.name + "\n" + WorkingStatus);
                    bool leftMap = await RunWhileOnMapAsync(api, token);
                    failures = 0;
                    if (token.IsCancellationRequested) break;
                    if (leftMap) continue; // travel back at once
                    Console.WriteLine("[" + Name + "] El modulo termino sin parada manual; reiniciando.");
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    failures++;
                    Console.WriteLine("[" + Name + "] Error recuperable: " + ex);
                    context.Report(Name + ": " + ex.Message + "\nReintentando automaticamente...");
                }
                finally
                {
                    ReleaseInner();
                    if (travel.StopOnArrival)
                    {
                        travel.Stop();
                        travel.StopOnArrival = false;
                    }
                }
                await Task.Delay(Math.Min(30000, 2000 * Math.Max(1, failures)), token);
            }
        }
    }

    public sealed class CollectBotModule : MapBoundModule
    {
        private readonly object sync = new();
        private IReadOnlyCollection<string> types;
        private BoxLogic? collector;

        public CollectBotModule(MapTravelModule travel, MapGraph.Map map, IEnumerable<string> boxTypes)
            : base(travel, map) => types = new List<string>(boxTypes);

        public override string Name => "Collect";
        protected override string WorkingStatus => "Buscando cajas seleccionadas";

        // Live change of the selected box types while running.
        public void UpdateTypes(IEnumerable<string> boxTypes)
        {
            var copy = new List<string>(boxTypes);
            lock (sync)
            {
                types = copy;
                collector?.UpdateTypes(copy);
            }
        }

        protected override Task RunOnMapAsync(Api api, CancellationToken token)
        {
            var logic = new BoxLogic(api)
            {
                Running = true,
                TargetMapId = Map.id,
                MapWidth = Map.width,
                MapHeight = Map.height
            };
            lock (sync)
            {
                logic.UpdateTypes(types);
                collector = logic;
            }
            return logic.StartMethod(token);
        }

        protected override void ReleaseInner()
        {
            lock (sync)
            {
                if (collector != null) collector.Running = false;
                collector = null;
            }
        }
    }

    public sealed class NpcKillerBotModule : MapBoundModule
    {
        private readonly object sync = new();
        private IReadOnlyCollection<string> targets;
        private string ammunition;
        private List<PreferredZone> zones;
        private NpcKillerModule? killer;

        public NpcKillerBotModule(MapTravelModule travel, MapGraph.Map map, IEnumerable<string> npcNames,
            string ammunition = "ucb-100", IEnumerable<PreferredZone>? preferredZones = null)
            : base(travel, map)
        {
            targets = new List<string>(npcNames);
            zones = preferredZones?.Select(z => z.Copy()).ToList() ?? new List<PreferredZone>();
            this.ammunition = BotSettings.IsValidLaser(ammunition) ? ammunition : "ucb-100";
        }

        public string Ammunition { get { lock (sync) return ammunition; } }

        // Live change of the laser ammunition while running.
        public void UpdateAmmunition(string ammo)
        {
            if (!BotSettings.IsValidLaser(ammo)) return;
            lock (sync)
            {
                ammunition = ammo;
                if (killer != null) killer.Ammunition = ammo;
            }
        }

        // Live change of the preferred zones of this module's map.
        public void UpdateZones(IEnumerable<PreferredZone> list)
        {
            var copy = list.Select(z => z.Copy()).ToList();
            lock (sync)
            {
                zones = copy;
                killer?.UpdateZones(copy);
            }
        }

        public override string Name => "NPC Killer";
        protected override string WorkingStatus => "Buscando NPCs seleccionados";

        // Live change of the selected NPC names while running.
        public void UpdateTargets(IEnumerable<string> npcNames)
        {
            var copy = new List<string>(npcNames);
            lock (sync)
            {
                targets = copy;
                killer?.UpdateTargets(copy);
            }
        }

        protected override Task RunOnMapAsync(Api api, CancellationToken token)
        {
            var logic = new NpcKillerModule(api)
            {
                Running = true,
                TargetMapId = Map.id,
                MapWidth = Map.width,
                MapHeight = Map.height
            };
            lock (sync)
            {
                logic.Ammunition = ammunition;
                logic.UpdateTargets(targets);
                logic.UpdateZones(zones);
                killer = logic;
            }
            return logic.StartMethod(token);
        }

        protected override void ReleaseInner()
        {
            lock (sync)
            {
                if (killer != null) killer.Running = false;
                killer = null;
            }
        }
    }
}
