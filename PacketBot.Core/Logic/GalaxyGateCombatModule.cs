using FunnyProject.Commands.Write;
using FunnyProject.Logic.Interface;
using FunnyProject.UserCollections.Objects;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HotkeyAction = FunnyProject.Commands.Write.Action;

namespace FunnyProject.Logic
{
    // Common survival / PET logic shared by every Galaxy Gate.
    // Wave transition is deliberately separate: the destination of the two exit
    // portals must be verified before sending any jump packet.
    public sealed class GalaxyGatePetState
    {
        private readonly object sync = new();
        private DateTime nextKamikazeUtc = DateTime.MinValue;
        private bool awaitingKamikazeConfirmation;
        private DateTime lastKamikazeAttemptUtc = DateTime.MinValue;
        public DateTime RepairDueUtc { get; set; } = DateTime.MinValue;
        public string CooldownStatus
        {
            get
            {
                lock (sync)
                {
                    if (awaitingKamikazeConfirmation)
                        return "Esperando al kamikaze...";
                    int seconds = Math.Max(0, (int)Math.Ceiling((nextKamikazeUtc - DateTime.UtcNow).TotalSeconds));
                    return seconds > 0 ? seconds + " s" : "Disponible";
                }
            }
        }
        public int CooldownSeconds
        {
            get { lock (sync) return Math.Max(0, (int)Math.Ceiling((nextKamikazeUtc - DateTime.UtcNow).TotalSeconds)); }
        }
        public bool CanKamikaze
        {
            get { lock (sync) return DateTime.UtcNow >= nextKamikazeUtc &&
                DateTime.UtcNow - lastKamikazeAttemptUtc >= TimeSpan.FromSeconds(1); }
        }
        public void MarkAttempt()
        {
            lock (sync)
            {
                // Retry once per second until the server confirms a positive cooldown.
                // Do not fabricate a 27-second cooldown from a local attempt.
                lastKamikazeAttemptUtc = DateTime.UtcNow;
                awaitingKamikazeConfirmation = true;
            }
        }

    public bool UpdateFromServer(double remainingMs, bool activatable, int state)
    {
        lock (sync)
        {
            if (remainingMs < 0 || remainingMs > 600000 ||
                double.IsNaN(remainingMs))
                return false;
    
            if (!activatable && remainingMs > 0)
            {
                bool firstConfirmation = awaitingKamikazeConfirmation;
                awaitingKamikazeConfirmation = false;
                nextKamikazeUtc =
                    DateTime.UtcNow.AddMilliseconds(remainingMs);
                return firstConfirmation;
            }
            else if (activatable && remainingMs <= 0)
            {
                awaitingKamikazeConfirmation = false;
                nextKamikazeUtc = DateTime.MinValue;
            }
            // 0 ms con activatable=false no confirma
            // que la habilidad esté disponible.
            return false;
        }
    }

    }

    public sealed class GalaxyGateCombatModule : LogicInterface
    {
        private readonly Api api;
        private readonly int homeMap;
        private readonly int portalType;
        private readonly MapTravelModule travel;
        private bool sawNpcs;
        private DateTime emptySince = DateTime.MinValue;
        private readonly Action<string> report;
        private readonly Action<Player[]>? onNpcSnapshot;
        private readonly int mapId;
        private bool recovering;
        private readonly GalaxyGatePetState petState;
        private readonly NpcOrbitCombat orbitCombat;
        private readonly bool useLaser;
        private readonly bool useKami;
        private int kamiSelectedNpc;
        private int kamiCircleStep;
        private DateTime nextKamiCircleMoveUtc = DateTime.MinValue;
        private const double KamiCircleRadius = 3100;
        private const double KamiCircleCenterX = 10500;
        private const double KamiCircleCenterY = 6500;
        private int lastReportedSecond = -1;
        private DateTime nextEscapeMoveUtc = DateTime.MinValue;
        private double escapeHeading;
        private bool breakingEncirclement;
        private DateTime nextBreakoutMoveUtc = DateTime.MinValue;
        private double breakoutHeading;
        private int breakoutStallCount;
        private double lastBreakoutX, lastBreakoutY;

        private bool IsEncircled(Player[] npcs)
        {
            double sx = api.User.Position.X, sy = api.User.Position.Y;
            int sectors = 0;
            int near = 0;
            foreach (var npc in npcs)
            {
                double dx = npc.X - sx, dy = npc.Y - sy;
                double distance = Math.Sqrt(dx * dx + dy * dy);
                if (distance > 1700) continue;
                near++;
                int sector = (int)Math.Floor((Math.Atan2(dy, dx) + Math.PI) * 8 / (2 * Math.PI));
                sectors |= 1 << Math.Clamp(sector, 0, 7);
            }
            // Multiple threats spread around the ship, not simply a single pack.
            int occupied = 0;
            for (int i = 0; i < 8; i++)
                if ((sectors & (1 << i)) != 0) occupied++;
            return near >= 5 && occupied >= 5;
        }

        private (int X, int Y, double Heading) ChooseBreakout(Player[] npcs)
        {
            const double stride = 2400;
            double sx = api.User.Position.X, sy = api.User.Position.Y;
            double bestScore = double.NegativeInfinity;
            (int X, int Y, double Heading) best = ((int)sx, (int)sy, breakoutHeading);
            for (int i = 0; i < 32; i++)
            {
                double heading = i * (Math.PI * 2 / 32);
                int x = (int)Math.Clamp(sx + stride * Math.Cos(heading), 650, 20350);
                int y = (int)Math.Clamp(sy + stride * Math.Sin(heading), 650, 12350);
                double vx = x - sx, vy = y - sy;
                double length = Math.Sqrt(vx * vx + vy * vy);
                if (length < 300) continue;
                double pathClearance = double.PositiveInfinity;
                double endClearance = double.PositiveInfinity;
                foreach (var npc in npcs)
                {
                    double t = Math.Clamp(((npc.X - sx) * vx + (npc.Y - sy) * vy) /
                        (vx * vx + vy * vy), 0, 1);
                    double px = sx + t * vx, py = sy + t * vy;
                    pathClearance = Math.Min(pathClearance,
                        Math.Sqrt(Math.Pow(px - npc.X, 2) + Math.Pow(py - npc.Y, 2)));
                    endClearance = Math.Min(endClearance,
                        Math.Sqrt(Math.Pow(x - npc.X, 2) + Math.Pow(y - npc.Y, 2)));
                }
                // Never freeze if all paths cross the ring. Prefer its widest gap.
                // Avoid reversing on each tick while pursuing an escape direction.
                double alignment = Math.Cos(heading - breakoutHeading);
                double score = pathClearance * 2 + endClearance * 0.45 +
                    length * 0.15 + (breakingEncirclement ? alignment * 180 : 0);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = (x, y, heading);
                }
            }
            return best;
        }


        // Pick an attainable waypoint using the nearest-threat clearance, not
        // the average NPC position (which can point into another NPC group).
        private (int X, int Y, double Clearance) EscapeWaypoint(Player[] npcs)
        {
            const int margin = 650;
            const double stride = 1800;
            double sx = api.User.Position.X, sy = api.User.Position.Y;
            double nearest = double.PositiveInfinity;
            double awayX = 0, awayY = 0;
            foreach (var npc in npcs)
            {
                double dx = sx - npc.X, dy = sy - npc.Y;
                double d = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
                if (d < nearest) nearest = d;
                // Nearby threats contribute more; avoid cancellation by distant NPCs.
                double weight = 1.0 / (d * d);
                awayX += dx / d * weight;
                awayY += dy / d * weight;
            }
            double baseAngle = awayX * awayX + awayY * awayY > 1e-16
                ? Math.Atan2(awayY, awayX) : escapeHeading;
            double bestScore = double.NegativeInfinity;
            (int X, int Y, double Clearance) best = ((int)sx, (int)sy, nearest);
            // Include tangent and reverse directions to escape corners and walls.
            for (int i = 0; i < 16; i++)
            {
                double heading = baseAngle + i * (2 * Math.PI / 16);
                int x = (int)Math.Clamp(sx + stride * Math.Cos(heading), margin, 21000 - margin);
                int y = (int)Math.Clamp(sy + stride * Math.Sin(heading), margin, 13000 - margin);
                double step = Math.Sqrt(Math.Pow(x - sx, 2) + Math.Pow(y - sy, 2));
                if (step < 250) continue;
                double clearance = npcs.Min(n => Math.Sqrt(Math.Pow(x - n.X, 2) + Math.Pow(y - n.Y, 2)));
                // Penalize paths that pass very close to a threat.
                double pathClearance = double.PositiveInfinity;
                foreach (var npc in npcs)
                {
                    double vx = x - sx, vy = y - sy;
                    double t = Math.Clamp(((npc.X - sx) * vx + (npc.Y - sy) * vy) /
                        Math.Max(1, vx * vx + vy * vy), 0, 1);
                    double px = sx + t * vx, py = sy + t * vy;
                    pathClearance = Math.Min(pathClearance,
                        Math.Sqrt(Math.Pow(px - npc.X, 2) + Math.Pow(py - npc.Y, 2)));
                }
                double score = Math.Min(clearance, pathClearance * 1.2) + step * 0.06;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = (x, y, clearance);
                    escapeHeading = heading;
                }
            }
            return best;
        }

        public GalaxyGateCombatModule(Api api, int homeMap, int portalType, MapTravelModule travel, GalaxyGatePetState petState, Action<string> report, string ammunition = "ucb-100", string attackMode = "both", Action<Player[]>? onNpcSnapshot = null) : base(api)
        {
            this.api = api;
            orbitCombat = new NpcOrbitCombat(api) { Ammunition = ammunition };
            useLaser = attackMode != "kami";
            useKami = attackMode != "laser";
            this.homeMap = homeMap;
            this.portalType = portalType;
            this.travel = travel;
            this.petState = petState;
            this.report = report;
            this.onNpcSnapshot = onNpcSnapshot;
            mapId = api.User.UserData.MapId;
        }

        public async Task RunAsync(CancellationToken token)
        {
            if (mapId <= 0 || mapId == homeMap)
                throw new InvalidOperationException("La nave no esta dentro de una Galaxy Gate.");
            while (!token.IsCancellationRequested)
            {
                token.ThrowIfCancellationRequested();
                if (api.User.UserData.MapId == homeMap)
                {
                    report("Regreso a X-1 detectado.");
                    return;
                }
                if (api.User.UserData.MapId != mapId)
                {
                    report("Cambio de mapa dentro de la GG. Reiniciando control de oleada.");
                    return;
                }
                if (api.User.PacketManager?.Connected != true)
                    throw new InvalidOperationException("TCP desconectado durante la GG.");
                api.User.PetFuel?.CheckAndBuy();
                int hp = api.User.ShipInfo.Hp;
                int max = api.User.ShipInfo.MaxHp;
                if (max <= 0)
                {
                    report("Esperando datos de vida de la nave...");
                    await Task.Delay(500, token);
                    continue;
                }
                double pct = 100d * hp / max;
                if (pct < 30) recovering = true;
                if (recovering && pct >= 90) recovering = false;
                Player[] npcs;
                lock (api.User.Players.Npcs) npcs = api.User.Players.Npcs.ToArray();
                if (petState.RepairDueUtc != DateTime.MinValue && DateTime.UtcNow >= petState.RepairDueUtc)
                {
                    SendPacket(new PetRepair());
                    petState.RepairDueUtc = DateTime.MinValue;
                }
                if (recovering && useLaser)
                {
                    api.User.ActionState.Current = BotAction.Run;
                    // Continuously kite pursuing NPCs instead of parking at an edge.
                    // Re-evaluate every 250ms; send a new move at most every 500ms.
                    if (npcs.Length > 0)
                    {
                        double sx = api.User.Position.X, sy = api.User.Position.Y;
                        double nearest = npcs.Min(n => Math.Sqrt(
                            Math.Pow(sx - n.X, 2) + Math.Pow(sy - n.Y, 2)));
                        if (DateTime.UtcNow >= nextEscapeMoveUtc ||
                            (!api.User.Position.Moving && nearest < 3500))
                        {
                            var waypoint = EscapeWaypoint(npcs);
                            if (Math.Pow(waypoint.X - api.User.Position.TargetX, 2) +
                                Math.Pow(waypoint.Y - api.User.Position.TargetY, 2) > 200 * 200 ||
                                !api.User.Position.Moving)
                                FlyToCorndinates(waypoint.X, waypoint.Y);
                            nextEscapeMoveUtc = DateTime.UtcNow.AddMilliseconds(500);
                        }
                        report("Evasion activa: " + pct.ToString("F0") +
                            "% HP; NPC mas cercano " + nearest.ToString("F0"));
                    }
                    else
                    {
                        // No threats: avoid unnecessary movement and let the ship repair.
                        report("Reparando sin NPCs: " + pct.ToString("F0") + "% HP");
                    }
                    await Task.Delay(250, token);
                    continue;
                }
                // Evaluate the final-NPC formation trigger before the next attack tick.
                onNpcSnapshot?.Invoke(npcs);
                if (npcs.Length > 0) { sawNpcs = true; emptySince = DateTime.MinValue; }
                if (npcs.Length == 0)
                {
                    api.User.ActionState.Current = BotAction.Idle;
                    breakingEncirclement = false;
                    nextBreakoutMoveUtc = DateTime.MinValue;
                    kamiSelectedNpc = 0;
                    orbitCombat.Reset();
                    // A completed wave can spawn two portals (continue + exit),
                    // or only one normal portal (type 1). Wait for a stable
                    // snapshot before deciding to avoid jumping during a spawn.
                    bool hasContinue, onlyNormalPortal;
                    lock (api.User.BasesPorts.Ports)
                    {
                        var portals = api.User.BasesPorts.Ports.Where(p => p.ID > 0).ToArray();
                        hasContinue = portals.Any(p => p.PortalType == portalType);
                        onlyNormalPortal = portals.Length == 1 && portals[0].PortalType == 1;
                    }
                    if (!sawNpcs && !hasContinue && !onlyNormalPortal)
                    {
                        report("Esperando NPCs...");
                        await Task.Delay(500, token);
                        continue;
                    }
                    if (emptySince == DateTime.MinValue) emptySince = DateTime.UtcNow;
                    if ((!hasContinue && !onlyNormalPortal) ||
                        DateTime.UtcNow - emptySince < TimeSpan.FromSeconds(3))
                    {
                        report("Esperando portal de continuacion de GG...");
                        await Task.Delay(500, token);
                        continue;
                    }
                    int jumpType = hasContinue ? portalType : 1;
                    report(onlyNormalPortal && !hasContinue
                        ? "Solo portal normal disponible: saltando tipo 1"
                        : "Saltando portal de continuacion GG tipo " + jumpType);
                    api.User.ActionState.Current = BotAction.Run;
                    await travel.JumpByPortalTypeAsync(jumpType, token, allowSameMapTransition: true);
                    sawNpcs = false;
                    emptySince = DateTime.MinValue;
                    await Task.Delay(2500, token);
                    continue;
                }
                if (!useLaser)
                {
                    api.User.ActionState.Current = BotAction.Run;
                    // Kami-only: select a live NPC, but never fire lasers or steer
                    // based on NPC positions. Movement follows a fixed circle.
                    var target = npcs.FirstOrDefault(n => n.ID > 0 && n.ID == kamiSelectedNpc)
                        ?? npcs.FirstOrDefault(n => n.ID > 0);
                    if (target != null && target.ID != kamiSelectedNpc)
                    {
                        kamiSelectedNpc = target.ID;
                        api.User.Target = target;
                        SendPacket(new FunnyProject.Commands.Write.SelectShip(
                            target.ID, target.X, target.Y,
                            api.User.Position.X, api.User.Position.Y));
                    }
                    if (DateTime.UtcNow >= nextKamiCircleMoveUtc)
                    {
                        double sx = api.User.Position.X, sy = api.User.Position.Y;
                        // A fixed, map-centered circle, independent of the
                        // spawn locations or distance of any NPC.
                        double a = kamiCircleStep * (2 * Math.PI / 48);
                        int x = (int)(KamiCircleCenterX + KamiCircleRadius * Math.Cos(a));
                        int y = (int)(KamiCircleCenterY + KamiCircleRadius * Math.Sin(a));
                        if (Math.Pow(sx - x, 2) + Math.Pow(sy - y, 2) < 650 * 650)
                            kamiCircleStep = (kamiCircleStep + 1) % 48;
                        a = kamiCircleStep * (2 * Math.PI / 48);
                        x = (int)(KamiCircleCenterX + KamiCircleRadius * Math.Cos(a));
                        y = (int)(KamiCircleCenterY + KamiCircleRadius * Math.Sin(a));
                        if (!api.User.Position.Moving ||
                            Math.Pow(api.User.Position.TargetX - x, 2) +
                            Math.Pow(api.User.Position.TargetY - y, 2) > 100 * 100)
                            FlyToCorndinates(x, y);
                        nextKamiCircleMoveUtc = DateTime.UtcNow.AddMilliseconds(500);
                    }
                    if (target != null && petState.CanKamikaze)
                    {
                        petState.MarkAttempt();
                        petState.RepairDueUtc = DateTime.UtcNow.AddSeconds(2);
                        api.User.PacketManager?.Send(new HotkeyAction("kamikaze_pet_hotkey"), "Kamikaze");
                    }
                    if (DateTime.UtcNow.Second != lastReportedSecond)
                    {
                        lastReportedSecond = DateTime.UtcNow.Second;
                        report("GG: recorrido circular con kamikaze");
                    }
                    await Task.Delay(100, token);
                    continue;
                }
                // NPCs can spawn in a ring around the Hero. Escape the ring
                // before using single-target orbital movement.
                double nearestNpc = npcs.Min(n => Math.Sqrt(
                    Math.Pow(api.User.Position.X - n.X, 2) +
                    Math.Pow(api.User.Position.Y - n.Y, 2)));
                if (!breakingEncirclement && IsEncircled(npcs))
                {
                    breakingEncirclement = true;
                    nextBreakoutMoveUtc = DateTime.MinValue;
                    breakoutStallCount = 0;
                    orbitCombat.Reset();
                    report("NPCs rodeando al Hero: buscando salida del cerco...");
                }
                if (breakingEncirclement)
                {
                    api.User.ActionState.Current = BotAction.Run;
                    if (nearestNpc > 2000)
                    {
                        breakingEncirclement = false;
                        orbitCombat.Reset();
                        report("Cerco superado. Reanudando combate orbital.");
                    }
                    else
                    {
                        if (DateTime.UtcNow >= nextBreakoutMoveUtc)
                        {
                            double moved = Math.Sqrt(Math.Pow(api.User.Position.X - lastBreakoutX, 2) +
                                Math.Pow(api.User.Position.Y - lastBreakoutY, 2));
                            if (nextBreakoutMoveUtc != DateTime.MinValue && moved < 100)
                                breakoutStallCount++;
                            else
                                breakoutStallCount = 0;
                            var escape = ChooseBreakout(npcs);
                            breakoutHeading = escape.Heading +
                                (breakoutStallCount >= 3 ? Math.PI / 4 : 0);
                            FlyToCorndinates(escape.X, escape.Y);
                            lastBreakoutX = api.User.Position.X;
                            lastBreakoutY = api.User.Position.Y;
                            nextBreakoutMoveUtc = DateTime.UtcNow.AddMilliseconds(700);
                        }
                        // Preserve PET kamikaze while escaping; do not allow the
                        // orbital controller to overwrite the breakout waypoint.
                        if (useKami && petState.CanKamikaze)
                        {
                            petState.MarkAttempt();
                            petState.RepairDueUtc = DateTime.UtcNow.AddSeconds(2);
                            api.User.PacketManager?.Send(new HotkeyAction("kamikaze_pet_hotkey"), "Kamikaze");
                        }
                        await Task.Delay(100, token);
                        continue;
                    }
                }
                // Keep orbiting NPCs even in kamikaze-only mode; do not fire lasers.
                api.User.ActionState.Current = BotAction.Attack;
                var combat = orbitCombat.Tick(npcs, attackWithLaser: useLaser);
                if (combat == null)
                {
                    await Task.Delay(100, token);
                    continue;
                }
                var now = DateTime.UtcNow;
                if (useKami && petState.CanKamikaze)
                {
                    petState.MarkAttempt();
                    petState.RepairDueUtc = DateTime.UtcNow.AddSeconds(2);
                    api.User.PacketManager?.Send(new HotkeyAction("kamikaze_pet_hotkey"), "Kamikaze");
                }
                if (now.Second != lastReportedSecond)
                {
                    lastReportedSecond = now.Second;
                    report("Combate GG activo");
                }
                await Task.Delay(100, token);
            }
        }
    }
}
