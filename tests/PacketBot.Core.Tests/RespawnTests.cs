using System.Buffers.Binary;
using System.Text;
using FunnyProject.Commands;
using FunnyProject.Commands.Read;
using FunnyProject.Sessions;
using Xunit;

namespace PacketBot.Core.Tests;

// HP=0: WordGalaxy respawns the ship after a TCP restart and a new Login.
public class RespawnTests : IDisposable
{
    private const string Sid = "0123456789abcdef0123456789abcdef";
    private readonly FakeGameServer server = new();
    private readonly InstanceManager manager;

    public RespawnTests()
    {
        var web = new WordGalaxyWebClient(cookies => new FakeWordGalaxyHttp(cookies,
            (_, _) => FakeWordGalaxyHttp.Html(FakeWordGalaxyHttp.MapHtml(sessionId: Sid, host: "127.0.0.1"))));
        manager = new InstanceManager(web, server.Port);
    }

    public void Dispose()
    {
        manager.Dispose();
        server.Dispose();
    }

    // Inverse of HpUpdate.Read: Hp = rotl(x, 3), MaxHp = rotr(y, 6).
    private static byte[] HpUpdatePacket(int hp, int maxHp)
    {
        var body = new byte[16];
        BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(0), (int)((uint)hp >> 3 | (uint)hp << 29));
        BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(4), (int)((uint)maxHp << 6 | (uint)maxHp >> 26));
        return FakeGameServer.Frame(HpUpdate.ID, body);
    }

    private static int LoginCount(FakeGameServer.Connection connection)
    {
        // Count Login frames (ID 10996) in everything received so far.
        byte[] bytes = connection.Snapshot();
        int count = 0, offset = 0;
        while (offset + 4 <= bytes.Length)
        {
            int length = (bytes[offset] << 8) | bytes[offset + 1];
            if ((short)((bytes[offset + 2] << 8) | bytes[offset + 3]) == Login.ID) count++;
            offset += 2 + length;
        }
        return count;
    }

    private async Task<(WordGalaxySession, FakeGameServer.Connection)> OpenAsync()
    {
        var session = await manager.OpenAsync("test-php-session-0001");
        var connection = server.NextConnection();
        await FakeGameServer.WaitUntil(() => LoginCount(connection) == 1);
        return (session, connection);
    }

    [Fact]
    public async Task DeathRestartsTcpAndLogsInAgain()
    {
        var (session, first) = await OpenAsync();
        FakeGameServer.Send(first, HpUpdatePacket(0, 10_000));

        var second = server.NextConnection();
        await FakeGameServer.WaitUntil(() => LoginCount(second) == 1);
        Assert.Contains(Sid, Encoding.ASCII.GetString(second.Snapshot()));
        await first.Closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, LoginCount(first)); // no second Login on the old connection
        await FakeGameServer.WaitUntil(() => session.PacketManager.Connected);
    }

    [Fact]
    public async Task RepeatedHpZeroTriggersOnlyOneRestart()
    {
        var (_, first) = await OpenAsync();
        FakeGameServer.Send(first, HpUpdatePacket(0, 10_000));
        FakeGameServer.Send(first, HpUpdatePacket(0, 10_000));
        server.NextConnection();
        Assert.True(server.NoConnectionWithin(1_000));
    }

    [Fact]
    public async Task SecondDeathAfterRespawnIsHandledAgain()
    {
        var (session, first) = await OpenAsync();
        FakeGameServer.Send(first, HpUpdatePacket(0, 10_000));
        var second = server.NextConnection();
        await FakeGameServer.WaitUntil(() => session.PacketManager.Connected && LoginCount(second) == 1);

        FakeGameServer.Send(second, HpUpdatePacket(10_000, 10_000)); // respawned
        await FakeGameServer.WaitUntil(() => session.Api.User.ShipInfo.Hp == 10_000);
        FakeGameServer.Send(second, HpUpdatePacket(0, 10_000));
        var third = server.NextConnection();
        await FakeGameServer.WaitUntil(() => LoginCount(third) == 1);
    }
}
