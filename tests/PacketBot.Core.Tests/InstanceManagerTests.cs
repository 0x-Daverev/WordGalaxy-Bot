using System.Net;
using System.Text;
using FunnyProject.Commands;
using FunnyProject.Sessions;
using Xunit;

namespace PacketBot.Core.Tests;

// End-to-end session tests against a local fake game server: HTTP /map is
// faked, TCP is real (loopback). No WinForms involved.
public class InstanceManagerTests : IDisposable
{
    private const int UserId = 123456;
    private const string Sid = "0123456789abcdef0123456789abcdef";
    private const string PhpSessId = "test-php-session-0001";

    private readonly FakeGameServer server = new();
    private Func<HttpResponseMessage> mapResponse =
        () => FakeWordGalaxyHttp.Html(FakeWordGalaxyHttp.MapHtml(UserId, Sid, "127.0.0.1"));
    private readonly InstanceManager manager;

    public InstanceManagerTests()
    {
        var web = new WordGalaxyWebClient(cookies => new FakeWordGalaxyHttp(cookies, (_, _) => mapResponse()));
        manager = new InstanceManager(web, server.Port);
    }

    public void Dispose()
    {
        manager.Dispose();
        server.Dispose();
    }

    private static async Task AssertLoginReceived(FakeGameServer.Connection connection)
    {
        await FakeGameServer.WaitUntil(() => connection.Snapshot().Length >= 4 &&
            Encoding.ASCII.GetString(connection.Snapshot()).Contains(Sid));
        byte[] bytes = connection.Snapshot();
        Assert.Equal(Login.ID, (short)((bytes[2] << 8) | bytes[3]));
    }

    private static void SendBoosters(FakeGameServer.Connection connection, long sample) =>
        FakeGameServer.Send(connection, FakeGameServer.Frame(4224, FakeGameServer.Utf(
            BoosterStateTests.Message(accountId: UserId, sample: sample, completed: sample))));

    [Fact]
    public async Task OpenAuthenticatesConnectsAndReceivesPackets()
    {
        var session = await manager.OpenAsync(PhpSessId);
        var connection = server.NextConnection();
        await AssertLoginReceived(connection);

        Assert.Equal(SessionState.Connected, session.State);
        Assert.Equal(1, manager.NumberOf(session));
        Assert.Same(session, manager.GetByNumber(1));
        Assert.Same(session, manager.GetByUserId(UserId));
        Assert.Equal(("123456", "wordgalaxy"), (session.Api.User.Username, session.Api.User.Server));
        Assert.Equal(PhpSessId, session.Api.User.UserData.PHPSESSID);

        // An inbound packet goes through Client -> PacketManager.Parse -> game state.
        SendBoosters(connection, 1_000);
        await FakeGameServer.WaitUntil(() => session.Api.User.Boosters.TransportPacketId == 4224);
    }

    [Fact]
    public async Task SameAccountCannotBeOpenedTwice()
    {
        await manager.OpenAsync(PhpSessId);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.OpenAsync(PhpSessId));
        Assert.Contains("ya tiene una instancia", ex.Message);
        Assert.Single(manager.Instances);
    }

    [Fact]
    public async Task TcpRestartLogsInAgainAndKeepsPacketHandlers()
    {
        var session = await manager.OpenAsync(PhpSessId);
        await AssertLoginReceived(server.NextConnection());

        await session.PacketManager.RestartTcpAsync();
        var second = server.NextConnection();
        await AssertLoginReceived(second);

        SendBoosters(second, 2_000);
        await FakeGameServer.WaitUntil(() => session.Api.User.Boosters.TransportPacketId == 4224);
    }

    [Fact]
    public async Task StoppedBotDoesNotReconnectAfterServerDrop()
    {
        var session = await manager.OpenAsync(PhpSessId);
        var connection = server.NextConnection();
        await AssertLoginReceived(connection);
        Assert.False(session.PacketManager.AutoReconnectEnabled);

        connection.Client.Close();
        await FakeGameServer.WaitUntil(() => session.State == SessionState.Disconnected);
        // The first automatic attempt would happen after 3 s.
        Assert.True(server.NoConnectionWithin(4_500));
    }

    [Fact]
    public async Task ActiveBotReconnectsAfterServerDrop()
    {
        var session = await manager.OpenAsync(PhpSessId);
        var connection = server.NextConnection();
        await AssertLoginReceived(connection);
        session.PacketManager.AutoReconnectEnabled = true; // what Start bot does

        connection.Client.Close();
        var reconnected = server.NextConnection(timeoutMs: 10_000);
        await AssertLoginReceived(reconnected);
        await FakeGameServer.WaitUntil(() => session.State == SessionState.Connected);
    }

    [Fact]
    public async Task CloseDisconnectsAndStopsReconnectAttempts()
    {
        var session = await manager.OpenAsync(PhpSessId);
        var connection = server.NextConnection();
        await AssertLoginReceived(connection);
        session.PacketManager.AutoReconnectEnabled = true;

        Assert.True(manager.Close(UserId));
        await connection.Closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(SessionState.Closed, session.State);
        Assert.True(session.Api.IsDisposed);
        Assert.Empty(manager.Instances);
        Assert.True(server.NoConnectionWithin(4_500));
    }

    [Fact]
    public async Task RestartReplacesSessionInTheSamePosition()
    {
        var old = await manager.OpenAsync(PhpSessId);
        var firstConnection = server.NextConnection();
        await AssertLoginReceived(firstConnection);

        var replacement = await manager.RestartAsync(UserId);
        await AssertLoginReceived(server.NextConnection());
        await firstConnection.Closed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotSame(old, replacement);
        Assert.Equal(SessionState.Closed, old.State);
        Assert.Equal(SessionState.Connected, replacement.State);
        Assert.Same(replacement, manager.GetByNumber(1));
    }

    [Fact]
    public async Task RestartWithExpiredSessionKeepsTheOldOne()
    {
        var old = await manager.OpenAsync(PhpSessId);
        await AssertLoginReceived(server.NextConnection());

        mapResponse = () => FakeWordGalaxyHttp.Html("<html>login</html>", "https://wordgalaxy.space/");
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.RestartAsync(UserId));

        Assert.Equal(SessionState.Connected, old.State);
        Assert.Same(old, manager.GetByNumber(1));
    }

    [Fact]
    public async Task UnreachableGameServerLeavesNoInstance()
    {
        server.Dispose(); // nothing listens on the port any more
        await Assert.ThrowsAnyAsync<Exception>(() => manager.OpenAsync(PhpSessId));
        Assert.Empty(manager.Instances);
    }

    [Fact]
    public async Task ManagerDisposeClosesEverySession()
    {
        await manager.OpenAsync(PhpSessId);
        var connection = server.NextConnection();
        manager.Dispose();
        await connection.Closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => manager.OpenAsync(PhpSessId));
    }

    // Windows "Usuario y contraseña" goes through the same registry.
    [Fact]
    public async Task CredentialLoginOpensAnInstanceWithTheIssuedSession()
    {
        var web = new WordGalaxyWebClient(cookies => new FakeWordGalaxyHttp(cookies, (request, jar) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                jar.Add(WordGalaxyWebClient.MapUri, new Cookie("PHPSESSID", "issued-by-server"));
                return FakeWordGalaxyHttp.Html("{\"status\":true}");
            }
            return FakeWordGalaxyHttp.Html(request.RequestUri!.AbsolutePath == "/map"
                ? FakeWordGalaxyHttp.MapHtml(UserId, Sid, "127.0.0.1") : "<html></html>");
        }));
        using var credentials = new InstanceManager(web, server.Port);
        var session = await credentials.OpenWithCredentialsAsync("pilot", "secret");
        await AssertLoginReceived(server.NextConnection());
        Assert.Equal("issued-by-server", session.PhpSessionId);
        Assert.Equal(UserId, session.Api.User.UserData.ID);
        Assert.Same(session, credentials.GetByNumber(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => credentials.OpenWithCredentialsAsync("pilot", "secret"));
        Assert.True(credentials.Close(UserId));
        Assert.Empty(credentials.Instances);
    }
}
