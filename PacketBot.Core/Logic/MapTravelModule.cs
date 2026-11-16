using FunnyProject.Commands.Read;
using FunnyProject.Commands.Write;
using FunnyProject.Logic.Interface;
using FunnyProject.Navigation;
using FunnyProject.UserCollections.Objects;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FunnyProject.Logic
{
    public sealed class MapTravelModule : IDisposable
    {
        private readonly Api api;
        private readonly MapGraph graph;
        private readonly LogicInterface movement;
        private readonly CancellationTokenSource cancel = new();
        private readonly object guard = new();
        private int currentMap = -1;
        private int faction = 0;
        private int destination = -1;
        private bool running;
        public bool AutoAcceptInvites { get; set; } = true;
        // null: accept invitations from any player; otherwise restrict by inviter (_-A4d).
        public int? AllowedInviterId { get; set; }
        public void AcceptGroupByUserId(int userId)
        {
            if (userId <= 0) throw new ArgumentOutOfRangeException(nameof(userId));
            if (api.User.PacketManager?.Connected != true) { Console.WriteLine("[Group] TCP desconectado; no se puede aceptar."); return; }
            api.User.PacketManager.Send(new GroupAccept(userId));
        }
        private void GroupInvite(object? sender, GroupInvitation invite)
        {
            if (!AutoAcceptInvites || api.User.PacketManager?.Connected != true) return;
            if (AllowedInviterId is int allowed && invite.InviterId != allowed)
            {
                return;
            }
            AcceptGroupByUserId(invite.AcceptanceId);
        }
        private DateTime nextAction = DateTime.MinValue;
        private int lastJumpMap = -1;
        private readonly Random random = new();
        public string Status { get; private set; } = "Detenido";
        public MapGraph Graph => graph;
        public int CurrentMap { get { lock (guard) return currentMap; } }
        public bool StopOnArrival { get; set; }
        public bool Running { get { lock (guard) return running; } }
        public MapTravelModule(Api api)
        {
            this.api = api;
            graph = new MapGraph();
            movement = new LogicInterface(api);
            if (api.User.UserData.MapId > 0) currentMap = api.User.UserData.MapId;
            faction = api.User.UserData.FactionId;
            if (api.User.PacketManager == null)
                throw new InvalidOperationException("TCP manager is required");
            api.User.PacketManager.OnHeroInit += HeroInit;
            api.User.PacketManager.OnGroupInvitation += GroupInvite;
            api.User.PacketManager.OnGateInit += GateInit;
            _ = Task.Run(Loop);
        }
        public void SetDestination(int mapId)
        {
            if (!graph.Maps.ContainsKey(mapId)) throw new ArgumentOutOfRangeException(nameof(mapId));
            lock (guard) { destination = mapId; nextAction = DateTime.MinValue; }
        }
        public void Start()
        {
            lock (guard) { if (destination < 0) throw new InvalidOperationException("Selecciona un mapa"); running = true; nextAction = DateTime.MinValue; }
            api.User.ActionState.Current = BotAction.Run;
        }
        public void Stop() { lock (guard) { running = false; Status = "Detenido"; } }
        private void HeroInit(object? sender, UserInitialize e)
        {
            lock (guard)
            {
                if (currentMap != e.MapId)
                {
                    currentMap = e.MapId;
                    lastJumpMap = -1;
                    nextAction = DateTime.UtcNow.AddSeconds(2);
                    lock (api.User.BasesPorts.Ports) api.User.BasesPorts.Ports.Clear();
                }
                faction = e.FactionID;
            }
        }
        private void GateInit(object? sender, GateInitialize e)
        {
            // The regular ObjectsHandler registers the gate in BasesPorts.
            // This event is retained so a caller can inspect received spawn records.
        }
        private async Task Loop()
        {
            while (!cancel.IsCancellationRequested)
            {
                try
                {
                    Tick();
                    await Task.Delay(450, cancel.Token);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Status = "Error: " + ex.Message;
                    await Task.Delay(2000);
                }
            }
        }
        private void Tick()
        {
            int map, target, team;
            lock (guard)
            {
                if (!running || currentMap < 0 || destination < 0) return;
                if (api.User.PacketManager?.Connected != true) { Status = "TCP desconectado"; return; }
                if (DateTime.UtcNow < nextAction) return;
                map = currentMap; target = destination; team = faction;
            }
            if (map == target)
            {
                if (StopOnArrival)
                {
                    lock (guard)
                    {
                        running = false;
                        Status = "Destino alcanzado: " + graph.Maps[map].name;
                    }
                    return;
                }
                var bounds = graph.Maps[map];
                if (bounds.width < 1200 || bounds.height < 1200) return;
                if (api.User.Position.Moving) return;
                int x, y;
                lock (random) { x = random.Next(500, bounds.width - 500); y = random.Next(500, bounds.height - 500); }
                api.User.ActionState.Current = BotAction.Run;
                movement.FlyToCorndinates(x, y);
                Status = "Moviendo aleatoriamente en " + bounds.name;
                lock (guard) nextAction = DateTime.UtcNow.AddSeconds(3);
                return;
            }
            var portal = graph.Next(map, target, team);
            if (portal == null) return;
            Gate? gate;
            lock (api.User.BasesPorts.Ports)
                gate = api.User.BasesPorts.Ports.Where(p => Math.Abs(p.X - portal.x) <= 150 && Math.Abs(p.Y - portal.y) <= 150)
                    .OrderBy(p => Math.Abs(p.X - portal.x) + Math.Abs(p.Y - portal.y)).FirstOrDefault();
            if (gate == null)
            {
                Status = "Esperando portal en " + graph.Maps[map].name;
                lock (guard) nextAction = DateTime.UtcNow.AddSeconds(2);
                return;
            }
            double dx = gate.X - api.User.Position.X, dy = gate.Y - api.User.Position.Y;
            if (Math.Sqrt(dx * dx + dy * dy) > 200)
            {
                api.User.ActionState.Current = BotAction.Run;
                movement.FlyToCorndinates(gate.X, gate.Y);
                Status = "Hacia portal " + gate.X + "," + gate.Y;
                lock (guard) nextAction = DateTime.UtcNow.AddSeconds(3);
                return;
            }
            if (gate.ID <= 0)
            {
                Status = "ID de portal no disponible";
                lock (guard) nextAction = DateTime.UtcNow.AddSeconds(3);
                return;
            }
            if (lastJumpMap != map)
            {
                api.User.PacketManager?.Send(new PortalSelect(gate.ID));
                api.User.PacketManager?.Send(new PortalJump());
                lastJumpMap = map;
                Status = "Saltando desde " + graph.Maps[map].name;
            }
            lock (guard) nextAction = DateTime.UtcNow.AddSeconds(8);
        }
        // Once on X-1, select an actual spawned portal by PortalType.
        // The spawn ID is session-specific and must never be confused with type.
        public async Task<bool> JumpByPortalTypeAsync(int portalType,
            CancellationToken token = default, bool allowSameMapTransition = false)
        {
            if (portalType <= 0) throw new ArgumentOutOfRangeException(nameof(portalType));
            if (api.User.PacketManager?.Connected != true)
                throw new InvalidOperationException("TCP desconectado.");
            Stop();
            int initialMap = CurrentMap;
            if (initialMap <= 0) throw new InvalidOperationException("Mapa actual desconocido.");
            DateTime deadline = DateTime.UtcNow.AddSeconds(45);
            DateTime nextMove = DateTime.MinValue;
            Gate? selected = null;
            while (DateTime.UtcNow < deadline)
            {
                token.ThrowIfCancellationRequested();
                if (CurrentMap != initialMap) return true;
                if (api.User.PacketManager?.Connected != true)
                    throw new InvalidOperationException("TCP desconectado durante el salto.");
                lock (api.User.BasesPorts.Ports)
                {
                    selected = api.User.BasesPorts.Ports
                        .Where(p => p.PortalType == portalType && p.ID > 0)
                        .OrderBy(p => Math.Pow(p.X - api.User.Position.X, 2) +
                                      Math.Pow(p.Y - api.User.Position.Y, 2))
                        .FirstOrDefault();
                }
                if (selected != null) break;
                Status = "Esperando portal de tipo " + portalType;
                await Task.Delay(500, token);
            }
            if (selected == null)
                throw new InvalidOperationException("No aparece portal del tipo " + portalType);
            while (DateTime.UtcNow < deadline)
            {
                token.ThrowIfCancellationRequested();
                if (CurrentMap != initialMap) return true;
                double dx = selected.X - api.User.Position.X;
                double dy = selected.Y - api.User.Position.Y;
                if (dx * dx + dy * dy <= 200 * 200) break;
                if (DateTime.UtcNow >= nextMove)
                {
                    api.User.ActionState.Current = BotAction.Run;
                    movement.FlyToCorndinates(selected.X, selected.Y);
                    Status = "Acercandose a portal tipo " + portalType;
                    nextMove = DateTime.UtcNow.AddSeconds(2);
                }
                await Task.Delay(350, token);
            }
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("No se alcanzo el portal tipo " + portalType);
            token.ThrowIfCancellationRequested();
            Status = "Saltando portal tipo " + portalType + " ID " + selected.ID;
            api.User.PacketManager?.Send(new PortalSelect(selected.ID));
            api.User.PacketManager?.Send(new PortalJump());
            if (allowSameMapTransition) { await Task.Delay(1200, token); return true; }
            DateTime jumpDeadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < jumpDeadline)
            {
                token.ThrowIfCancellationRequested();
                if (CurrentMap != initialMap) return true;
                await Task.Delay(350, token);
            }
            throw new TimeoutException("No se confirmo cambio de mapa tras saltar portal tipo " + portalType);
        }

        public void Dispose()
        {
            cancel.Cancel();
            if (api.User.PacketManager != null)
            {
                api.User.PacketManager.OnHeroInit -= HeroInit;
                api.User.PacketManager.OnGroupInvitation -= GroupInvite;
                api.User.PacketManager.OnGateInit -= GateInit;
            }
            cancel.Dispose();
        }
    }
}
