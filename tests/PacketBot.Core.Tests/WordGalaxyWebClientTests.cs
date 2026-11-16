using System.Net;
using FunnyProject.Sessions;
using Xunit;

namespace PacketBot.Core.Tests;

public class WordGalaxyWebClientTests
{
    private const string PhpSessId = "test-php-session-0001";
    private FakeWordGalaxyHttp? http;

    private WordGalaxyWebClient Client(Func<HttpRequestMessage, CookieContainer, HttpResponseMessage> respond) =>
        new(cookies => http = new FakeWordGalaxyHttp(cookies, respond));

    [Fact]
    public async Task FetchMapSendsCookieAndParsesPage()
    {
        var client = Client((_, _) => FakeWordGalaxyHttp.Html(FakeWordGalaxyHttp.MapHtml(host: "gs.example.test")));
        var page = await client.FetchMapAsync(" " + PhpSessId + " ");
        Assert.Equal((123456, "gs.example.test"), (page.UserId, page.GameHost));
        Assert.Equal((HttpMethod.Get, "/map", PhpSessId), Assert.Single(http!.Requests));
        Assert.Equal("/map?wg_calendar=continue", Assert.Single(http.PathsAndQueries));
    }

    [Fact]
    public async Task RedirectAwayFromMapMeansExpiredSession()
    {
        var client = Client((_, _) => FakeWordGalaxyHttp.Html("<html>login</html>", "https://wordgalaxy.space/"));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.FetchMapAsync(PhpSessId));
        Assert.Equal("PHPSESSID caducado o redirigido fuera de /map.", ex.Message);
    }

    [Fact]
    public async Task RedirectToAnotherDomainIsRejected()
    {
        var client = Client((_, _) => FakeWordGalaxyHttp.Html(FakeWordGalaxyHttp.MapHtml(), "https://evil.example/map"));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.FetchMapAsync(PhpSessId));
        Assert.Contains("otro dominio", ex.Message);
    }

    [Fact]
    public async Task HttpErrorIsReportedWithoutTheSessionId()
    {
        var client = Client((_, _) => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.FetchMapAsync(PhpSessId));
        Assert.StartsWith("GET /map: HTTP 403", ex.Message);
        Assert.DoesNotContain(PhpSessId, ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a;b")]
    [InlineData("a,b")]
    [InlineData("a\nb")]
    public async Task InvalidPhpSessIdIsRejectedBeforeAnyRequest(string value)
    {
        var client = Client((_, _) => throw new InvalidOperationException("No request expected"));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.FetchMapAsync(value));
        Assert.Equal("Introduce un PHPSESSID valido.", ex.Message);
        Assert.Null(http);
    }

    [Fact]
    public async Task CredentialLoginReturnsIssuedSessionAndMap()
    {
        var client = Client((request, cookies) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                string body = request.Content!.ReadAsStringAsync().Result;
                Assert.Contains("username=pilot", body);
                Assert.Contains("action=login", body);
                Assert.Contains("captcha_id=", body);
                Assert.Equal(WordGalaxyWebClient.SiteUri, request.Headers.Referrer);
                // What HttpClientHandler does with a Set-Cookie response header.
                cookies.Add(WordGalaxyWebClient.MapUri, new Cookie("PHPSESSID", "issued-by-server"));
                return FakeWordGalaxyHttp.Html("{\"status\":true}");
            }
            return FakeWordGalaxyHttp.Html(request.RequestUri!.AbsolutePath == "/map"
                ? FakeWordGalaxyHttp.MapHtml(userId: 77) : "<html></html>");
        });

        var (phpSessionId, map) = await client.LoginAsync(" pilot ", "secret");
        Assert.Equal("issued-by-server", phpSessionId);
        Assert.Equal(77, map.UserId);
        Assert.Equal(new[] { "/", "/api/", "/map" }, http!.Requests.Select(r => r.Path));
        Assert.Equal("issued-by-server", http.Requests[^1].PhpSessionId);
    }

    [Fact]
    public async Task CredentialLoginWithoutIssuedCookieFails()
    {
        var client = Client((_, _) => FakeWordGalaxyHttp.Html("{\"status\":false}"));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.LoginAsync("pilot", "wrong"));
        Assert.StartsWith("Login sin PHPSESSID", ex.Message);
    }
}
