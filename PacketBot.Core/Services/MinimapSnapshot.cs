using System.Collections.Generic;
using System.Linq;
using FunnyProject.Navigation;
using FunnyProject.UserCollections.Objects;

namespace FunnyProject.Services
{
    // Everything the Windows minimap (RenderManager) draws, as plain data so
    // other front ends (the web panel) can draw the same picture. Game
    // coordinates; Width/Height give the map size to scale them.
    public sealed record MinimapDot(int X, int Y);

    public sealed record MinimapBar(int Value, int Max);

    // A barrier (NoAccess zone) as the game collides with it: a circle
    // (X, Y = centre, Radius) or a box (X, Y = top-left, Width x Height).
    // Inverted: the blocked area is a Margin-wide band around the outside.
    // A preferred NPC Killer zone of the current map (top-left, size).
    public sealed record MinimapRect(int X, int Y, int Width, int Height);

    // A station of the current map (base, repair dock, hangar…).
    public sealed record MinimapStation(string Name, string Short, int X, int Y);

    public sealed record MinimapBarrier(string Shape, int X, int Y, int Width, int Height,
        int Radius, bool Inverted, int Margin);

    public sealed record MinimapSnapshot(
        int MapId, string MapName, int Width, int Height,
        string HeroName, MinimapDot Hero, MinimapDot? Destination, MinimapDot? Pet,
        MinimapBar Hp, MinimapBar Shield,
        string? TargetName, MinimapDot? Target, MinimapBar? TargetHp, MinimapBar? TargetShield,
        bool TargetConfirmed,
        IReadOnlyList<MinimapDot> Npcs, IReadOnlyList<MinimapDot> Enemies, IReadOnlyList<MinimapDot> Allies,
        IReadOnlyList<MinimapDot> Boxes, IReadOnlyList<MinimapDot> Portals,
        IReadOnlyList<MinimapBarrier> Barriers,
        IReadOnlyList<MinimapRect> Preferred,
        IReadOnlyList<MinimapStation> Stations)
    {
        // Same lists and locks as RenderManager. Only bonus boxes, as there.
        // preferred: NPC Killer zones of the current map (from the settings).
        public static MinimapSnapshot From(Api api, IReadOnlyList<FunnyProject.Logic.PreferredZone>? preferred = null)
        {
            var user = api.User;
            int mapId = user.UserData.MapId;
            MapGraph.Default.Maps.TryGetValue(mapId, out var map);
            static MinimapDot[] Dots(List<Player> list)
            {
                lock (list) return list.Select(p => new MinimapDot(p.X, p.Y)).ToArray();
            }
            MinimapDot[] boxes;
            lock (user.Boxes._Boxes)
                boxes = user.Boxes._Boxes.Where(b => b.Type != null && b.Type.Contains("BONUS"))
                    .Select(b => new MinimapDot(b.X, b.Y)).ToArray();
            MinimapDot[] portals;
            lock (user.BasesPorts.Ports)
                portals = user.BasesPorts.Ports.Select(p => new MinimapDot(p.X, p.Y)).ToArray();
            var target = user.Target;
            var position = user.Position;
            return new MinimapSnapshot(
                mapId, map?.name ?? mapId.ToString(), map?.width ?? 21000, map?.height ?? 13100,
                user.UserData.InGameName ?? "",
                new MinimapDot(position.X, position.Y),
                position.Moving ? new MinimapDot(position.TargetX, position.TargetY) : null,
                user.PetMap.TryGetPosition(out int petX, out int petY) ? new MinimapDot(petX, petY) : null,
                new MinimapBar(user.ShipInfo.Hp, user.ShipInfo.MaxHp),
                new MinimapBar(user.ShipInfo.Shield, user.ShipInfo.MaxShield),
                target?.Name,
                target == null ? null : new MinimapDot(target.X, target.Y),
                // The spawn packet has no HP: no bar until real data arrives.
                target is { MaxHP: > 0 } ? new MinimapBar(target.HP, target.MaxHP) : null,
                target is { MaxShield: > 0 } ? new MinimapBar(target.Shield, target.MaxShield) : null,
                target != null && user.ConfirmedTargetId == target.ID,
                Dots(user.Players.Npcs), Dots(user.Players.EnemyPlayers), Dots(user.Players.AllyPlayers),
                boxes, portals,
                user.Zones.Barriers().Select(z => z.Shape == FunnyProject.UserCollections.PoiShape.Circle
                    ? new MinimapBarrier("circle", z.CenterX, z.CenterY, z.Width, z.Height, z.Radius, z.Inverted, z.Margin)
                    : new MinimapBarrier("box", z.Left, z.Top, z.Width, z.Height, 0, z.Inverted, z.Margin)).ToArray(),
                (preferred ?? System.Array.Empty<FunnyProject.Logic.PreferredZone>())
                    .Select(z => new MinimapRect(z.X, z.Y, z.Width, z.Height)).ToArray(),
                user.Stations.For(user.UserData.MapId)
                    .Select(s => new MinimapStation(s.Name, s.Short, s.X, s.Y)).ToArray());
        }
    }
}
