using FunnyProject.Logic.Interface;
using FunnyProject.UserCollections.Objects;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FunnyProject.Logic
{
    public class NpcKillerModule : LogicInterface
    {
        public bool Running { get; set; }
        public int TargetMapId { get; set; }
        // Laser ammunition; may change while running.
        public string Ammunition { get; set; } = "ucb-100";
        public int MapWidth { get; set; } = 21000;
        public int MapHeight { get; set; } = 13100;
        private readonly NpcOrbitCombat combat;
        private bool recovering;
        private string[] targetNames = Array.Empty<string>();
        public void UpdateTargets(IEnumerable<string> names) =>
            Volatile.Write(ref targetNames, names.Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        private DateTime nextRoamUtc = DateTime.MinValue;
        private PreferredZone[] zones = Array.Empty<PreferredZone>();
        // Preferred zones of this map; may change while running.
        public void UpdateZones(IEnumerable<PreferredZone> list) =>
            Volatile.Write(ref zones, list.Select(z => z.Copy()).ToArray());

        public NpcKillerModule(Api api) : base(api)
        {
            combat = new NpcOrbitCombat(api) { KeepTarget = true };
        }

        internal enum Step { Fight, Roam, ReturnToZone }

        // What NPC Killer does this tick with the visible selected NPCs:
        //  - the NPC being fought is kept until it dies or leaves sight,
        //    even if it leads outside the preferred zones;
        //  - with preferred zones, a new target must be inside one; outside
        //    them the ship first flies back to a zone and searches there;
        //  - without zones, the nearest NPC, as before.
        internal static (Step Step, Player[] Targets) Plan(Player[] visible, int fightingId,
            IReadOnlyList<PreferredZone> zones, int shipX, int shipY)
        {
            if (fightingId > 0 && visible.Any(p => p.ID == fightingId))
                return (Step.Fight, visible);
            if (zones.Count == 0)
                return visible.Length > 0 ? (Step.Fight, visible) : (Step.Roam, visible);
            var inside = visible.Where(p => PreferredZones.InAny(zones, p.X, p.Y)).ToArray();
            if (inside.Length > 0) return (Step.Fight, inside);
            return (PreferredZones.InAny(zones, shipX, shipY) ? Step.Roam : Step.ReturnToZone,
                Array.Empty<Player>());
        }

        public async Task StartMethod(CancellationToken token = default)
        {
            while (Running && !token.IsCancellationRequested)
            {
                try
                {
                    if (Api.User.PacketManager?.Connected != true)
                    {
                        await Task.Delay(500, token);
                        continue;
                    }

                    // Never attack entities from a different map after a jump.
                    if (TargetMapId > 0 && Api.User.UserData.MapId != TargetMapId)
                    {
                        combat.Reset();
                        Api.User.ActionState.Current = BotAction.Idle;
                        await Task.Delay(500, token);
                        continue;
                    }

                    int maxHp = Api.User.ShipInfo.MaxHp;
                    if (maxHp <= 0)
                    {
                        await Task.Delay(500, token);
                        continue;
                    }
                    combat.Ammunition = Ammunition;
                    combat.MaxX = Math.Max(501, MapWidth - 500);
                    combat.MaxY = Math.Max(501, MapHeight - 500);
                    double hpPct = 100d * Api.User.ShipInfo.Hp / maxHp;
                    if (hpPct < 30) recovering = true;
                    if (recovering && hpPct >= 90) recovering = false;
                    if (recovering)
                    {
                        Api.User.ActionState.Current = BotAction.Run;
                        // Keep the existing repair-at-port behaviour, but do not
                        // block cancellation or dereference a missing port.
                        combat.Reset();
                        var port = Api.User.BasesPorts.Ports
                            .OrderBy(p => CalculateDistance(p.X, p.Y)).FirstOrDefault();
                        if (port != null &&
                            (!Api.User.Position.Moving || DateTime.UtcNow >= nextRoamUtc))
                        {
                            FlyToCorndinates(port.X, port.Y);
                            nextRoamUtc = DateTime.UtcNow.AddSeconds(2);
                        }
                        await Task.Delay(500, token);
                        continue;
                    }

                    Player[] npcs;
                    lock (Api.User.Players.Npcs)
                        npcs = Api.User.Players.Npcs.ToArray();
                    var selected = Volatile.Read(ref targetNames);
                    npcs = npcs.Where(p => p.Name != null && selected.Contains(p.Name, StringComparer.OrdinalIgnoreCase) &&
                        !combat.IsIgnored(p.ID)).ToArray(); // dropped after running away
                    var preferred = Volatile.Read(ref zones);
                    int shipX = Api.User.Position.X, shipY = Api.User.Position.Y;
                    var (step, targets) = Plan(npcs, combat.SelectedNpc, preferred, shipX, shipY);
                    if (step == Step.Fight)
                    {
                        Api.User.ActionState.Current = BotAction.Attack;
                        combat.Tick(targets);
                    }
                    else
                    {
                        bool wasFighting = combat.SelectedNpc > 0;
                        combat.Reset();
                        Api.User.ActionState.Current = BotAction.Run;
                        // After a kill outside the zones, head back at once.
                        if (step == Step.ReturnToZone && wasFighting) nextRoamUtc = DateTime.MinValue;
                        if ((!Api.User.Position.Moving || wasFighting) &&
                            DateTime.UtcNow >= nextRoamUtc)
                        {
                            var point = step == Step.ReturnToZone
                                ? PreferredZones.ReturnPoint(preferred, Api.User.Zones, Random.Shared, shipX, shipY)
                                : PreferredZones.RoamPoint(preferred, Api.User.Zones, Random.Shared);
                            if (point != null) FlyToCorndinates(point.Value.X, point.Value.Y);
                            else FlyToCorndinates(Random.Shared.Next(500, combat.MaxX),
                                Random.Shared.Next(500, combat.MaxY));
                            nextRoamUtc = DateTime.UtcNow.AddSeconds(2);
                        }
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    Console.WriteLine("[NPC Killer] " + ex.Message);
                    await Task.Delay(500, token);
                }
                await Task.Delay(100, token);
            }
            combat.Reset();
            Api.User.ActionState.Current = BotAction.Idle;
        }
    }
}
