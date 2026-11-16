using System;
using System.Collections.Generic;
namespace FunnyProject
{
    internal sealed class BotSettings
    {
        public string SelectedModule { get; set; } = "Collect";
        public string GalaxyGateName { get; set; } = "";
        public int Spins { get; set; } = 100;
        // Laser ammunition of Galaxy Gates (name kept for existing settings files).
        public string Laser { get; set; } = "ucb-100";
        // Laser ammunition of NPC Killer, independent of Galaxy Gates.
        public string NpcKillerLaser { get; set; } = "ucb-100";

        public static readonly string[] LaserAmmunition = { "lcb-10", "mcb-25", "mcb-50", "ucb-100" };
        public static bool IsValidLaser(string? ammo) => System.Array.IndexOf(LaserAmmunition, ammo) >= 0;
        public string AttackMode { get; set; } = "both";
        public string RunFormation { get; set; } = "";
        public string AttackFormation { get; set; } = "";
        public string GgFinalNpcFormation { get; set; } = "";
        public int CollectMapId { get; set; }
        public Dictionary<int, List<string>> CollectTypesByMap { get; set; } = new();
        public int NpcKillerMapId { get; set; }
        public Dictionary<int, List<string>> NpcKillerTargetsByMap { get; set; } = new();
        // Preferred (not mandatory) NPC Killer areas per map.
        public Dictionary<int, List<FunnyProject.Logic.PreferredZone>> NpcKillerZonesByMap { get; set; } = new();
        // Buy Energy Leech while NPC Killer runs (opt-in).
        public bool NpcKillerEnergyLeech { get; set; }
        // Persisted before each Energy Leech purchase (no repeat after a restart).
        public DateTime? EnergyLeechAttemptUtc { get; set; }
        // All automatic purchases are opt-in, independently for each item.
        public Dictionary<int, bool> AutoBuyBoosters { get; set; } = new();
        // Persisted before each POST to avoid repeated spending after a restart.
        public Dictionary<int, DateTime> BoosterPurchaseAttemptUtc { get; set; } = new();
        public bool AutoReconnect { get; set; }
        public bool AutoAcceptInvites { get; set; } = true;
        public int? AllowedInviterId { get; set; }
    }
}
