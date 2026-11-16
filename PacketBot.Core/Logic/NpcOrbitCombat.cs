using FunnyProject.Logic.Interface;
using FunnyProject.UserCollections.Objects;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FunnyProject.Logic
{
    // Shared combat navigation for Galaxy Gates and NPC Killer.
    public sealed class NpcOrbitCombat : LogicInterface
    {
        private int selectedNpc;
        private DateTime nextAttackUtc = DateTime.MinValue;
        private DateTime nextOrbitMoveUtc = DateTime.MinValue;
        private double angle;
        private int orbitDirection = 1;

        public string Ammunition { get; set; } = "ucb-100";
        // Keep fighting the selected NPC while it is still in the list
        // (until it dies or leaves sight), instead of switching to whichever
        // is nearest on every tick. NPC Killer turns it on; GG keeps the
        // nearest-first behaviour.
        public bool KeepTarget { get; set; }
        // NPC being fought (0 = none).
        public int SelectedNpc => selectedNpc;

        // With KeepTarget: an NPC that runs away (often faster, and it does
        // not fight back until hit) is dropped. It is dropped when it has
        // been farther than ChaseDistance and the ship has not got at least
        // 100 closer during GiveUpAfter. It is then ignored for IgnoreFor.
        public const double ChaseDistance = 1000;
        public static readonly TimeSpan GiveUpAfter = TimeSpan.FromSeconds(4);
        public static readonly TimeSpan IgnoreFor = TimeSpan.FromSeconds(60);
        internal Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;
        private readonly Dictionary<int, DateTime> ignoredUntil = new();
        // Re-selection while the server has not confirmed the target.
        public static readonly TimeSpan SelectRetry = TimeSpan.FromSeconds(1.5);
        private DateTime nextSelectRetryUtc = DateTime.MinValue;
        private int selectRetries;
        private double chaseAnchor;
        private DateTime chaseAnchorUtc = DateTime.MinValue;

        public bool IsIgnored(int npcId)
        {
            if (!ignoredUntil.TryGetValue(npcId, out var until)) return false;
            if (Clock() < until) return true;
            ignoredUntil.Remove(npcId);
            return false;
        }
        // Movement limits (500 px inside the map edge). Defaults fit the
        // standard 21000x13100 maps; NPC Killer sets them per map.
        public int MaxX { get; set; } = 20500;
        public int MaxY { get; set; } = 12500;

        public NpcOrbitCombat(Api api) : base(api) { }

        public void Reset()
        {
            selectedNpc = 0;
            nextAttackUtc = DateTime.MinValue;
            nextOrbitMoveUtc = DateTime.MinValue;
            chaseAnchorUtc = DateTime.MinValue;
            Api.User.Target = null;
        }

        // True when the target has stayed out of reach without the ship
        // getting closer for GiveUpAfter.
        private bool GivesUpChase(double distance)
        {
            var now = Clock();
            if (distance <= ChaseDistance)
            {
                chaseAnchorUtc = DateTime.MinValue;
                return false;
            }
            if (chaseAnchorUtc == DateTime.MinValue || distance <= chaseAnchor - 100)
            {
                chaseAnchor = distance;
                chaseAnchorUtc = now;
                return false;
            }
            return now - chaseAnchorUtc >= GiveUpAfter;
        }

        public (int TargetId, double Distance)? Tick(Player[] npcs, bool attackWithLaser = true)
        {
            if (Api.User.PacketManager?.Connected != true) return null;
            if (KeepTarget) npcs = npcs.Where(p => !IsIgnored(p.ID)).ToArray();
            var target = KeepTarget && selectedNpc > 0
                ? npcs.FirstOrDefault(p => p.ID == selectedNpc)
                : null;
            target ??= npcs.Where(p => p.ID > 0)
                .OrderBy(p => Math.Pow(p.X - Api.User.Position.X, 2) +
                              Math.Pow(p.Y - Api.User.Position.Y, 2))
                .FirstOrDefault();
            if (target == null) { Reset(); return null; }
            bool changedTarget = target.ID != selectedNpc;
            if (attackWithLaser && (changedTarget || DateTime.UtcNow >= nextAttackUtc))
            {
                EngageNpc(target, selectTarget: changedTarget, ammo: Ammunition);
                nextAttackUtc = DateTime.UtcNow.AddSeconds(3);
            }
            else if (!attackWithLaser && changedTarget)
            {
                // Kamikaze needs a selected target, but must not fire lasers.
                Api.User.Target = target;
                SelectNpc(target);
            }
            if (changedTarget)
            {
                nextSelectRetryUtc = Clock() + SelectRetry;
                selectRetries = 0;
            }
            else if (Api.User.ConfirmedTargetId != target.ID && Clock() >= nextSelectRetryUtc)
            {
                // The server has not confirmed the selection (no 11468): the
                // attack does nothing until it does, so select again.
                Api.User.Target = target;
                SelectNpc(target);
                if (attackWithLaser) EngageNpc(target, selectTarget: false, ammo: Ammunition);
                nextSelectRetryUtc = Clock() + SelectRetry;
                if (++selectRetries == 5)
                    Console.WriteLine("[Combate] El servidor no confirma la seleccion de " +
                        (target.Name ?? "NPC") + " (" + target.ID + ") tras 5 intentos.");
            }
            selectedNpc = target.ID;
                // Orbit by short tangential steps measured from the ACTUAL ship
                // position. Never chase a waypoint on the NPC-facing side.
                // Keep a buffer above 580 to account for straight-line movement.
                const double minimumDistance = 580;
                const double preferredOrbit = 680;
                const double maximumDistance = 850;
                double shipX = Api.User.Position.X, shipY = Api.User.Position.Y;
                double orbitDx = shipX - target.X, orbitDy = shipY - target.Y;
                double distance = Math.Sqrt(orbitDx * orbitDx + orbitDy * orbitDy);
                var now = DateTime.UtcNow;
                if (changedTarget)
                {
                    orbitDirection = 1;
                    nextOrbitMoveUtc = DateTime.MinValue;
                    chaseAnchorUtc = DateTime.MinValue;
                }
                if (KeepTarget && GivesUpChase(distance))
                {
                    ignoredUntil[target.ID] = Clock() + IgnoreFor;
                    Reset();
                    return null;
                }
                if (now >= nextOrbitMoveUtc)
                {
                    double radialAngle = distance > 1
                        ? Math.Atan2(orbitDy, orbitDx) : angle;
                    angle = radialAngle;
                    // A 0.35 radian step produces a short chord rather than a
                    // direct route across the NPC. Larger radii near danger.
                    double radius = distance < 700 ? 780 :
                        distance > maximumDistance ? preferredOrbit :
                        Math.Max(preferredOrbit, distance);
                    const double angularStep = 0.35;
                    (int X, int Y, double Clearance, double Travel) Candidate(int direction)
                    {
                        double a = radialAngle + direction * angularStep;
                        int x = (int)Math.Clamp(target.X + radius * Math.Cos(a), 500, MaxX);
                        int y = (int)Math.Clamp(target.Y + radius * Math.Sin(a), 500, MaxY);
                        double vx = x - shipX, vy = y - shipY;
                        double travel = Math.Sqrt(vx * vx + vy * vy);
                        double projection = travel < 1 ? 0 : Math.Clamp(
                            ((target.X - shipX) * vx + (target.Y - shipY) * vy) /
                            (vx * vx + vy * vy), 0, 1);
                        double closestX = shipX + projection * vx - target.X;
                        double closestY = shipY + projection * vy - target.Y;
                        return (x, y, Math.Sqrt(closestX * closestX + closestY * closestY), travel);
                    }
                    var forward = Candidate(orbitDirection);
                    var reverse = Candidate(-orbitDirection);
                    // Reverse orbit direction near map borders or obstacles.
                    if ((forward.Clearance < minimumDistance ||
                         forward.Travel < 80) &&
                        reverse.Clearance > forward.Clearance)
                    {
                        orbitDirection = -orbitDirection;
                        forward = reverse;
                    }
                    if (forward.Clearance >= minimumDistance && forward.Travel >= 80)
                    {
                        if (changedTarget ||
                            Math.Pow(forward.X - Api.User.Position.TargetX, 2) +
                            Math.Pow(forward.Y - Api.User.Position.TargetY, 2) > 80 * 80 ||
                            !Api.User.Position.Moving)
                            FlyToCorndinates(forward.X, forward.Y);
                    }
                    else
                    {
                        // Too close or pinned against a wall: move outward
                        // rather than taking a chord through the NPC.
                        double outwardAngle = distance > 1 ? radialAngle : 0;
                        int safeX = (int)Math.Clamp(
                            shipX + 350 * Math.Cos(outwardAngle), 500, MaxX);
                        int safeY = (int)Math.Clamp(
                            shipY + 350 * Math.Sin(outwardAngle), 500, MaxY);
                        if (Math.Pow(safeX - shipX, 2) + Math.Pow(safeY - shipY, 2) <= 100 * 100)
                        {
                            // Radial escape is blocked by a border/corner. Try
                            // tangential movements in both directions, favouring
                            // the one that increases separation from the NPC.
                            double bestClearance = double.NegativeInfinity;
                            int bestX = safeX, bestY = safeY;
                            foreach (int direction in new[] { orbitDirection, -orbitDirection })
                            {
                                double tangent = radialAngle + direction * Math.PI / 2;
                                int tx = (int)Math.Clamp(shipX + 650 * Math.Cos(tangent), 500, MaxX);
                                int ty = (int)Math.Clamp(shipY + 650 * Math.Sin(tangent), 500, MaxY);
                                double movement = Math.Sqrt(Math.Pow(tx - shipX, 2) + Math.Pow(ty - shipY, 2));
                                if (movement < 150) continue;
                                double clearance = Math.Sqrt(Math.Pow(tx - target.X, 2) + Math.Pow(ty - target.Y, 2));
                                if (clearance > bestClearance)
                                {
                                    bestClearance = clearance;
                                    bestX = tx; bestY = ty;
                                    orbitDirection = direction;
                                }
                            }
                            safeX = bestX; safeY = bestY;
                        }
                        if (Math.Pow(safeX - shipX, 2) + Math.Pow(safeY - shipY, 2) > 100 * 100)
                            FlyToCorndinates(safeX, safeY);
                    }
                    nextOrbitMoveUtc = now.AddMilliseconds(250);
                }
            return (target.ID, distance);
        }
    }
}
