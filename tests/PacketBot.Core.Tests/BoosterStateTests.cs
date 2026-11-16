using System.Text.Json;
using FunnyProject.Logic;
using Xunit;

namespace PacketBot.Core.Tests;

// Characterization tests for the passive WG_BOOSTERS snapshot. Auto-buy relies
// on TryGetRemainingSeconds; any regression here can cause duplicate purchases.
public class BoosterStateTests
{
    private const string Prefix = "0|WG_BOOSTERS|v1|";

    internal static string Message(int accountId = 42, int categoryCount = 9,
        long sample = 1_000, long completed = 1_001, string damageBooster = "DMG_B01",
        long remaining = 3_600, string timerMode = "online_tick", bool active = true)
    {
        var categories = Enumerable.Range(0, categoryCount).Select(attribute => new
        {
            attribute,
            totalPercent = attribute == 2 ? 10.0 : 0.0,
            personalPercent = attribute == 2 ? 10.0 : 0.0,
            shared = new { percent = 0.0 },
            cbs = new { percent = 0.0 },
            personal = attribute == 2
                ? new object[]
                {
                    new
                    {
                        percent = 10.0, remainingSeconds = remaining, expiresUnix = 0L,
                        rawTypeName = damageBooster, displayTypeName = "Daño",
                        timerMode, active
                    }
                }
                : Array.Empty<object>()
        });
        string json = JsonSerializer.Serialize(new
        {
            version = 1, ok = true, accountId, serverSampleUnix = sample,
            serverCompletedUnix = completed, categories
        });
        return Prefix + Uri.EscapeDataString(json);
    }

    [Fact]
    public void ValidSnapshotIsAccepted()
    {
        var state = new BoosterState();
        Assert.True(state.TryReceive(Message(), 4224, 42));
        Assert.Equal(4224, state.TransportPacketId);
        Assert.NotEqual(DateTime.MinValue, state.LastUpdatedUtc);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0|WG_BOOSTERS|v2|{}")]
    [InlineData("0|WG_BOOSTERS|v1|not-json")]
    public void InvalidMessagesAreRejected(string message) =>
        Assert.False(new BoosterState().TryReceive(message, 4224));

    [Fact]
    public void SnapshotForAnotherAccountIsRejected() =>
        Assert.False(new BoosterState().TryReceive(Message(accountId: 7), 4224, 42));

    [Fact]
    public void SnapshotWithMissingCategoriesIsRejected() =>
        Assert.False(new BoosterState().TryReceive(Message(categoryCount: 8), 4224));

    [Fact]
    public void OlderSnapshotDoesNotReplaceNewerOne()
    {
        var state = new BoosterState();
        Assert.True(state.TryReceive(Message(sample: 2_000, completed: 2_000), 1));
        Assert.False(state.TryReceive(Message(sample: 1_000, completed: 1_000), 2));
        Assert.Equal(1, state.TransportPacketId);
    }

    [Fact]
    public void InvalidSnapshotKeepsLastKnownState()
    {
        var state = new BoosterState();
        Assert.True(state.TryReceive(Message(), 1));
        Assert.False(state.TryReceive(Prefix + "%7B", 2));
        Assert.True(state.TryGetRemainingSeconds("DMG_B01", 2, out _));
    }

    [Fact]
    public void RemainingSecondsRequiresExactBoosterName()
    {
        var state = new BoosterState();
        Assert.True(state.TryReceive(Message(damageBooster: "DMG-B01"), 1));
        // Hyphens are normalised to underscores, case is ignored.
        Assert.True(state.TryGetRemainingSeconds("dmg_b01", 2, out long seconds));
        Assert.Equal(3_600, seconds);
        Assert.False(state.TryGetRemainingSeconds("DMG_B02", 2, out _));
        Assert.False(state.TryGetRemainingSeconds("DMG_B01", 1, out _));
    }

    [Fact]
    public void InactiveBoosterIsNotReported()
    {
        var state = new BoosterState();
        Assert.True(state.TryReceive(Message(active: false), 1));
        Assert.False(state.TryGetRemainingSeconds("DMG_B01", 2, out _));
    }

    [Fact]
    public void WallClockTimerSubtractsServerProcessingTime()
    {
        var state = new BoosterState();
        Assert.True(state.TryReceive(Message(sample: 1_000, completed: 1_010,
            remaining: 100, timerMode: "wall_clock"), 1));
        Assert.True(state.TryGetRemainingSeconds("DMG_B01", 2, out long seconds));
        Assert.InRange(seconds, 88, 90);
    }

    [Fact]
    public void ClearedStateNeverAuthorisesPurchase()
    {
        var state = new BoosterState();
        Assert.True(state.TryReceive(Message(), 1));
        state.Clear();
        Assert.False(state.TryGetRemainingSeconds("DMG_B01", 2, out _));
        Assert.Contains("esperando", state.Summary());
    }
}
