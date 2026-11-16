using System.Text.RegularExpressions;
using FunnyProject;
using Xunit;

namespace PacketBot.Core.Tests;

public class BotVersionTests
{
    [Fact]
    public void VersionComesFromDirectoryBuildProps()
    {
        // MAYOR.MENOR.PARCHE, optionally with a CI suffix (-ci.N); no commit hash.
        Assert.Matches(new Regex(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?$"), BotVersion.Current);
        Assert.StartsWith(BotVersion.Current, BotVersion.Full);
        Assert.NotEqual("0.0.0", BotVersion.Current);
    }
}
