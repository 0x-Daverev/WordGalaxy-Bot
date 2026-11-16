using System.Collections;
using System.Drawing.Imaging;
using System.Reflection;
using FunnyProject;
using FunnyProject.Forms;
using FunnyProject.Logic;
using FunnyProject.UserCollections;
using FunnyProject.UserCollections.Objects;
using FunnyProject.UserCollections.Resources;

// Usage: PacketBot.Screenshot <out.png> [minimap.jpg]
// Demo state: map 2BL (307) with its 24 barriers as the server sends them,
// BL NPCs, bonus boxes, two preferred zones and the ship fighting a target.
// Nothing connects to the game.
internal static class Program
{
    private const int Map = 307; // 2BL

    [STAThread]
    private static void Main(string[] args)
    {
        string output = args.Length > 0 ? args[0] : "packetbot-windows.png";
        ApplicationConfiguration.Initialize();
        FunnyProject.Abstractions.UserAlerts.Handler = _ => { };

        var user = new User("PacketBot", "", "demo");
        var api = new Api(user);
        SaveDemoSettings(user);
        FillDemoState(user);
        if (args.Length > 1) PreloadMinimap(args[1]);

        var window = new UserWindow(api)
        {
            StartPosition = FormStartPosition.Manual, Location = new Point(0, 0), FormBorderStyle = FormBorderStyle.None
        };
        var timer = new System.Windows.Forms.Timer { Interval = 6000 };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            // From the screen: DrawToBitmap does not capture the owner-drawn
            // minimap on every host (Wine).
            var area = window.RectangleToScreen(window.ClientRectangle);
            using var bitmap = new Bitmap(area.Width, area.Height);
            using (var g = Graphics.FromImage(bitmap)) g.CopyFromScreen(area.Location, Point.Empty, area.Size);
            bitmap.Save(output, ImageFormat.Png);
            Application.Exit();
        };
        window.Shown += (_, _) =>
        {
            // What the window shows with NPC Killer running on 2BL (the demo
            // cannot start the bot: there is no connection).
            SetText(window, "ggProgress", "NPC Killer: 2BL\nBuscando NPCs seleccionados");
            SetText(window, "botButton", "Parar bot");
            timer.Start();
        };
        Application.Run(window);
    }

    private static void SetText(UserWindow window, string field, string text)
    {
        if (typeof(UserWindow).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(window) is Control c)
            c.Text = text;
    }

    private static void SaveDemoSettings(User user)
    {
        var s = BotSettingsStore.Load(user.Server, user.Username);
        s.SelectedModule = "NPC Killer";
        s.NpcKillerMapId = Map;
        s.NpcKillerLaser = "ucb-100";
        s.NpcKillerEnergyLeech = true;
        s.NpcKillerTargetsByMap[Map] = new List<string> { "-=[ Kristallon ]=-", "-=[ Kristallin ]=-" };
        s.NpcKillerZonesByMap = new()
        {
            [Map] = new List<PreferredZone>
            {
                new() { X = 10600, Y = 6000, Width = 3000, Height = 3000 },
                new() { X = 15000, Y = 600, Width = 3800, Height = 2500 },
            }
        };
        BotSettingsStore.Save(user.Server, user.Username, s);
    }

    private static void FillDemoState(User user)
    {
        user.UserData.MapId = Map;
        user.UserData.InGameName = "PacketBot";
        user.ShipInfo.MaxHp = 256000; user.ShipInfo.Hp = 231400;
        user.ShipInfo.MaxShield = 180000; user.ShipInfo.Shield = 152300;
        user.Position.X = 11900; user.Position.Y = 7700;

        // The 24 barriers of 2BL, as logged from the server (inverted lines and points).
        int[][] walls =
        {
            new[] { 14400, 5300, 0, 0 }, new[] { 1000, 10800, 600, 0 }, new[] { 13600, 6300, 0, 100 },
            new[] { 16500, 3400, 0, 0 }, new[] { 14700, 4300, 0, 0 }, new[] { 16500, 4900, 0, 0 },
            new[] { 17500, 3100, 0, 0 }, new[] { 4700, 2300, 0, 3100 }, new[] { 500, 9400, 8600, 0 },
            new[] { 9700, -1, 0, 5401 }, new[] { 17400, 11300, 0, 200 }, new[] { -385, 3800, 3385, 0 },
            new[] { 1400, 1700, 5000, 0 }, new[] { 1800, 11500, 0, 0 }, new[] { 18900, 11200, 0, 0 },
            new[] { 10300, 9400, 5700, 0 }, new[] { 5300, 5400, 3800, 0 }, new[] { 17200, 9400, 2800, 0 },
            new[] { 3600, 11500, 0, 0 }, new[] { 3600, 10400, 0, 0 }, new[] { 18100, 10400, 0, 400 },
            new[] { 19200, 2100, 200, 0 }, new[] { 17300, 5500, 0, 0 }, new[] { 18300, 4400, 0, 0 },
        };
        foreach (var w in walls)
            user.Zones.Add(new PoiZone("", 5, PoiShape.Rectangle, true, true,
                new[] { (w[0], w[1]), (w[0] + w[2], w[1] + w[3]) }, 0));

        var npcs = new (int X, int Y, string Name)[]
        {
            (12600, 7300, "-=[ Kristallon ]=-"), (11200, 8400, "-=[ Kristallin ]=-"),
            (12900, 8600, "-=[ Kristallin ]=-"), (16200, 1800, "-=[ Kristallin ]=-"),
            (17600, 2600, "-=[ Kristallon ]=-"), (6300, 7600, "-=[ Kristallin ]=-"),
            (3200, 6400, "-=[ Kristallin ]=-"), (14800, 11600, "-=[ Kristallon ]=-"),
        };
        int id = 9000;
        foreach (var n in npcs)
            user.Players.Add(new Player { ID = ++id, Name = n.Name, X = n.X, Y = n.Y, IsNpc = true, HP = 400000, MaxHP = 400000 }, true);

        var target = new Player { ID = 9001, Name = "-=[ Kristallon ]=-", X = 12600, Y = 7300, IsNpc = true,
            HP = 248000, MaxHP = 400000, Shield = 61000, MaxShield = 300000 };
        user.Players.Add(target, true);
        user.Target = target;
        user.ConfirmedTargetId = target.ID;
        user.Position.TargetX = 12100; user.Position.TargetY = 6900;
        user.Position.Moving = true;

        var random = new Random(4);
        for (int i = 0; i < 26; i++)
            user.Boxes.Add(new Box("demo" + i, random.Next(800, 20200), random.Next(800, 12400), "BONUS_BOX"));
    }

    // RenderManager downloads the minimap picture; here it is given directly.
    private static void PreloadMinimap(string path)
    {
        var type = typeof(UserWindow).Assembly.GetType("FunnyProject.GUIManagers.RenderManager")
            ?? throw new InvalidOperationException("RenderManager no encontrado");
        var cache = (IDictionary)type.GetField("minimapCache", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        lock (cache) cache[Map] = new Bitmap(path);
    }
}
