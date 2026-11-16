using FunnyProject.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PacketBot.Server;

// Headless PacketBot for Linux servers and Docker (also runs on Windows).
//  --check: log in, wait for the hero initialization and exit (0 = OK).
//  otherwise: .NET Generic Host with one session per PHPSESSID, optional
//  Telegram control and ordered shutdown on SIGINT/SIGTERM.

bool journald = LogWriter.UnderJournald(Environment.GetEnvironmentVariable);
Console.SetOut(new LogWriter(Console.Out, journald ? LogWriter.Info : null));
Console.SetError(new LogWriter(Console.Error, journald ? LogWriter.Error : null));

var (options, error) = ServerOptions.Parse(args, Environment.GetEnvironmentVariable);
if (options == null)
{
    Console.Error.WriteLine(error);
    return args.Any(a => a is "--help" or "-h") ? 0 : 2;
}
foreach (string id in options.PhpSessionIds) LogSecrets.Add(id);
LogSecrets.Add(options.TelegramToken);
LogSecrets.Add(options.WebPassword);

if (options.CheckOnly)
    return await CheckAsync(options);

Console.WriteLine("[Server] PacketBot " + FunnyProject.BotVersion.Current);
var builder = Host.CreateApplicationBuilder(Array.Empty<string>());
// The engine logs with Console.WriteLine (timestamped above); keep only
// warnings from the host itself.
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(20));
// Under systemd (Type=notify): READY/STOPPING notifications; no-op otherwise.
builder.Services.AddSystemd();
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(_ => new ServerInstanceHost(new InstanceManager()));
builder.Services.AddHostedService<SessionWorker>();
if (options.TelegramEnabled)
    builder.Services.AddHostedService<TelegramWorker>();
else
    Console.WriteLine("[Telegram] Desactivado (define " + ServerOptions.TelegramTokenVariable + " y " +
        ServerOptions.TelegramChatVariable + " para activarlo).");

if (options.WebEnabled)
    builder.Services.AddHostedService(sp => new PacketBot.Server.Web.WebPanel(
        sp.GetRequiredService<ServerOptions>(), sp.GetRequiredService<ServerInstanceHost>()));
else
    Console.WriteLine("[Web] Desactivado (define " + ServerOptions.WebPasswordVariable + " para activarlo).");

using var app = builder.Build();
await app.RunAsync();
return Environment.ExitCode;

// --check: one account, no host.
static async Task<int> CheckAsync(ServerOptions options)
{
    using var instances = new InstanceManager();
    try
    {
        Console.WriteLine("[Server] Consultando /map...");
        var session = await instances.OpenAsync(options.PhpSessionId);
        Console.WriteLine($"[Server] Instancia 1: cuenta {session.UserId}, servidor de juego {session.GameHost}:{session.GamePort}.");
        var user = session.Api.User;
        var deadline = DateTime.UtcNow + options.HeroTimeout;
        while (!user.BoosterRequestsReady && session.State == SessionState.Connected && DateTime.UtcNow < deadline)
            await Task.Delay(100);
        if (!user.BoosterRequestsReady)
        {
            Console.Error.WriteLine(session.State == SessionState.Connected
                ? "[Server] El servidor no envio la inicializacion del heroe a tiempo."
                : "[Server] La conexion TCP se cerro antes de inicializar el heroe.");
            return 1;
        }
        Console.WriteLine($"[Server] Heroe inicializado: {user.UserData.InGameName} (mapa {user.UserData.MapId}, " +
            $"HP {user.ShipInfo.Hp}/{user.ShipInfo.MaxHp}).");
        Console.WriteLine("[Server] Comprobacion completada; cerrando sesion.");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("[Server] Error: " + ex.Message);
        return 1;
    }
    finally
    {
        Console.WriteLine("[Server] Sesiones cerradas.");
    }
}
