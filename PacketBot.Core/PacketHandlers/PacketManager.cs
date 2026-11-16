using FunnyProject.Commands.PacketSorter;
using FunnyProject.Commands.Read;
using FunnyProject.Commands.Write;
using FunnyProject.EventsHandler;
using Krypton_Core.Packets.Bytes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FunnyProject.PacketHandlers
{
    public class PacketManager : Client
    {
        public EventHandlerResources Res { get; set; }
        public HeroEventHandler Hero { get; set; }
        public ObjectsHandler ObjHandler { get; set; }
        public PlayersEventHandler PlHandler { get; set; }
        private SortPacket SortClass { get; set; }
        private DateTime _lastShieldPacketLogUtc = DateTime.MinValue;
        public PacketManager(Api api) : base(api)
        {
            Api = api;
            Res = new(Api);
            Hero = new(Api);
            PlHandler = new(Api); 
            ObjHandler = new(Api);
            SortClass = new(Api);
            Api.User.PetFuel = new FunnyProject.Logic.PetFuelManager(Api);
            Disconnected += (_, _) => Api.User.Telemetry.Save();
        }
        #region handlers
        //hero
        public event EventHandler<UserInitialize>? OnHeroInit;
        public event EventHandler<GroupInvitation>? OnGroupInvitation;
        public event EventHandler<HpUpdate>? OnHpUpdate;

        //resources
        public event EventHandler<BoxInitialize>? OnBoxInit;

        //objects
        public event EventHandler<GateInitialize>? OnGateInit;
        public event EventHandler<RemoveBox>? OnBoxRemove;

        //players
        public event EventHandler<PlayerInit>? OnPlayerInit;
        public event EventHandler<RemovePlayer>? OnPlayerRemove;
        public event EventHandler<RemovePlayerFromMap>? OnPlayerRemoveFromMap;
        public event EventHandler<PlayerMoved>? OnPlayerMove;
        #endregion

        private static byte[] Remaining(EndianBinaryReader read)
        {
            var stream = read.BaseStream;
            if (!stream.CanSeek) return Array.Empty<byte>();
            var body = new byte[Math.Max(0, stream.Length - stream.Position)];
            int done = 0, n;
            while (done < body.Length && (n = stream.Read(body, done, body.Length - done)) > 0) done += n;
            return body;
        }

        public override void Parse(EndianBinaryReader read)
        {
            var id = read.ReadInt16();
          
            switch(id)
            {
                case 596:
                    {
                        try
                        {
                            var invite = new GroupInvitation(read);
                            OnGroupInvitation?.Invoke(this, invite);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine("[Group] No se pudo interpretar invitacion 596: " + ex.Message);
                        }
                        break;
                    }
                case PetCooldownUpdate.ID:
                    {
                        var cooldown = new PetCooldownUpdate(read);
                        // Same packet for every item of the item bar.
                        Api.User.ItemTimers.Update(cooldown.Ability, cooldown.State, cooldown.Time,
                            cooldown.MaxTime, cooldown.Activatable, DateTime.UtcNow);
                        if (cooldown.Ability == "kamikaze_pet_hotkey")
                        {
                            bool exploded = Api.User.PetCooldown.UpdateFromServer(
                                cooldown.Time, cooldown.Activatable, cooldown.State);
                            if (exploded)
                                Api.User.PetMap.Clear();

                        }

                        break;
                    }
                case UserInitialize.ID:
                    {
                        var hero = new UserInitialize(read);
                        Api.User.Telemetry.UserId = Api.User.UserData.ID;
                        OnHeroInit?.Invoke(this, hero);
                        Api.User.BoosterRequestsReady = true;
                        break;
                    }
                case NpcHealthUpdate.ID:
                    {
                        var update = new NpcHealthUpdate(read);
                        // 11468 is the server's ship selection: the target is confirmed.
                        if (update.PlayerID > 0) Api.User.ConfirmedTargetId = update.PlayerID;
                        // Validate HP and shield independently. A malformed or
                        // transient shield value must not block HP updates.
                        if (update.PlayerID > 0)
                        {
                            lock (Api.User.Players.AllPlayers)
                            {
                                var player = Api.User.Players.AllPlayers.Find(
                                    p => p.ID == update.PlayerID && p.IsNpc);
                                if (player != null)
                                {
                                    if (update.MaxHP > 0 && update.HP >= 0 &&
                                        update.HP <= update.MaxHP)
                                    {
                                        player.HP = update.HP;
                                        player.MaxHP = update.MaxHP;
                                    }
                                    if (update.MaxShield > 0 && update.Shield >= 0 &&
                                        update.Shield <= update.MaxShield)
                                    {
                                        player.Shield = update.Shield;
                                        player.MaxShield = update.MaxShield;
                                    }
                                    else if (update.MaxShield == 0 && update.Shield == 0)
                                    {
                                        player.Shield = 0;
                                        player.MaxShield = 0;
                                    }

                                }
                            }
                        }
                        break;
                    }
                case UpdateHp.ID:
                    {
                        // This packet is not the selected NPC status. Do not
                        // overwrite NPC health with its unrelated values.
                        _ = new UpdateHp(read);
                        break;
                    }
                case 4224:
                    {
                        var message = read.ReadString();
                        Api.User.Telemetry.RecordText(message);
                        if (message.StartsWith("0|WG_BOOSTERS|v1|", StringComparison.Ordinal))
                        {
                            Api.User.Boosters.TryReceive(message, id, Api.User.UserData.ID);
                        }
                        else if (!message.Contains("0|n|s|end"))
                        {
                            SortClass.Sort(message);
                        }
                        break;
                    }
                case BoxInitialize.ID:
                    {
                        var box = new BoxInitialize(read);
                        OnBoxInit?.Invoke(this, box);
                        break;
                    }
                case GateInitialize.ID:
                    {
                        var gate = new GateInitialize(read);
                        OnGateInit?.Invoke(this, gate);
                        break;
                    }
                case RemoveBox.ID:
                    {
                        var box = new RemoveBox(read);
                        OnBoxRemove?.Invoke(this , box);
                        break;
                    }
                case PlayerInit.ID:
                    {
                        var player = new PlayerInit(read);
                        OnPlayerInit?.Invoke(this,player);
                        break;
                    }
                case RemovePlayer.ID:
                    {
                        var player = new RemovePlayer(read);
                        Api.User.PetMap.ClearIfMatches(player.UserID);
                        OnPlayerRemove?.Invoke(this, player);
                        break;
                    }
                case RemovePlayerFromMap.ID:
                    {
                        var player = new RemovePlayerFromMap(read);
                        Api.User.PetMap.ClearIfMatches(player.UserID);
                        OnPlayerRemoveFromMap?.Invoke(this, player);
                        break;
                    }
                case PlayerMoved.ID:
                    {
                        var player = new PlayerMoved(read);
                        bool known;
                        lock (Api.User.Players.AllPlayers)
                            known = Api.User.Players.AllPlayers.Any(p => p.ID == player.UserID);
                        if (!Api.User.PetMap.TryMove(player.UserID, player.ToX, player.ToY, known))
                            OnPlayerMove?.Invoke(this, player);
                        break;
                    }
                case MapStation.ID:
                    Api.User.Stations.OnServerStation(new MapStation(read), Api.User.UserData.MapId);
                    break;
                case ItemStatus.ID:
                    Api.User.Items.Update(new ItemStatus(read), DateTime.UtcNow);
                    break;
                case 30106:
                    // Whole item bar at login (client §_-V4m§): one item state per item.
                    Api.User.Items.Load(ItemStatus.FindAll(Remaining(read)), DateTime.UtcNow);
                    break;
                case MapAddPoi.ID:
                    {
                        var poi = new MapAddPoi(read);
                        Api.User.Zones.OnServerZone(poi, Api.User.UserData.MapId);
                        break;
                    }
                case MapRemovePoi.ID:
                    {
                        var poi = new MapRemovePoi(read);
                        Api.User.Zones.Remove(poi.Id);
                        break;
                    }
                case ShieldUpdate.ID:
                    {
                        ShieldUpdate shd = new(read);
                        break;
                    }
                case HpUpdate.ID:
                    {
                        HpUpdate hp = new(read);
                        OnHpUpdate?.Invoke(this, hp);
                        break;
                    }
                case ShipInfoUpdate.ID:
                    {
                        ShipInfoUpdate ship = new(read);
                        break;
                    }
                default:
                    {
                        // Not in the normal log: counted for docs/PROTOCOL.md.
                        Api.User.Telemetry.RecordUnknown(id, Remaining(read), Api.User.UserData.MapId);
                        break;
                    }
            }
        }
    }
}
