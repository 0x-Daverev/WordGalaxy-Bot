using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using FunnyProject.Telegram;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PacketBot.Server.Web;

// Failed passwords slow everyone down: after 5 failures within a minute the
// login is closed for a minute. Global, not per IP, so forged
// X-Forwarded-For headers cannot get around it.
public sealed class LoginGuard
{
    private readonly object sync = new();
    private readonly Queue<DateTime> failures = new();
    private readonly Func<DateTime> clock;
    private DateTime lockedUntil;

    public LoginGuard(Func<DateTime>? clock = null) => this.clock = clock ?? (() => DateTime.UtcNow);

    public bool Locked
    {
        get { lock (sync) return clock() < lockedUntil; }
    }

    public void Failed()
    {
        lock (sync)
        {
            var now = clock();
            failures.Enqueue(now);
            while (failures.Count > 0 && now - failures.Peek() > TimeSpan.FromMinutes(1)) failures.Dequeue();
            if (failures.Count >= 5)
            {
                lockedUntil = now.AddMinutes(1);
                failures.Clear();
            }
        }
    }

    public static bool Matches(string given, string expected) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(given ?? "")),
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
}

// Settings and control of the bot from a browser, like the Windows window.
// Plain HTTP on PACKETBOT_WEB_PORT; an HTTPS reverse proxy publishes it.
//  - Every /api call except login needs the session cookie (password).
//  - State-changing calls also need the X-PacketBot header: a browser only
//    sends it from this page (other sites would need CORS, not enabled), and
//    the cookie is SameSite=Strict.
//  - Settings are changed through the same validation as Telegram's /ajuste.
public sealed class WebPanel : IHostedService, IAsyncDisposable
{
    public const string CsrfHeader = "X-PacketBot";
    private const string Scheme = CookieAuthenticationDefaults.AuthenticationScheme;

    private readonly ServerOptions options;
    private readonly ServerInstanceHost host;
    private readonly string keysDirectory;
    private readonly string bindAddress;
    private readonly LoginGuard guard;
    private readonly MinimapImages minimaps;
    private WebApplication? app;

    public WebPanel(ServerOptions options, ServerInstanceHost host)
        : this(options, host, null, "0.0.0.0", null, null) { }

    // Tests: loopback, port 0 and a temporary key folder.
    public WebPanel(ServerOptions options, ServerInstanceHost host, string? keysDirectory, string bindAddress,
        LoginGuard? guard, MinimapImages? minimaps = null)
    {
        this.minimaps = minimaps ?? new MinimapImages();
        this.options = options;
        this.host = host;
        this.bindAddress = bindAddress;
        this.guard = guard ?? new LoginGuard();
        this.keysDirectory = keysDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WordGalaxyPacketBot", "web-keys");
    }

    // Base address once started (tests use port 0).
    public Uri? Address { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(WebPanel).Assembly.GetName().Name
        });
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        // Keys are stored as files in the volume (no OS key store on Linux);
        // the warning about it would repeat on every start.
        builder.Logging.AddFilter("Microsoft.AspNetCore.DataProtection", LogLevel.Error);
        builder.WebHost.UseKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Listen(System.Net.IPAddress.Parse(bindAddress), options.WebPort);
        });
        // Login cookies survive restarts of the container (keys in the volume).
        Directory.CreateDirectory(keysDirectory);
        builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keysDirectory))
            .SetApplicationName("PacketBot.Web");
        builder.Services.AddAuthentication(Scheme).AddCookie(Scheme, c =>
        {
            c.Cookie.Name = "packetbot";
            c.Cookie.HttpOnly = true;
            c.Cookie.SameSite = SameSiteMode.Strict;
            c.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            c.ExpireTimeSpan = TimeSpan.FromDays(7);
            c.SlidingExpiration = true;
            // An API: answer 401 instead of redirecting to a login page.
            c.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
            c.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
        });
        builder.Services.AddAuthorization();
        builder.Services.Configure<ForwardedHeadersOptions>(o =>
        {
            // Reverse proxy in front: trust its X-Forwarded-Proto so cookies are
            // Secure on HTTPS. The port is not published without the proxy.
            o.ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedFor;
            o.KnownNetworks.Clear();
            o.KnownProxies.Clear();
        });

        app = builder.Build();
        app.UseForwardedHeaders();
        app.Use(async (ctx, next) =>
        {
            var h = ctx.Response.Headers;
            h["X-Content-Type-Options"] = "nosniff";
            h["X-Frame-Options"] = "DENY";
            h["Referrer-Policy"] = "no-referrer";
            h["Content-Security-Policy"] =
                "default-src 'self'; img-src 'self' data:; style-src 'self'; script-src 'self'; frame-ancestors 'none'";
            h["Cache-Control"] = "no-store"; // the minimap image overrides it
            if (HttpMethods.IsPost(ctx.Request.Method) && ctx.Request.Path.StartsWithSegments("/api") &&
                ctx.Request.Headers[CsrfHeader] != "1")
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsJsonAsync(new ReplyView(false, "Falta la cabecera " + CsrfHeader + "."));
                return;
            }
            await next();
        });
        app.UseAuthentication();
        app.UseAuthorization();
        Map(app);

        await app.StartAsync(cancellationToken);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault();
        Address = address == null ? null : new Uri(address.Replace("0.0.0.0", "127.0.0.1"));
        Console.WriteLine("[Web] Panel activo en el puerto " + (Address?.Port ?? options.WebPort) + ".");
    }

    private void Map(WebApplication web)
    {
        web.MapGet("/healthz", () => Results.Text("ok"));
        foreach (var (path, file, type) in new[]
                 {
                     ("/", "index.html", "text/html; charset=utf-8"),
                     ("/app.js", "app.js", "text/javascript; charset=utf-8"),
                     ("/app.css", "app.css", "text/css; charset=utf-8")
                 })
        {
            byte[] content = Resource(file);
            web.MapGet(path, () => Results.Bytes(content, type));
        }

        web.MapPost("/api/login", async (HttpContext ctx, LoginRequest body) =>
        {
            if (guard.Locked)
                return Results.Json(new ReplyView(false, "Demasiados intentos. Espera un minuto."), statusCode: 429);
            if (!LoginGuard.Matches(body.Password, options.WebPassword!))
            {
                guard.Failed();
                Console.Error.WriteLine("[Web] Contrasena incorrecta.");
                return Results.Json(new ReplyView(false, "Contrasena incorrecta."), statusCode: 401);
            }
            var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "admin") }, Scheme);
            await ctx.SignInAsync(Scheme, new ClaimsPrincipal(identity),
                new AuthenticationProperties { IsPersistent = true });
            return Results.Json(new ReplyView(true, "Sesion iniciada."));
        });
        web.MapPost("/api/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(Scheme);
            return Results.Json(new ReplyView(true, "Sesion cerrada."));
        });

        var api = web.MapGroup("/api").RequireAuthorization();
        api.MapGet("/instances", () => host.Typed.Select((i, index) => View(i, index + 1)).ToArray());
        api.MapGet("/instances/{n:int}/settings", (int n) =>
            Find(n) is { } i ? Results.Json(i.WebSettings()) : NotFound(n));
        api.MapPost("/instances/{n:int}/settings", (int n, ChangeRequest body) =>
            Find(n) is { } i ? Reply(i.ChangeSetting(n, body.Key ?? "", body.Value ?? "")) : NotFound(n));
        api.MapPost("/instances/{n:int}/boxes", (int n, ChecklistRequest body) =>
            Find(n) is { } i ? Reply(i.SetChecklist("boxes", body.Selected ?? new())) : NotFound(n));
        api.MapPost("/instances/{n:int}/npcs", (int n, ChecklistRequest body) =>
            Find(n) is { } i ? Reply(i.SetChecklist("npcs", body.Selected ?? new())) : NotFound(n));
        // Minimap: positions every second (same data as the Windows minimap).
        api.MapGet("/instances/{n:int}/minimap", (int n) =>
            Find(n) is { } i ? Results.Json(FunnyProject.Services.MinimapSnapshot.From(i.Session.Api, i.CurrentMapZones())) : NotFound(n));
        // What the ship has (item bar), grouped as in /nave.
        api.MapGet("/instances/{n:int}/items", (int n) =>
            Find(n) is { } i ? Results.Json(i.Session.Api.User.Items.Snapshot().Select(x => new ItemView(
                x.Category, x.Name, x.Amount, double.IsNaN(x.Maximum) ? null : x.Maximum))) : NotFound(n));
        // NPC Killer preferred zones, drawn on the minimap (ship's current map).
        api.MapPost("/instances/{n:int}/zones", (int n, ZoneRequest body) =>
            Find(n) is { } i ? Zone(i.ChangeCurrentMapZones((s, map) =>
                FunnyProject.Logic.PreferredZones.Add(s, map, body.X1, body.Y1, body.X2, body.Y2))) : NotFound(n));
        api.MapPost("/instances/{n:int}/zones/remove", (int n, PointRequest body) =>
            Find(n) is { } i ? Zone(i.ChangeCurrentMapZones((s, map) =>
                FunnyProject.Logic.PreferredZones.RemoveAt(s, map, body.X, body.Y))) : NotFound(n));
        api.MapPost("/instances/{n:int}/zones/clear", (int n) =>
            Find(n) is { } i ? Zone(i.ChangeCurrentMapZones((s, map) =>
                (true, FunnyProject.Logic.PreferredZones.Clear(s, map)))) : NotFound(n));
        api.MapGet("/minimap/{mapId:int}.jpg", async (int mapId, HttpContext ctx) =>
        {
            byte[]? image = await minimaps.GetAsync(mapId, ctx.RequestAborted);
            if (image == null) return Results.NotFound();
            ctx.Response.Headers["Cache-Control"] = "private, max-age=86400";
            return Results.Bytes(image, "image/jpeg");
        });
        api.MapPost("/instances/{n:int}/start", (int n) => Find(n) is { } i ? Reply(i.StartBot()) : NotFound(n));
        api.MapPost("/instances/{n:int}/stop", (int n) => Find(n) is { } i ? Reply(i.StopBot()) : NotFound(n));
        api.MapPost("/instances/{n:int}/tcp", async (int n) =>
        {
            if (Find(n) is not { } i) return NotFound(n);
            await i.RestartTcpAsync();
            return Reply("Reinicio TCP solicitado.");
        });
        api.MapPost("/instances/{n:int}/restart", async (int n) =>
        {
            if (Find(n) is not { } i) return NotFound(n);
            await host.RestartInstanceAsync(i);
            return Reply("Instancia reiniciada.");
        });
    }

    private ServerBotInstance? Find(int n)
    {
        var all = host.Typed;
        return n >= 1 && n <= all.Count ? all[n - 1] : null;
    }

    private static IResult NotFound(int n) =>
        Results.Json(new ReplyView(false, "No existe la instancia " + n + "."), statusCode: 404);

    // Replies of the shared commands; validation errors are not HTTP errors.
    private static IResult Reply(string message) => Results.Json(new ReplyView(
        message.StartsWith("Guardado", StringComparison.Ordinal) || message.StartsWith("Inicio", StringComparison.Ordinal) ||
        message.StartsWith("Bot detenido", StringComparison.Ordinal) || message.StartsWith("Cajas", StringComparison.Ordinal) ||
        message.StartsWith("Objetivos", StringComparison.Ordinal) || message.StartsWith("Reinicio", StringComparison.Ordinal) ||
        message.StartsWith("Instancia reiniciada", StringComparison.Ordinal), message));

    private static IResult Zone(FunnyProject.Logic.PreferredZoneReply r) =>
        Results.Json(new ReplyView(r.Ok, r.Message));

    private static InstanceView View(ServerBotInstance i, int number) => new(number, i.DisplayName,
        i.Session.UserId, i.BotRunning, i.BotActive, i.TcpConnected, i.MapId,
        FunnyProject.Navigation.MapGraph.Default.Maps.TryGetValue(i.MapId, out var m) ? m.name : i.MapId.ToString(),
        i.DetailedStatus);

    private static byte[] Resource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("web/" + name)
            ?? throw new InvalidOperationException("Falta el recurso web/" + name);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (app != null) await app.StopAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (app != null) await app.DisposeAsync();
        minimaps.Dispose();
    }
}
