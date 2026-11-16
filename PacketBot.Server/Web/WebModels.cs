namespace PacketBot.Server.Web;

// JSON shapes of the web panel. The PHPSESSID and the Telegram token are
// never part of them.
public sealed record InstanceView(int Number, string Name, int UserId, bool Running, bool Active,
    bool Tcp, int MapId, string MapName, string Status);

public sealed record OptionView(string Value, string Label);

public sealed record CheckView(string Name, bool Selected);

public sealed record SettingsView(
    string Module,
    int CollectMapId, IReadOnlyList<CheckView> Boxes,
    int NpcKillerMapId, string NpcKillerAmmunition, IReadOnlyList<CheckView> Npcs, bool EnergyLeech,
    string Gate, int Spins, string GateAmmunition, string AttackMode,
    string RunFormation, string AttackFormation, string FinalFormation,
    IReadOnlyList<CheckView> AutoBuy, bool AcceptInvites, string Path,
    IReadOnlyList<OptionView> Maps, IReadOnlyList<string> Ammunition, IReadOnlyList<string> Gates,
    IReadOnlyList<int> SpinOptions, IReadOnlyList<string> Formations);

public sealed record ChangeRequest(string Key, string Value);

public sealed record ChecklistRequest(List<string> Selected);

public sealed record ItemView(string Category, string Name, double Amount, double? Maximum);

public sealed record ZoneRequest(int X1, int Y1, int X2, int Y2);

public sealed record PointRequest(int X, int Y);

public sealed record LoginRequest(string Password);

public sealed record ReplyView(bool Ok, string Message);
