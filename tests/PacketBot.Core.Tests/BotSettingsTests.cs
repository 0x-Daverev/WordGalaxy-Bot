using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FunnyProject;
using Xunit;

namespace PacketBot.Core.Tests;

public class BotSettingsTests : IDisposable
{
    // A unique identity per test run: the store writes to the real per-user
    // settings folder, so each test cleans up only its own file.
    private readonly string server = "tests-" + Guid.NewGuid().ToString("N");
    private const string Username = "regression-user";

    private string SettingsPath()
    {
        string identity = server.ToLowerInvariant() + ":" + Username;
        string name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))) + ".json";
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WordGalaxyPacketBot", "settings", name);
    }

    public void Dispose()
    {
        try { File.Delete(SettingsPath()); } catch (IOException) { }
    }

    [Fact]
    public void DefaultsNeverEnablePurchasesOrReconnect()
    {
        var settings = new BotSettings();
        Assert.Empty(settings.AutoBuyBoosters);
        Assert.Empty(settings.BoosterPurchaseAttemptUtc);
        Assert.False(settings.AutoReconnect);
        Assert.Equal("Collect", settings.SelectedModule);
        Assert.Equal(100, settings.Spins);
        Assert.Equal("ucb-100", settings.Laser);
        Assert.Equal("ucb-100", settings.NpcKillerLaser);
        Assert.Equal("both", settings.AttackMode);
    }

    [Fact]
    public void MissingFileLoadsDefaults()
    {
        var loaded = BotSettingsStore.Load(server, Username);
        Assert.Equal("Collect", loaded.SelectedModule);
        Assert.Empty(loaded.AutoBuyBoosters);
    }

    [Fact]
    public void SaveAndLoadRoundTrip()
    {
        var attempt = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
        var settings = new BotSettings
        {
            SelectedModule = "NpcKiller",
            Spins = 10,
            Laser = "mcb-50",
            NpcKillerLaser = "lcb-10",
            AttackMode = "kami",
            NpcKillerMapId = 5,
            NpcKillerTargetsByMap = new() { [5] = new() { "-=[ Streuner ]=-" } },
            AutoBuyBoosters = new() { [1] = true },
            BoosterPurchaseAttemptUtc = new() { [1] = attempt },
            AllowedInviterId = 77,
        };
        BotSettingsStore.Save(server, Username, settings);

        // Identity is case- and whitespace-insensitive.
        var loaded = BotSettingsStore.Load(" " + server.ToUpperInvariant(), Username.ToUpperInvariant());
        Assert.Equal("NpcKiller", loaded.SelectedModule);
        Assert.Equal(10, loaded.Spins);
        Assert.Equal("mcb-50", loaded.Laser);          // Galaxy Gates
        Assert.Equal("lcb-10", loaded.NpcKillerLaser); // NPC Killer, independent
        Assert.Equal("kami", loaded.AttackMode);
        Assert.Equal(new[] { "-=[ Streuner ]=-" }, loaded.NpcKillerTargetsByMap[5]);
        Assert.True(loaded.AutoBuyBoosters[1]);
        Assert.Equal(attempt, loaded.BoosterPurchaseAttemptUtc[1].ToUniversalTime());
        Assert.Equal(77, loaded.AllowedInviterId);
    }

    [Fact]
    public void InvalidValuesAreNormalisedOnLoad()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath())!);
        File.WriteAllText(SettingsPath(), JsonSerializer.Serialize(new
        {
            Spins = 3, Laser = "unknown", NpcKillerLaser = "x", AttackMode = "rockets", AllowedInviterId = 0
        }));
        var loaded = BotSettingsStore.Load(server, Username);
        Assert.Equal(100, loaded.Spins);
        Assert.Equal("ucb-100", loaded.Laser);
        Assert.Equal("ucb-100", loaded.NpcKillerLaser);
        Assert.Equal("both", loaded.AttackMode);
        Assert.Null(loaded.AllowedInviterId);
    }

    [Fact]
    public void CorruptFileFallsBackToDefaults()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath())!);
        File.WriteAllText(SettingsPath(), "{ not json");
        var loaded = BotSettingsStore.Load(server, Username);
        Assert.Empty(loaded.AutoBuyBoosters);
    }

    [Fact]
    public void BotActionStartsIdle()
    {
        var state = new BotActionState();
        Assert.Equal(BotAction.Idle, state.Current);
        state.Current = BotAction.Attack;
        Assert.Equal(BotAction.Attack, state.Current);
    }
}
