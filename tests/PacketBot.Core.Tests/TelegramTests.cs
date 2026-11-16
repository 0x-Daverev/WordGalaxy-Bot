using System.Net;
using System.Text;
using System.Text.Json;
using FunnyProject.Telegram;
using Xunit;

namespace PacketBot.Core.Tests;

public class TelegramCommandDispatcherTests
{
    internal sealed class FakeInstance : IBotInstance
    {
        public object Key => this;
        public string DisplayName { get; set; } = "Piloto";
        public string PhpSessionId { get; set; } = "php-1";
        public bool BotRunning { get; set; }
        public bool BotActive => BotRunning;
        public FunnyProject.Runtime.BotStopReason LastStopReason { get; set; }
        public int BotStopCount { get; set; }
        public string? LastError { get; set; }
        public bool TcpConnected { get; set; } = true;
        public int MapId { get; set; } = 3;
        public string DetailedStatus { get; set; } = "Accion: Idle";
        public string NpcTargetsStatus(int instanceNumber) => "objetivos " + instanceNumber;
        public List<string> NpcChanges { get; } = new();
        public string ChangeNpcTargets(int instanceNumber, string argument)
        {
            NpcChanges.Add(instanceNumber + ":" + argument);
            return "cambiado";
        }
        public string NpcZonesStatus(int instanceNumber) => "zonas " + instanceNumber;
        public string ShipItemsStatus(int instanceNumber) => "nave " + instanceNumber;
        public List<string> ZoneChanges { get; } = new();
        public string ChangeNpcZones(int instanceNumber, string argument)
        {
            ZoneChanges.Add(instanceNumber + ":" + argument);
            return "zona cambiada";
        }
        public int TcpRestarts;
        public string StartBot() { BotRunning = true; return "Inicio de Collect solicitado."; }
        public string StopBot() { BotRunning = false; return "Bot detenido."; }
        public Task RestartTcpAsync() { TcpRestarts++; return Task.CompletedTask; }
    }

    internal sealed class FakeHost : IBotInstanceHost
    {
        public List<IBotInstance> List { get; } = new();
        public List<IBotInstance> Restarted { get; } = new();
        public IReadOnlyList<IBotInstance> Instances => List;
        public Task RestartInstanceAsync(IBotInstance instance) { Restarted.Add(instance); return Task.CompletedTask; }
    }

    private readonly FakeHost host = new();
    private readonly FakeInstance first = new();
    private readonly FakeInstance second = new() { DisplayName = "Segundo", PhpSessionId = "", TcpConnected = false };
    private readonly List<(string PhpSessionId, int Index)> rankCalls = new();
    private readonly TelegramCommandDispatcher dispatcher;

    public TelegramCommandDispatcherTests()
    {
        host.List.AddRange(new[] { first, second });
        dispatcher = new TelegramCommandDispatcher(host, (php, index) =>
        {
            rankCalls.Add((php, index));
            return Task.FromResult("rango " + index);
        });
    }

    [Theory]
    [InlineData("/ajustes 1")]
    [InlineData("/cajas 1")]
    [InlineData("/ajuste 1 spins 5")]
    [InlineData("/caja 1 1")]
    public async Task SettingsCommandsAreOnlyForTheServer(string command) =>
        Assert.Contains("solo esta disponible en el servidor", await dispatcher.HandleAsync(command));

    [Theory]
    [InlineData("/ajuste 1", "Uso: /ajuste N <clave> <valor>")]
    [InlineData("/ajuste 1 spins", "Uso: /ajuste N <clave> <valor>")]
    [InlineData("/caja 9 1", "Uso: /caja N")]
    [InlineData("/ajustes 0", "Indica un numero valido")]
    public async Task SettingsCommandsCheckTheirArguments(string command, string expected) =>
        Assert.Contains(expected, await dispatcher.HandleAsync(command));

    [Theory]
    [InlineData("/help")]
    [InlineData("/start")]
    [InlineData("/HELP@MiBot")]
    public async Task HelpListsTheCommands(string command) =>
        Assert.Equal(TelegramCommandDispatcher.HelpText + "\n\nPacketBot " + FunnyProject.BotVersion.Current,
            await dispatcher.HandleAsync(command));

    [Fact]
    public async Task EmptyAndUnknownCommands()
    {
        Assert.Equal("Usa /help para ver los comandos.", await dispatcher.HandleAsync("   "));
        Assert.Equal("Comando desconocido. Usa /help.", await dispatcher.HandleAsync("/borrar 1"));
    }

    [Fact]
    public async Task StatusListsInstancesInOrder()
    {
        first.BotRunning = true;
        Assert.Equal(
            "🚀 Instancia 1 — Piloto\n━━━━━━━━━━━━━━━━\nBot: 🟢 Activo\nTCP: 🟢 Conectado\nMapa: 3\nAccion: Idle" +
            "\n\n" +
            "🚀 Instancia 2 — Segundo\n━━━━━━━━━━━━━━━━\nBot: 🔴 Parado\nTCP: 🔴 Desconectado\nMapa: 3\nAccion: Idle",
            await dispatcher.HandleAsync("/status"));
    }

    [Fact]
    public async Task StatusWithoutInstances()
    {
        host.List.Clear();
        Assert.Equal("No hay instancias abiertas.", await dispatcher.HandleAsync("/status"));
    }

    [Theory]
    [InlineData("/startbot")]
    [InlineData("/startbot 0")]
    [InlineData("/startbot 3")]
    [InlineData("/startbot uno")]
    [InlineData("/startbot 1 2")]
    public async Task InvalidInstanceNumberIsRejected(string command) =>
        Assert.Equal("Indica un numero valido de /status. Ejemplo: /rank 1.", await dispatcher.HandleAsync(command));

    [Fact]
    public async Task StartStopTcpAndRestartReachTheRightInstance()
    {
        Assert.Equal("Inicio de Collect solicitado.", await dispatcher.HandleAsync("/startbot 1"));
        Assert.True(first.BotRunning);
        Assert.Equal("Bot detenido.", await dispatcher.HandleAsync("/stopbot 1"));
        Assert.False(first.BotRunning);
        Assert.Equal("Reinicio TCP solicitado para Segundo.", await dispatcher.HandleAsync("/tcp 2"));
        Assert.Equal(1, second.TcpRestarts);
        Assert.Equal("Instancia reiniciada: Piloto.", await dispatcher.HandleAsync("/restart 1"));
        Assert.Same(first, Assert.Single(host.Restarted));
    }

    [Fact]
    public async Task PhpSessIdAndRank()
    {
        Assert.Equal("PHPSESSID de la instancia 1: php-1", await dispatcher.HandleAsync("/phpsessid 1"));
        Assert.Equal("La instancia 2 no tiene un PHPSESSID disponible.", await dispatcher.HandleAsync("/phpsessid 2"));
        Assert.Equal("rango 1", await dispatcher.HandleAsync("/rank 1"));
        Assert.Equal(("php-1", 1), Assert.Single(rankCalls));
    }

    [Fact]
    public async Task NpcsReturnsTheInstanceTargets()
    {
        Assert.Equal("objetivos 2", await dispatcher.HandleAsync("/npcs 2"));
        Assert.Equal("Indica un numero valido de /status. Ejemplo: /rank 1.", await dispatcher.HandleAsync("/npcs"));
    }

    [Fact]
    public async Task NpcPassesTheArgumentsToTheInstance()
    {
        Assert.Equal("cambiado", await dispatcher.HandleAsync("/npc 1 2 3"));
        Assert.Equal("cambiado", await dispatcher.HandleAsync("/npc@MiBot 2 todos"));
        Assert.Equal(new[] { "1:2 3" }, first.NpcChanges);
        Assert.Equal(new[] { "2:todos" }, second.NpcChanges);
    }

    [Fact]
    public async Task ZoneCommandsReachTheInstance()
    {
        Assert.Equal("zonas 2", await dispatcher.HandleAsync("/zonas 2"));
        Assert.Equal("nave 1", await dispatcher.HandleAsync("/nave 1"));
        Assert.Equal("zona cambiada", await dispatcher.HandleAsync("/zona 1 borrar todas"));
        Assert.Equal(new[] { "1:borrar todas" }, first.ZoneChanges);
        Assert.StartsWith("Uso: /zona N borrar", await dispatcher.HandleAsync("/zona 1"));
    }

    [Theory]
    [InlineData("/npc")]
    [InlineData("/npc 1")]
    [InlineData("/npc 3 1")]
    [InlineData("/npc x 1")]
    public async Task NpcUsageErrors(string command) =>
        Assert.Equal("Uso: /npc N <numeros de /npcs N>, /npc N todos o /npc N ninguno.", await dispatcher.HandleAsync(command));

    [Fact]
    public void MenuMatchesTheCommands()
    {
        Assert.Equal(new[] { "status", "startbot", "stopbot", "tcp", "restart", "phpsessid", "rank", "npcs", "npc", "nave", "zonas", "zona", "ajustes", "ajuste", "cajas", "caja", "help" },
            TelegramCommandDispatcher.Menu.Select(m => m.Command));
    }

    private DateTime now = new(2026, 11, 11, 18, 0, 0, DateTimeKind.Utc);

    private TelegramAlertMonitor Monitor() => new(TimeSpan.FromSeconds(60), () => now);

    private void StopBot(FakeInstance instance, FunnyProject.Runtime.BotStopReason reason, string? error = null)
    {
        instance.BotRunning = false;
        instance.LastStopReason = reason;
        instance.LastError = error;
        instance.BotStopCount++;
    }

    [Fact]
    public void StopRequestedByTheUserSendsNothing()
    {
        var monitor = Monitor();
        first.BotRunning = true;
        Assert.Empty(monitor.Check(host));
        StopBot(first, FunnyProject.Runtime.BotStopReason.Requested);
        Assert.Empty(monitor.Check(host));
    }

    [Theory]
    [InlineData(FunnyProject.Runtime.BotStopReason.Completed, null, "el modulo termino por si solo")]
    [InlineData(FunnyProject.Runtime.BotStopReason.Faulted, "boom", "error: boom")]
    [InlineData(FunnyProject.Runtime.BotStopReason.StartFailed, "refused", "no se pudo restablecer TCP al iniciar (refused)")]
    public void UnrequestedStopAlertsOnce(FunnyProject.Runtime.BotStopReason reason, string? error, string expected)
    {
        var monitor = Monitor();
        first.BotRunning = true;
        monitor.Check(host);
        StopBot(first, reason, error);
        Assert.Equal(new[] { "⚠️ PacketBot detenido: Piloto (mapa 3): " + expected + ". Usa /startbot 1 para reanudarlo." },
            monitor.Check(host));
        Assert.Empty(monitor.Check(host));
    }

    [Fact]
    public void StopShorterThanTheCheckIntervalIsNotMissed()
    {
        var monitor = Monitor();
        monitor.Check(host); // bot stopped
        // Started and failed between two checks.
        StopBot(first, FunnyProject.Runtime.BotStopReason.StartFailed, "refused");
        Assert.Single(monitor.Check(host));
    }

    [Fact]
    public void ShortTcpDropThatRecoversSendsNothing()
    {
        var monitor = Monitor();
        first.BotRunning = true;
        monitor.Check(host);
        first.TcpConnected = false; // e.g. TCP restart after a death
        now = now.AddSeconds(5);
        Assert.Empty(monitor.Check(host));
        now = now.AddSeconds(50);
        Assert.Empty(monitor.Check(host));
        first.TcpConnected = true;
        Assert.Empty(monitor.Check(host));
        // A new drop starts its own grace period.
        first.TcpConnected = false;
        now = now.AddSeconds(30);
        Assert.Empty(monitor.Check(host));
    }

    [Fact]
    public void LongTcpOutageAlertsOnceAndReportsRecovery()
    {
        var monitor = Monitor();
        first.BotRunning = true;
        monitor.Check(host);
        first.TcpConnected = false;
        Assert.Empty(monitor.Check(host));
        now = now.AddSeconds(61);
        var alert = Assert.Single(monitor.Check(host));
        Assert.StartsWith("🔌 Sin conexion TCP desde hace 1 min: Piloto (mapa 3).", alert);
        Assert.Contains("/restart 1", alert);
        now = now.AddMinutes(10);
        Assert.Empty(monitor.Check(host)); // no repeat during the same outage
        first.TcpConnected = true;
        Assert.Equal(new[] { "✅ TCP recuperado: Piloto." }, monitor.Check(host));
        Assert.Empty(monitor.Check(host));
    }

    [Fact]
    public void TcpDownWhileTheBotIsStoppedSendsNothing()
    {
        var monitor = Monitor();
        first.TcpConnected = false;
        monitor.Check(host);
        now = now.AddMinutes(5);
        Assert.Empty(monitor.Check(host));
    }

    [Fact]
    public void StoppingTheBotDuringAnOutageCancelsTheTcpAlert()
    {
        var monitor = Monitor();
        first.BotRunning = true;
        monitor.Check(host);
        first.TcpConnected = false;
        now = now.AddSeconds(30);
        monitor.Check(host);
        StopBot(first, FunnyProject.Runtime.BotStopReason.Requested);
        now = now.AddMinutes(5);
        Assert.Empty(monitor.Check(host));
    }

    [Fact]
    public void LifeIsFormattedInSpanish()
    {
        Assert.Equal("Vida: 9.500/10.000 (95%)", BotStatusText.Life(9_500, 10_000));
        Assert.Equal("Vida: Sin datos", BotStatusText.Life(0, 0));
    }
}

public class TelegramBotServiceTests
{
    private const string Token = "123456:TEST-token";
    private const long Chat = 555;

    private static object Message(long updateId, long chatId, string type, string text) =>
        FakeTelegram.Message(updateId, chatId, type, text);

    [Fact]
    public async Task OnlyTheAuthorisedPrivateChatIsAnswered()
    {
        var api = new FakeTelegram(Token);
        api.QueueUpdates(
            Message(1, 999, "private", "/status"),   // other chat
            Message(2, Chat, "group", "/status"),    // right chat id but not private
            Message(3, Chat, "private", "/help"));
        var received = new List<string>();
        using var service = new TelegramBotService(Token, Chat, text =>
        {
            lock (received) received.Add(text);
            return Task.FromResult("respuesta a " + text);
        }, api);
        service.Start();

        string sent = await api.FirstMessage.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "/help" }, received);
        Assert.Contains("chat_id=555", sent);
        Assert.Contains("text=respuesta a /help", sent);
        await FakeGameServer.WaitUntil(() => { lock (api.Calls) return api.Calls.Any(c => c.Method == "getMyCommands"); });
        lock (api.Calls)
        {
            Assert.Equal(2, api.Calls.Count(c => c.Method == "setMyCommands"));
            // The next poll acknowledges the processed updates.
            Assert.Contains(api.Calls, c => c.Method == "getUpdates");
        }
    }

    [Fact]
    public async Task DispatchErrorsAreReportedToTheChat()
    {
        var api = new FakeTelegram(Token);
        api.QueueUpdates(Message(1, Chat, "private", "/status"));
        using var service = new TelegramBotService(Token, Chat,
            _ => throw new InvalidOperationException("fallo"), api);
        service.Start();
        Assert.Contains("text=Error: fallo", await api.FirstMessage.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task DisposeStopsPolling()
    {
        var api = new FakeTelegram(Token);
        var service = new TelegramBotService(Token, Chat, t => Task.FromResult(t), api);
        service.Start();
        service.Dispose();
        await service.Polling.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Telegram down: errors are counted, waits grow to 60 s, success resets.
    private sealed class FlakyTelegram : HttpMessageHandler
    {
        private readonly FakeTelegram inner;
        public volatile int FailuresLeft;
        public FlakyTelegram(FakeTelegram inner, int failures) { this.inner = inner; FailuresLeft = failures; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/getUpdates") && Interlocked.Decrement(ref FailuresLeft) >= 0)
                throw new HttpRequestException("sin red");
            return new HttpMessageInvoker(inner).SendAsync(request, token);
        }
    }

    [Fact]
    public async Task PollingErrorsBackOffAndRecover()
    {
        var api = new FakeTelegram(Token);
        var flaky = new FlakyTelegram(api, 3);
        using var service = new TelegramBotService(Token, Chat, t => Task.FromResult(t), flaky,
            retryDelay: TimeSpan.FromMilliseconds(10));
        Assert.Equal(TimeSpan.FromMilliseconds(10), service.RetryDelay(1));
        Assert.Equal(TimeSpan.FromMilliseconds(40), service.RetryDelay(3));
        Assert.Null(service.LastSuccessUtc);
        service.Start();
        await FakeGameServer.WaitUntil(() => service.LastSuccessUtc != null);
        Assert.Equal(0, service.ConsecutiveFailures);

        var slow = new TelegramBotService(Token, Chat, t => Task.FromResult(t), api);
        Assert.Equal(TimeSpan.FromSeconds(5), slow.RetryDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(60), slow.RetryDelay(5)); // capped
        Assert.Equal(TimeSpan.FromSeconds(60), slow.RetryDelay(1000));
        slow.Dispose();
    }

    [Theory]
    [InlineData("", 1)]
    [InlineData("token", 0)]
    public void InvalidSettingsAreRejected(string token, long chat) =>
        Assert.Throws<ArgumentException>(() => new TelegramBotService(token, chat, t => Task.FromResult(t)));
}
