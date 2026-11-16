using FunnyProject.PacketHandlers;
using FunnyProject.UserCollections;
using FunnyProject.UserCollections.Objects;
using FunnyProject.UserCollections.Resources;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.Pkcs;
using System.Text;
using System.Threading.Tasks;

namespace FunnyProject
{
    public class User
    {
        public BotActionState ActionState { get; } = new();
        // Set after the server initializes this hero on the current TCP connection.
        public volatile bool BoosterRequestsReady;

        private string[] availableDroneFormations = Array.Empty<string>();
        public string[] AvailableDroneFormations =>
            System.Threading.Volatile.Read(ref availableDroneFormations);
        public void UpdateAvailableDroneFormations(string[] ids) =>
            System.Threading.Volatile.Write(ref availableDroneFormations, ids);
        // Request != acknowledgement. These values must never be interpreted
        // as the currently equipped drone formation.
        // Updated only from the server's current-formation packet (12754).
        private string currentDroneFormation = "";
        private long currentDroneFormationSeenUtcTicks;
        public string CurrentDroneFormation =>
            System.Threading.Volatile.Read(ref currentDroneFormation);
        public DateTime CurrentDroneFormationSeenUtc =>
            new DateTime(System.Threading.Interlocked.Read(ref currentDroneFormationSeenUtcTicks), DateTimeKind.Utc);

        public void UpdateCurrentDroneFormation(string id)
        {
            System.Threading.Volatile.Write(ref currentDroneFormation, id);
            System.Threading.Interlocked.Exchange(ref currentDroneFormationSeenUtcTicks, DateTime.UtcNow.Ticks);
        }
        private string lastRequestedFormation = "";
        private string lastObservedFormation = "";
        private int lastObservedFormationPacket;
        public string LastRequestedFormation =>
            System.Threading.Volatile.Read(ref lastRequestedFormation);
        public string LastObservedFormation =>
            System.Threading.Volatile.Read(ref lastObservedFormation);
        public int LastObservedFormationPacket =>
            System.Threading.Volatile.Read(ref lastObservedFormationPacket);

        private long lastFormationRequestUtcTicks;
        public DateTime LastFormationRequestUtc =>
            new DateTime(System.Threading.Interlocked.Read(ref lastFormationRequestUtcTicks), DateTimeKind.Utc);

        public void RecordFormationRequest(string id)
        {
            System.Threading.Volatile.Write(ref lastRequestedFormation, id);
            System.Threading.Interlocked.Exchange(ref lastFormationRequestUtcTicks, DateTime.UtcNow.Ticks);
        }

        public void ObserveFormationPacket(string id, int packetId)
        {
            if (id == LastObservedFormation && packetId == LastObservedFormationPacket)
                return;
            System.Threading.Volatile.Write(ref lastObservedFormation, id);
            System.Threading.Volatile.Write(ref lastObservedFormationPacket, packetId);
        }

        public void ResetFormationDiagnostics()
        {
            System.Threading.Volatile.Write(ref currentDroneFormation, "");
            System.Threading.Interlocked.Exchange(ref currentDroneFormationSeenUtcTicks, 0);
            System.Threading.Volatile.Write(ref lastRequestedFormation, "");
            System.Threading.Interlocked.Exchange(ref lastFormationRequestUtcTicks, 0);
            System.Threading.Volatile.Write(ref lastObservedFormation, "");
            System.Threading.Volatile.Write(ref lastObservedFormationPacket, 0);
        }

        public int Uridium { get; set; }
        public string? Username { get; set; }
        public string? Password { get; set; }
        public string? Server { get; set; }
        public PacketManager? PacketManager { get; set; }

        public UserData UserData = new();
        public Position Position = new();
        // Zones (POI) of the current map; NoAccess zones are barriers.
        public MapZones Zones = new();
        // Item bar timers from the server (packet 31208), e.g. Energy Leech.
        public ItemTimers ItemTimers = new();
        // Unhandled server packets and text command keys (docs/PROTOCOL.md).
        public FunnyProject.Services.PacketTelemetry Telemetry = new();
        // Stations of the map (packet 13718) and what the ship has (30106, 12754).
        public Stations Stations = new();
        public ShipItems Items = new();
        // NPC whose selection the server last confirmed (packet 11468; the
        // client only takes a target, with its HP and shield, from it). 0 =
        // none. Reset when the bot selects a different NPC.
        public volatile int ConfirmedTargetId;
        public bool TargetConfirmed => Target != null && ConfirmedTargetId == Target.ID;
        public Statistics Statistics = new();
        public Boxes Boxes = new();
        public BasesPorts BasesPorts = new();
        public Players Players = new();
        public ShipInfo ShipInfo = new();
        public FunnyProject.Logic.BoosterState Boosters = new();
        public FunnyProject.Logic.GalaxyGatePetState PetCooldown = new();
        public FunnyProject.Logic.PetMapState PetMap = new();
        public FunnyProject.Logic.PetFuelManager? PetFuel { get; set; }
        

        public Box? SelectedBox { get; set; }
        public Player? Target { get; set; }


        public User(string name , string password , string server)
        {
            Username = name;
            Password = password;
            Server = server;

        }

    }
}
