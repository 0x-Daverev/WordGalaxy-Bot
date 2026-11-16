using FunnyProject;
using FunnyProject.Navigation;
using FunnyProject.Sessions;
using PacketBot.Core.Tests;
using PacketBot.Server;
using Xunit;

namespace PacketBot.Server.Tests;

// End to end without a real server: fake /map + loopback fake game server +
// fake Telegram Bot API, driving Collect through the shared dispatcher.
public class ServerTelegramTests : IAsyncLifetime
{
    private const string Token = "123456:TEST-token";
    private const long Chat = 555;

    // A unique account so the per-account settings file never collides with
    // a real one; deleted afterwards.
    private readonly int userId = 900_000_000 + Random.Shared.Next(1_000_000);
    private readonly FakeGameServer gameServer = new();
    private readonly FakeTelegram telegram = new(Token);
    private readonly ServerInstanceHost host;
    private readonly TelegramWorker worker;
    private readonly MapGraph.Map map = new MapGraph().Maps.Values.Single(m => m.name == "1-1");
    private ServerBotInstance instance = null!;
    private FakeGameServer.Connection connection = null!;
    private long nextUpdate = 1;

    public ServerTelegramTests()
    {
        var web = new WordGalaxyWebClient(cookies => new FakeWordGalaxyHttp(cookies,
            (_, _) => FakeWordGalaxyHttp.Html(FakeWordGalaxyHttp.MapHtml(userId: userId, host: "127.0.0.1"))));
        host = new ServerInstanceHost(new InstanceManager(web, gameServer.Port));
        var options = new ServerOptions(new[] { "php" }, false, TimeSpan.FromSeconds(5), null, Token, Chat);
        worker = new TelegramWorker(options, host, telegram, null);
    }

    public async Task InitializeAsync()
    {
        instance = await host.OpenAsync("test-php-session-0001", CancellationToken.None);
        connection = gameServer.NextConnection();
        instance.Session.Api.User.UserData.MapId = map.id; // already on the Collect map
        await worker.StartAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await worker.StopAsync(CancellationToken.None);
        await host.StopAllAsync(TimeSpan.FromSeconds(5));
        string settings = instance.SettingsPath;
        host.Dispose();
        gameServer.Dispose();
        try { File.Delete(settings); } catch (IOException) { }
    }

    private void Save(BotSettings settings) =>
        BotSettingsStore.Save("wordgalaxy", userId.ToString(), settings);

    private void Command(string text) =>
        telegram.QueueUpdates(FakeTelegram.Message(nextUpdate++, Chat, "private", text));

    [Fact]
    public async Task CollectIsStartedAndStoppedFromTelegram()
    {
        Save(new BotSettings
        {
            SelectedModule = "Collect",
            CollectMapId = map.id,
            CollectTypesByMap = new() { [map.id] = new() { "BONUS_BOX" } }
        });

        Command("/status");
        string status = await telegram.WaitForSentAsync("Instancia 1");
        Assert.Contains("Bot: 🔴 Parado", status);
        Assert.Contains("TCP: 🟢 Conectado", status);
        Assert.Contains("Modulo: Collect", status);

        Command("/startbot 1");
        await telegram.WaitForSentAsync("Inicio de Collect solicitado.");
        await FakeGameServer.WaitUntil(() => instance.BotRunning);
        Assert.True(instance.Session.PacketManager.AutoReconnectEnabled);

        Command("/status");
        await telegram.WaitForSentAsync("Actividad: Collect: 1-1 | Buscando cajas seleccionadas");

        Command("/startbot 1");
        await telegram.WaitForSentAsync("El bot ya esta activo.");

        Command("/stopbot 1");
        await telegram.WaitForSentAsync("Bot detenido.");
        Assert.False(instance.BotRunning);
        Assert.False(instance.Session.PacketManager.AutoReconnectEnabled);
        // Stopped on request: no alert, even after a few alert checks.
        await Task.Delay(2_500);
        lock (telegram.Sent) Assert.DoesNotContain(telegram.Sent, m => m.Contains("PacketBot detenido"));
    }

    [Fact]
    public async Task MissingMapAndUnsupportedModulesAreReported()
    {
        Save(new BotSettings { SelectedModule = "Collect", CollectMapId = 0 });
        Command("/startbot 1");
        await telegram.WaitForSentAsync("Selecciona el mapa antes de iniciar Collect");

        Save(new BotSettings { SelectedModule = "Otro" });
        Command("/startbot 1");
        await telegram.WaitForSentAsync("Modulo desconocido: Otro.");
        Assert.False(instance.BotRunning);
    }

    [Fact]
    public async Task StartbotReusesTheModuleStartedWithStartCollect()
    {
        // Windows left GGs selected; the server was started with --start collect.
        Save(new BotSettings
        {
            SelectedModule = "GGs",
            CollectMapId = map.id,
            CollectTypesByMap = new() { [map.id] = new() { "BONUS_BOX" } }
        });
        Assert.Equal("Inicio de Collect solicitado.", instance.Start("collect"));
        await FakeGameServer.WaitUntil(() => instance.BotRunning);

        Command("/stopbot 1");
        await telegram.WaitForSentAsync("Bot detenido.");
        Command("/status");
        await telegram.WaitForSentAsync("Modulo: Collect");
        Command("/startbot 1");
        await telegram.WaitForSentAsync("Inicio de Collect solicitado.");
        await FakeGameServer.WaitUntil(() => instance.BotRunning);
    }

    [Fact]
    public async Task NpcsShowsSavedTargetsAndNpcsSeenOnTheMap()
    {
        Save(new BotSettings
        {
            SelectedModule = "NPC Killer",
            NpcKillerMapId = map.id,
            NpcKillerTargetsByMap = new() { [map.id] = new() { "-=[ Streuner ]=-" } }
        });
        instance.Session.Api.User.Players.Add(new FunnyProject.UserCollections.Objects.Player
            { ID = 77, Name = "-=[ Lordakia ]=-", IsNpc = true }, true);
        // Observations are sampled every second.
        await FakeGameServer.WaitUntil(() => instance.Session.Api.Observations.NpcNames(map.id).Count > 0);
        Command("/npcs 1");
        string reply = await telegram.WaitForSentAsync("NPC Killer");
        Assert.Contains("Mapa: 1-1", reply);
        Assert.Contains("1. ⬜ -=[ Lordakia ]=-", reply);   // seen, not selected
        Assert.Contains("2. ✅ -=[ Streuner ]=-", reply);   // saved target
        Assert.Contains("/npc 1 <numeros>", reply);
    }

    [Fact]
    public async Task NpcTargetsAreChangedFromTelegramAndAppliedLive()
    {
        Save(new BotSettings
        {
            SelectedModule = "NPC Killer",
            NpcKillerMapId = map.id,
            NpcKillerLaser = "mcb-25",
            NpcKillerTargetsByMap = new() { [map.id] = new() { "-=[ Streuner ]=-" } }
        });
        instance.Session.Api.User.Players.Add(new FunnyProject.UserCollections.Objects.Player
            { ID = 78, Name = "-=[ Lordakia ]=-", IsNpc = true }, true);
        await FakeGameServer.WaitUntil(() => instance.Session.Api.Observations.NpcNames(map.id).Count > 0);

        Assert.Equal("Inicio de NPC Killer solicitado.", instance.Start("npc"));
        await FakeGameServer.WaitUntil(() => instance.BotRunning);

        Command("/npcs 1");
        string list = await telegram.WaitForSentAsync("1. ⬜ -=[ Lordakia ]=-");
        Assert.Contains("2. ✅ -=[ Streuner ]=-", list);
        Assert.Contains("Municion: MCB-25", list);

        Command("/npc 1 1 2"); // add Lordakia, remove Streuner
        await telegram.WaitForSentAsync(
            "Objetivos de NPC Killer en 1-1: -=[ Lordakia ]=-. Aplicado al bot en marcha.");
        var saved = BotSettingsStore.Load("wordgalaxy", userId.ToString());
        Assert.Equal(new[] { "-=[ Lordakia ]=-" }, saved.NpcKillerTargetsByMap[map.id]);

        Command("/npc 1 9");
        await telegram.WaitForSentAsync("Numero no valido: 9.");
    }

    [Fact]
    public async Task GalaxyGatesRunFromTelegramWithSavedSettings()
    {
        var gg = new FakeGalaxyGateApi();
        instance.GalaxyGateApiFactory = _ => gg;
        Save(new BotSettings
        {
            SelectedModule = "GGs",
            GalaxyGateName = "Beta",
            Spins = 5,
            Laser = "mcb-50",
            AttackMode = "laser"
        });

        Command("/startbot 1");
        await telegram.WaitForSentAsync("Inicio del bot solicitado.");
        await FakeGameServer.WaitUntil(() => gg.Writes().Length > 0);
        Assert.Equal("spin 3 x5", gg.Writes()[0]); // gate id resolved from the saved name

        Command("/status");
        string status = await telegram.WaitForSentAsync("Modulo: GGs");
        Assert.Contains("Puerta: Beta", status);
        Assert.Contains("Ataque: Laser", status);
        Assert.Contains("Municion: MCB-50", status);

        Command("/stopbot 1");
        await telegram.WaitForSentAsync("Bot detenido.");
    }

    [Fact]
    public async Task GalaxyGatesNeedASavedGate()
    {
        Save(new BotSettings { SelectedModule = "GGs", GalaxyGateName = "" });
        Command("/startbot 1");
        await telegram.WaitForSentAsync("Selecciona una GG y los spins antes de iniciarlo");
        Assert.False(instance.BotRunning);
    }

    private int CountSent(short id)
    {
        byte[] bytes = connection.Snapshot();
        int count = 0;
        for (int offset = 0; offset + 4 <= bytes.Length;)
        {
            int length = (bytes[offset] << 8) | bytes[offset + 1];
            if ((short)((bytes[offset + 2] << 8) | bytes[offset + 3]) == id) count++;
            offset += 2 + length;
        }
        return count;
    }

    [Fact]
    public async Task FormationsAndBoosterPollingRunWithoutAWindow()
    {
        var user = instance.Session.Api.User;
        user.UpdateAvailableDroneFormations(new[] { "drill", "arrow" });
        Save(new BotSettings
        {
            SelectedModule = "Collect",
            CollectMapId = map.id,
            CollectTypesByMap = new() { [map.id] = new() { "BONUS_BOX" } },
            RunFormation = "arrow"
        });
        user.BoosterRequestsReady = true;
        await FakeGameServer.WaitUntil(() => CountSent(FunnyProject.Commands.Write.OpenBoosters.ID) == 1 &&
            CountSent(FunnyProject.Commands.Write.RequestBoosters.ID) == 1);

        Assert.Equal("Inicio de Collect solicitado.", instance.Start("collect"));
        await FakeGameServer.WaitUntil(() => instance.BotRunning);
        user.ActionState.Current = FunnyProject.BotAction.Run; // Collect roaming to a box
        await FakeGameServer.WaitUntil(() => CountSent(FunnyProject.Commands.Write.Action.ID) == 1);
        await Task.Delay(1_200); // several 500 ms ticks: no resend
        Assert.Equal(1, CountSent(FunnyProject.Commands.Write.Action.ID));
        // Auto-buy is opt-in: nothing ticked, nothing bought.
        Assert.Equal("Compras automaticas: desactivadas", instance.BoosterAutoBuy.Status);
    }

    [Fact]
    public async Task StatusShowsSupervisionAndCountsReconnects()
    {
        // Something from the game, so "last packet" has a value.
        FakeGameServer.Send(connection, FakeGameServer.Frame(1, new byte[] { 0, 0 }));
        await FakeGameServer.WaitUntil(() => instance.Session.PacketManager.LastReceivedUtc != default);
        Command("/status");
        string status = await telegram.WaitForSentAsync("Reconexiones TCP: 0");
        Assert.Contains("Ultimo paquete: hace ", status);
        Assert.DoesNotContain("Ultima desconexion", status);

        Command("/tcp 1");
        await telegram.WaitForSentAsync("Reinicio TCP solicitado");
        gameServer.NextConnection();
        await FakeGameServer.WaitUntil(() => instance.Session.PacketManager.ConnectionCount == 2);
        string heartbeat = instance.Heartbeat(DateTime.UtcNow);
        Assert.StartsWith("[Estado] TCP conectado | Bot Stopped | ", heartbeat);
        Assert.Contains("Reconexiones TCP: 1", heartbeat);
        Assert.DoesNotContain("\n", heartbeat);
    }

    [Fact]
    public async Task ProblemsOpeningAccountsAreSentToTelegram()
    {
        host.ReportProblem("⚠️ No se pudo abrir la cuenta 2: PHPSESSID caducado o redirigido fuera de /map.");
        await telegram.WaitForSentAsync("No se pudo abrir la cuenta 2");
    }

    [Fact]
    public async Task CollectCanBeConfiguredFromScratchThroughTelegram()
    {
        // No settings file at all: everything from Telegram.
        try { File.Delete(instance.SettingsPath); } catch (IOException) { }
        Command("/ajuste 1 modulo collect");
        await telegram.WaitForSentAsync("Guardado. Modulo: Collect.");
        Command("/ajuste 1 mapa_collect 1-1");
        await telegram.WaitForSentAsync("Guardado. Mapa de Collect: 1-1.");
        Command("/cajas 1");
        await telegram.WaitForSentAsync("1. ⬜ BONUS_BOX");
        Command("/caja 1 1");
        await telegram.WaitForSentAsync("Cajas de Collect en 1-1: BONUS_BOX.");
        Command("/ajuste 1 spins 3");
        await telegram.WaitForSentAsync("Spins no validos: 3");

        var saved = BotSettingsStore.Load("wordgalaxy", userId.ToString());
        Assert.Equal("Collect", saved.SelectedModule);
        Assert.Equal(map.id, saved.CollectMapId);
        Assert.Equal(new[] { "BONUS_BOX" }, saved.CollectTypesByMap[map.id]);

        Command("/ajustes 1");
        string all = await telegram.WaitForSentAsync("⚙️ Ajustes");
        Assert.Contains("Collect: mapa 1-1; cajas: BONUS_BOX", all);
        Assert.Contains("Fichero: " + instance.SettingsPath, all);

        Command("/startbot 1");
        await telegram.WaitForSentAsync("Inicio de Collect solicitado.");
        await FakeGameServer.WaitUntil(() => instance.BotRunning);
        Command("/caja 1 ninguno");
        await telegram.WaitForSentAsync("Cajas de Collect en 1-1: ninguna. Aplicado al bot en marcha.");
        Command("/ajuste 1 modulo npc");
        await telegram.WaitForSentAsync("Guardado. Modulo: NPC Killer. Se aplica en el proximo /startbot.");
    }

    [Fact]
    public async Task FormationAndAutoBuyChangesApplyAtOnce()
    {
        instance.Session.Api.User.UpdateAvailableDroneFormations(new[] { "drill", "arrow" });
        Command("/ajuste 1 formacion_mover arrow");
        await telegram.WaitForSentAsync("Guardado. Formacion al moverse: arrow. Aplicado.");
        Command("/ajuste 1 formacion_atacar ring");
        await telegram.WaitForSentAsync("Formacion no disponible: ring. Disponibles: drill, arrow o ninguna.");
        Command("/ajuste 1 autocompra HON_B01 si");
        await telegram.WaitForSentAsync("Guardado. Autocompra de HON_B01: activada");
        var saved = BotSettingsStore.Load("wordgalaxy", userId.ToString());
        Assert.Equal("arrow", saved.RunFormation);
        Assert.True(saved.AutoBuyBoosters[2053]);
    }

    [Fact]
    public async Task TcpAndRestartCommands()
    {
        Command("/tcp 1");
        await telegram.WaitForSentAsync("Reinicio TCP solicitado para " + userId + ".");
        gameServer.NextConnection(); // new TCP connection with Login

        var oldSession = instance.Session;
        Command("/restart 1");
        await telegram.WaitForSentAsync("Instancia reiniciada: " + userId + ".");
        var current = Assert.Single(host.Typed);
        Assert.NotSame(oldSession, current.Session);
        Assert.True(oldSession.IsClosed);
        instance = current;
    }

    [Fact]
    public async Task PhpSessIdIsOnlySentToTheAuthorisedChat()
    {
        telegram.QueueUpdates(FakeTelegram.Message(nextUpdate++, 999, "private", "/phpsessid 1"));
        Command("/phpsessid 1");
        await telegram.WaitForSentAsync("PHPSESSID de la instancia 1: test-php-session-0001");
        lock (telegram.Calls)
            Assert.All(telegram.Calls.Where(c => c.Method == "sendMessage"), c => Assert.Contains("chat_id=555", c.Body));
    }
}
