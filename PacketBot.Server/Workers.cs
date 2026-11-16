using FunnyProject.Sessions;
using FunnyProject.Telegram;
using Microsoft.Extensions.Hosting;

namespace PacketBot.Server;

// Opens every configured account, optionally starts a module, logs TCP state
// changes and, on shutdown, stops the modules before closing TCP.
public sealed class SessionWorker : BackgroundService
{
    private readonly ServerOptions options;
    private readonly ServerInstanceHost host;
    private readonly IHostApplicationLifetime lifetime;

    public SessionWorker(ServerOptions options, ServerInstanceHost host, IHostApplicationLifetime lifetime)
    {
        this.options = options;
        this.host = host;
        this.lifetime = lifetime;
    }

    // Tests must not change the exit code of the test process.
    internal Action<int> SetExitCode { get; set; } = code => Environment.ExitCode = code;

    // Periodic one-line state of every account, for `journalctl`.
    internal TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromMinutes(15);

    // Waits between attempts to open an account after a network error (no
    // network yet at boot, game server restarting...): about 5 minutes in
    // total, then the account is given up. A rejected PHPSESSID is not retried.
    internal IReadOnlyList<TimeSpan> OpenRetryDelays { get; set; } =
        new[] { 5, 10, 20, 40, 60, 60, 60, 60 }.Select(s => TimeSpan.FromSeconds(s)).ToArray();

    // Network failures worth retrying; anything else needs the user.
    internal static bool IsTransient(Exception ex, CancellationToken token) => ex switch
    {
        OperationCanceledException => !token.IsCancellationRequested, // HTTP timeout
        HttpRequestException or System.Net.Sockets.SocketException or IOException or TimeoutException => true,
        _ => ex.InnerException != null && IsTransient(ex.InnerException, token)
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int number = 0, opened = 0;
        foreach (string phpSessionId in options.PhpSessionIds)
        {
            number++;
            try
            {
                if (await OpenAccountAsync(number, phpSessionId, stoppingToken)) opened++;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
        if (opened == 0)
        {
            if (options.FailureExitDelay > TimeSpan.Zero)
            {
                Console.Error.WriteLine("[Server] No se pudo abrir ninguna cuenta; se cerrara en " +
                    options.FailureExitDelay.TotalSeconds.ToString("0") + " s (" +
                    ServerOptions.FailureExitDelayVariable + ").");
                try { await Task.Delay(options.FailureExitDelay, stoppingToken); }
                catch (OperationCanceledException) { return; } // SIGTERM: normal stop
            }
            Console.Error.WriteLine("[Server] No se pudo abrir ninguna cuenta; cerrando.");
            SetExitCode(1);
            lifetime.StopApplication();
            return;
        }
        Console.WriteLine("[Server] Sesiones abiertas. Ctrl+C o SIGTERM para salir.");

        var lastStates = new Dictionary<WordGalaxySession, SessionState>();
        var nextHeartbeat = DateTime.UtcNow + HeartbeatInterval;
        while (!stoppingToken.IsCancellationRequested)
        {
            bool heartbeat = DateTime.UtcNow >= nextHeartbeat;
            if (heartbeat) nextHeartbeat = DateTime.UtcNow + HeartbeatInterval;
            foreach (var instance in host.Typed)
            {
                LogTag.Enter(instance.Tag);
                var state = instance.Session.State;
                if (lastStates.TryGetValue(instance.Session, out var prior) && prior != state)
                    Console.WriteLine($"[Server] {instance.DisplayName}: TCP {state}.");
                lastStates[instance.Session] = state;
                if (heartbeat) Console.WriteLine(instance.Heartbeat(DateTime.UtcNow));
            }
            LogTag.Enter((LogTag?)null);
            try { await Task.Delay(1000, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    // Returns true when the account has an open session (even if the hero
    // initialization did not arrive in time: /restart can still fix it).
    private async Task<bool> OpenAccountAsync(int number, string phpSessionId, CancellationToken token)
    {
        // Scoped to this method: every line of this account carries its tag.
        var tag = LogTag.Enter("cuenta " + number);
        for (int attempt = 0; ; attempt++)
        {
            ServerBotInstance instance;
            try
            {
                Console.WriteLine($"[Server] Cuenta {number}/{options.PhpSessionIds.Count}: consultando /map...");
                instance = await host.OpenAsync(phpSessionId, token, tag);
            }
            catch (Exception ex) when (IsTransient(ex, token) && attempt < OpenRetryDelays.Count)
            {
                var delay = OpenRetryDelays[attempt];
                Console.Error.WriteLine($"[Server] Cuenta {number}: error de red ({ex.GetType().Name}: {ex.Message}). " +
                    $"Reintento {attempt + 1}/{OpenRetryDelays.Count} en {delay.TotalSeconds:0} s.");
                await Task.Delay(delay, token);
                continue;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
            {
                Console.Error.WriteLine($"[Server] Cuenta {number}: {ex.Message}");
                host.ReportProblem($"⚠️ No se pudo abrir la cuenta {number}: {ex.Message}" +
                    (IsTransient(ex, token) ? " (error de red tras varios reintentos)" : ""));
                return false;
            }
            var session = instance.Session;
            Console.WriteLine($"[Server] Instancia {host.Manager.NumberOf(session)}: cuenta {session.UserId}, " +
                $"servidor de juego {session.GameHost}:{session.GamePort}.");
            if (!await WaitForHeroAsync(session, token))
                return true;
            Console.WriteLine($"[Server] Heroe inicializado: {instance.DisplayName} (mapa {instance.MapId}). " +
                "Ajustes: " + instance.SettingsPath);
            if (options.StartModule != null)
                Console.WriteLine("[Server] " + instance.Start(options.StartModule));
            return true;
        }
    }

    private async Task<bool> WaitForHeroAsync(WordGalaxySession session, CancellationToken token)
    {
        var deadline = DateTime.UtcNow + options.HeroTimeout;
        while (!session.Api.User.BoosterRequestsReady && session.State == SessionState.Connected &&
               DateTime.UtcNow < deadline)
            await Task.Delay(100, token);
        if (session.Api.User.BoosterRequestsReady) return true;
        Console.Error.WriteLine(session.State == SessionState.Connected
            ? "[Server] El servidor no envio la inicializacion del heroe a tiempo."
            : "[Server] La conexion TCP se cerro antes de inicializar el heroe.");
        return false;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        // Stop the modules first (no orphan tasks, no reconnects), then close TCP.
        await host.StopAllAsync(TimeSpan.FromSeconds(5));
        host.Dispose();
        Console.WriteLine("[Server] Sesiones cerradas.");
    }
}

// Telegram control: commands through the shared dispatcher and the same
// "bot stopped" / "TCP lost" alerts as Windows, checked every second.
public sealed class TelegramWorker : BackgroundService
{
    private readonly ServerOptions options;
    private readonly ServerInstanceHost host;
    private readonly HttpMessageHandler? handler;
    private readonly Uri? apiBase;

    public TelegramWorker(ServerOptions options, ServerInstanceHost host)
        : this(options, host, null, null) { }

    // Tests pass a fake Bot API.
    public TelegramWorker(ServerOptions options, ServerInstanceHost host, HttpMessageHandler? handler, Uri? apiBase)
    {
        this.options = options;
        this.host = host;
        this.handler = handler;
        this.apiBase = apiBase;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var dispatcher = new TelegramCommandDispatcher(host);
        using var service = new TelegramBotService(options.TelegramToken!, options.TelegramChatId,
            dispatcher.HandleAsync, handler, apiBase);
        service.Start();
        Console.WriteLine("[Telegram] Control remoto activo para el chat autorizado.");
        var alerts = new TelegramAlertMonitor();
        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var alert in alerts.Check(host))
                _ = service.NotifyAsync(alert);
            while (host.TryTakeProblem(out string problem))
                _ = service.NotifyAsync(problem);
            try { await Task.Delay(1000, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
        // Shutting down (e.g. no account could be opened): still tell the user why.
        var last = new List<Task>();
        while (host.TryTakeProblem(out string problem))
            last.Add(service.NotifyAsync(problem));
        if (last.Count > 0)
            await Task.WhenAny(Task.WhenAll(last), Task.Delay(TimeSpan.FromSeconds(5)));
    }
}
