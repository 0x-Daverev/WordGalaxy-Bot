using FunnyProject;
using PacketBot.Server;
using Xunit;

namespace PacketBot.Server.Tests;

public class ServerSettingsAccessTests : IDisposable
{
    private readonly string user = "settings-test-" + Guid.NewGuid().ToString("N");
    private readonly ServerSettingsAccess access;

    public ServerSettingsAccessTests() => access = new ServerSettingsAccess("wordgalaxy", user);

    public void Dispose()
    {
        try { File.Delete(access.Path); } catch (IOException) { }
    }

    [Fact]
    public void UpdatesFromDifferentWritersAreAllKept()
    {
        var stale = access.Read();
        access.Update(s => s.BoosterPurchaseAttemptUtc[2051] = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        access.Update(s => s.NpcKillerTargetsByMap[1] = new() { "-=[ Streuner ]=-" });

        var saved = BotSettingsStore.Load("wordgalaxy", user);
        Assert.True(saved.BoosterPurchaseAttemptUtc.ContainsKey(2051));
        Assert.Equal(new[] { "-=[ Streuner ]=-" }, saved.NpcKillerTargetsByMap[1]);
        // Copy-on-write: a snapshot already handed out is never changed.
        Assert.Empty(stale.BoosterPurchaseAttemptUtc);
        Assert.Same(access.Read(), access.Read());
    }

    [Fact]
    public void ConcurrentUpdatesDoNotLoseChanges()
    {
        Parallel.For(0, 40, i => access.Update(s => s.BoosterPurchaseAttemptUtc[i] = DateTime.UnixEpoch));
        Assert.Equal(40, BotSettingsStore.Load("wordgalaxy", user).BoosterPurchaseAttemptUtc.Count);
    }

    [Fact]
    public void ExternalEditsAreReadAgain()
    {
        Assert.Equal("", access.Read().RunFormation);
        BotSettingsStore.Save("wordgalaxy", user, new BotSettings { RunFormation = "arrow" });
        File.SetLastWriteTimeUtc(access.Path, DateTime.UtcNow.AddSeconds(5)); // coarse file systems
        Assert.Equal("arrow", access.Read().RunFormation);
    }
}
