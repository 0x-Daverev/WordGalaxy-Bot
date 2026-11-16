using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FunnyProject;
using FunnyProject.Sessions;
using PacketBot.Core.Tests;
using PacketBot.Server;
using PacketBot.Server.Web;
using Xunit;

namespace PacketBot.Server.Tests;

// The panel against a fake /map and game server, through real HTTP.
public class WebPanelTests : IAsyncLifetime
{
    private const string Password = "panel-password-1";
    private readonly int userId = 910_000_000 + Random.Shared.Next(1_000_000);
    private readonly string keys = Path.Combine(Path.GetTempPath(), "pb-web-" + Guid.NewGuid().ToString("N"));
    private readonly FakeGameServer gameServer = new();
    private readonly ServerInstanceHost host;
    private readonly WebPanel panel;
    private readonly CookieContainer cookies = new();
    private HttpClient http = null!;
    private ServerBotInstance instance = null!;

    public WebPanelTests()
    {
        var web = new WordGalaxyWebClient(c => new FakeWordGalaxyHttp(c,
            (_, _) => FakeWordGalaxyHttp.Html(FakeWordGalaxyHttp.MapHtml(userId: userId, host: "127.0.0.1"))));
        host = new ServerInstanceHost(new InstanceManager(web, gameServer.Port));
        var options = new ServerOptions(new[] { "php" }, false, TimeSpan.FromSeconds(5),
            WebPassword: Password, WebPort: 0);
        panel = new WebPanel(options, host, keys, "127.0.0.1", null);
    }

    public async Task InitializeAsync()
    {
        instance = await host.OpenAsync("test-php-session-0001", CancellationToken.None);
        gameServer.NextConnection();
        await panel.StartAsync(CancellationToken.None);
        http = new HttpClient(new HttpClientHandler { CookieContainer = cookies }) { BaseAddress = panel.Address };
    }

    public async Task DisposeAsync()
    {
        string settings = instance.SettingsPath;
        await panel.StopAsync(CancellationToken.None);
        await panel.DisposeAsync();
        await host.StopAllAsync(TimeSpan.FromSeconds(5));
        host.Dispose();
        gameServer.Dispose();
        http.Dispose();
        try { File.Delete(settings); } catch (IOException) { }
        try { Directory.Delete(keys, true); } catch (IOException) { }
    }

    private Task<HttpResponseMessage> Post(string path, object body, bool csrf = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        if (csrf) request.Headers.Add(WebPanel.CsrfHeader, "1");
        return http.SendAsync(request);
    }

    private async Task<ReplyView> Reply(string path, object body)
    {
        var response = await Post(path, body);
        return (await response.Content.ReadFromJsonAsync<ReplyView>(new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
    }

    private async Task Login()
    {
        var response = await Post("/api/login", new { password = Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task PageAndHealthNeedNoLoginButTheApiDoes()
    {
        Assert.Equal("ok", await http.GetStringAsync("/healthz"));
        var page = await http.GetAsync("/");
        Assert.Contains("<title>PacketBot</title>", await page.Content.ReadAsStringAsync());
        Assert.Contains("frame-ancestors 'none'", page.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Contains("textContent", await http.GetStringAsync("/app.js"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/instances")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post("/api/instances/1/start", new { })).StatusCode);
        Assert.False(instance.BotActive);
    }

    [Fact]
    public async Task WrongPasswordsAreLimited()
    {
        for (int i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await Post("/api/login", new { password = "nope-nope" })).StatusCode);
        // Locked for a minute, even with the right password.
        Assert.Equal((HttpStatusCode)429, (await Post("/api/login", new { password = Password })).StatusCode);
    }

    [Fact]
    public async Task PostsWithoutTheHeaderAreRejected()
    {
        await Login();
        var response = await Post("/api/instances/1/start", new { }, csrf: false);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(instance.BotActive);
    }

    [Fact]
    public async Task CollectIsConfiguredAndStartedFromTheBrowser()
    {
        await Login();
        string list = await http.GetStringAsync("/api/instances");
        Assert.Contains("\"userId\":" + userId, list);
        Assert.DoesNotContain("test-php-session-0001", list); // never shown

        Assert.True((await Reply("/api/instances/1/settings", new { key = "modulo", value = "collect" })).Ok);
        Assert.True((await Reply("/api/instances/1/settings", new { key = "mapa_collect", value = "1" })).Ok);
        var bad = await Reply("/api/instances/1/settings", new { key = "spins", value = "3" });
        Assert.False(bad.Ok);
        Assert.Contains("Spins no validos", bad.Message);

        string settings = await http.GetStringAsync("/api/instances/1/settings");
        using (var json = JsonDocument.Parse(settings))
        {
            var root = json.RootElement;
            Assert.Equal("Collect", root.GetProperty("module").GetString());
            Assert.Equal(1, root.GetProperty("collectMapId").GetInt32());
            Assert.Contains(root.GetProperty("boxes").EnumerateArray(), b => b.GetProperty("name").GetString() == "BONUS_BOX");
            Assert.Contains(root.GetProperty("maps").EnumerateArray(), m => m.GetProperty("label").GetString() == "5-1");
            Assert.Equal(9, root.GetProperty("autoBuy").GetArrayLength());
        }
        Assert.DoesNotContain("test-php-session-0001", settings);

        // Unknown names are ignored: only what the page offered is saved.
        var boxes = await Reply("/api/instances/1/boxes", new { selected = new[] { "BONUS_BOX", "<script>" } });
        Assert.Equal("Cajas de Collect en 1-1: BONUS_BOX.", boxes.Message);
        Assert.Equal(new[] { "BONUS_BOX" }, BotSettingsStore.Load("wordgalaxy", userId.ToString()).CollectTypesByMap[1]);

        instance.Session.Api.User.UserData.MapId = 1;
        Assert.Equal("Inicio de Collect solicitado.", (await Reply("/api/instances/1/start", new { })).Message);
        await FakeGameServer.WaitUntil(() => instance.BotRunning);
        Assert.Contains("\"running\":true", await http.GetStringAsync("/api/instances"));
        Assert.Equal("Bot detenido.", (await Reply("/api/instances/1/stop", new { })).Message);
        Assert.False(instance.BotActive);
        Assert.False((await Reply("/api/instances/9/stop", new { })).Ok);
    }

    [Fact]
    public async Task MinimapShowsTheShipAndWhatIsAroundIt()
    {
        var user = instance.Session.Api.User;
        user.UserData.MapId = 91; // 5-1
        user.Position.X = 10_000;
        user.Position.Y = 6_000;
        user.ShipInfo.Hp = 50;
        user.ShipInfo.MaxHp = 100;
        user.Players.Add(new FunnyProject.UserCollections.Objects.Player { ID = 5, X = 11_000, Y = 6_500, IsNpc = true }, true);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/instances/1/minimap")).StatusCode);
        await Login();
        using var json = JsonDocument.Parse(await http.GetStringAsync("/api/instances/1/minimap"));
        var m = json.RootElement;
        Assert.Equal("5-1", m.GetProperty("mapName").GetString());
        Assert.Equal(10_000, m.GetProperty("hero").GetProperty("x").GetInt32());
        Assert.Equal(50, m.GetProperty("hp").GetProperty("value").GetInt32());
        Assert.Equal(11_000, m.GetProperty("npcs")[0].GetProperty("x").GetInt32());
        Assert.True(m.GetProperty("width").GetInt32() > 0);
    }

    [Fact]
    public async Task ShipItemsAndStationsAreShown()
    {
        await Login();
        Assert.Empty(JsonDocument.Parse(await http.GetStringAsync("/api/instances/1/items")).RootElement.EnumerateArray());

        // Captured item state (packet 12754): rocket launcher HST-2, 1 of 5.
        var user = instance.Session.Api.User;
        user.Items.Update(new FunnyProject.Commands.Read.ItemStatus(Convert.FromHexString(
            "003FF000000000000015A300002565717569706D656E745F776561706F6E5F726F636B65746C61756E636865725F6873742D32010100002BEE00000001488C7B6100000013747469705F726F636B65746C61756E63686572613C00056712000000010423002565717569706D656E745F776561706F6E5F726F636B65746C61756E636865725F6873742D32613C000167126F500006255459504525765CAB9B305B2BEE00000001488C7B6100000013747469705F726F636B65746C61756E63686572613C00056712000000010423002565717569706D656E745F776561706F6E5F726F636B65746C61756E636865725F6873742D32613C000167126F500006255459504525765CAB9B401400000000000000002565717569706D656E745F776561706F6E5F726F636B65746C61756E636865725F6873742D3201"), 0), DateTime.UtcNow);
        using (var items = JsonDocument.Parse(await http.GetStringAsync("/api/instances/1/items")))
        {
            var item = Assert.Single(items.RootElement.EnumerateArray());
            Assert.Equal("Armas", item.GetProperty("category").GetString());
            Assert.Equal(1, item.GetProperty("amount").GetDouble());
            Assert.Equal(5, item.GetProperty("maximum").GetDouble());
        }
        Assert.Contains("HST-2: 1 / 5", instance.ShipItemsStatus(1));

        using var map = JsonDocument.Parse(await http.GetStringAsync("/api/instances/1/minimap"));
        Assert.Equal(JsonValueKind.Array, map.RootElement.GetProperty("stations").ValueKind);
    }

    [Fact]
    public async Task PreferredZonesAreDrawnOnTheMinimapOfTheCurrentMap()
    {
        instance.Session.Api.User.UserData.MapId = 307; // 2BL
        await Login();
        var added = await Reply("/api/instances/1/zones", new { x1 = 6000, y1 = 5000, x2 = 1000, y2 = 2000 });
        Assert.True(added.Ok, added.Message);
        Assert.False((await Reply("/api/instances/1/zones", new { x1 = 0, y1 = 0, x2 = 100, y2 = 100 })).Ok);

        using (var json = JsonDocument.Parse(await http.GetStringAsync("/api/instances/1/minimap")))
        {
            var zone = Assert.Single(json.RootElement.GetProperty("preferred").EnumerateArray());
            Assert.Equal(1000, zone.GetProperty("x").GetInt32());
            Assert.Equal(3000, zone.GetProperty("height").GetInt32());
        }

        Assert.False((await Reply("/api/instances/1/zones/remove", new { x = 9000, y = 9000 })).Ok);
        Assert.True((await Reply("/api/instances/1/zones/remove", new { x = 3000, y = 3000 })).Ok);
        Assert.True((await Reply("/api/instances/1/zones/clear", new { })).Ok);
        using var after = JsonDocument.Parse(await http.GetStringAsync("/api/instances/1/minimap"));
        Assert.Empty(after.RootElement.GetProperty("preferred").EnumerateArray());
    }

    [Fact]
    public async Task MinimapPicturesAreProxiedAndCached()
    {
        int calls = 0;
        var images = new MinimapImages(new FakeImageHttp(request =>
        {
            Interlocked.Increment(ref calls);
            Assert.Equal("wordgalaxy.space", request.RequestUri!.Host);
            return request.RequestUri.AbsolutePath.EndsWith("minimap-1-700.jpg")
                ? new HttpResponseMessage(HttpStatusCode.OK)
                  { Content = new ByteArrayContent(new byte[] { 0xFF, 0xD8, 1 }) { Headers = { ContentType = new("image/jpeg") } } }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        Assert.Equal(new byte[] { 0xFF, 0xD8, 1 }, await images.GetAsync(1, CancellationToken.None));
        Assert.Equal(new byte[] { 0xFF, 0xD8, 1 }, await images.GetAsync(1, CancellationToken.None));
        Assert.Equal(1, calls);
        Assert.Null(await images.GetAsync(2, CancellationToken.None)); // 3 variants tried
        Assert.Null(await images.GetAsync(2, CancellationToken.None)); // miss cached
        Assert.Equal(4, calls);
        Assert.Null(await images.GetAsync(-1, CancellationToken.None));
        Assert.Equal(4, calls);
    }

    private sealed class FakeImageHttp : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> respond;
        public FakeImageHttp(Func<HttpRequestMessage, HttpResponseMessage> respond) => this.respond = respond;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(respond(request));
    }

    [Fact]
    public async Task LogoutEndsTheSession()
    {
        await Login();
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/instances")).StatusCode);
        await Post("/api/logout", new { });
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/instances")).StatusCode);
    }

    [Fact]
    public void LoginGuardLocksForAMinute()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var guard = new LoginGuard(() => now);
        for (int i = 0; i < 4; i++) guard.Failed();
        Assert.False(guard.Locked);
        guard.Failed();
        Assert.True(guard.Locked);
        now = now.AddSeconds(61);
        Assert.False(guard.Locked);
        Assert.True(LoginGuard.Matches("abc", "abc"));
        Assert.False(LoginGuard.Matches("abd", "abc"));
    }
}
