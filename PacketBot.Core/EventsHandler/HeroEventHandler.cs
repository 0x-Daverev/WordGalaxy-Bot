using FunnyProject.Commands.Read;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FunnyProject.EventsHandler
{
    public class HeroEventHandler
    {
        Api Api { get; init; }
        private int _deathHandled;
        public HeroEventHandler(Api api)
        {
            Api = api;

            
        }
        public void RegisterEvents()
        {
            Api.User.PacketManager!.OnHeroInit += (s, HeroInitData) =>
            {
                InitializeHero(HeroInitData);
                // A new hero snapshot (e.g. after the respawn relogin) re-arms
                // death detection even if no HP update above zero follows.
                System.Threading.Interlocked.Exchange(ref _deathHandled, 0);
            };
            Api.User.PacketManager!.OnHpUpdate += (s , e) =>
            {
                UpdateHeroHp(e);
            };
        

        }

        private void UpdateHeroHp(HpUpdate e)
        {
            Api.User.ShipInfo.Hp = e.Hp;
            Api.User.ShipInfo.MaxHp = e.MaxHp;
            if (e.Hp > 0)
            {
                System.Threading.Interlocked.Exchange(ref _deathHandled, 0);
            }
            else if (e.Hp == 0 && e.MaxHp > 0 &&
                System.Threading.Interlocked.CompareExchange(ref _deathHandled, 1, 0) == 0)
            {
                // Do not send or close the socket from inside its own receive loop.
                _ = Task.Run(RespawnAsync);
            }
        }

        // WordGalaxy respawns the ship after a new TCP connection and Login.
        // Re-sending Login on the same connection is not enough (verified on
        // the real server on 11-11-2026: no respawn after 10 s).
        private async Task RespawnAsync()
        {
            Console.WriteLine("[Hero] Vida 0 confirmada por paquete HP; reiniciando solo TCP.");
            try
            {
                if (Api.User.PacketManager is { } manager)
                    await manager.RestartTcpAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine("[Hero] Error al reiniciar TCP: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void InitializeHero(UserInitialize e)
        {
            var stats = Api.User.Statistics;
            var position = Api.User.Position;
            stats.Uridium = e.Uridium;
            stats.Credits = e.Credits;
            stats.Experience = e.Experience;
            stats.Level = e.Level;
            // Every HeroInitialize is a new authoritative map snapshot,
            // including a TCP relogin to the SAME map. Clear stale entities
            // before the server sends the new spawn packets.
            lock (Api.User.Players.AllPlayers)
            {
                foreach (var player in Api.User.Players.AllPlayers)
                    Api.Tweener.TargetCancel(player);
            }
            Api.User.Players.Clear();
            Api.User.ResetFormationDiagnostics();
            // Boxes are scoped to the current map snapshot, including TCP relogins.
            Api.User.Boxes.Clear();
            Api.User.SelectedBox = null;
            Api.User.PetMap.Clear();
            Api.User.Target = null;
            // A hero initialization starts a fresh map snapshot, even if MapId
            // stays unchanged (relogin / another GG wave on the same map).
            lock (Api.User.BasesPorts.Ports)
                Api.User.BasesPorts.Ports.Clear();
            // Zones (barriers) belong to a map. On the same map (respawn,
            // relogin, GG wave) the server re-sends them and each one replaces
            // the same zone (MapZones.Key).
            if (Api.User.UserData.MapId != e.MapId)
            {
                Api.User.Zones.Clear();
                Api.User.Stations.Clear();
            }
            Api.User.UserData.MapId = e.MapId;
            if (!string.IsNullOrWhiteSpace(e.Username))
                Api.User.UserData.InGameName = e.Username;
            stats.Honor = e.Honnor;
            Api.User.UserData.FactionId = e.FactionID;
            Api.User.ShipInfo.Hp = e.Hp;
            Api.User.ShipInfo.MaxHp = e.MaxHp;
            if (e.Hp > 0)
                System.Threading.Interlocked.Exchange(ref _deathHandled, 0);
            Api.User.ShipInfo.Shield = e.Shield;
            lock (Api.User.Position)
            {
                position.Speed = e.Speed;
                position.X = e.X;
                position.Y = e.Y;
             
            }
        }
    }
}
