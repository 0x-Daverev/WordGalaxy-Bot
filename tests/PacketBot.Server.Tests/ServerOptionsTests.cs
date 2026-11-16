using PacketBot.Server;
using Xunit;

namespace PacketBot.Server.Tests;

public class ServerOptionsTests
{
    private static string? NoEnvironment(string _) => null;

    [Fact]
    public void ArgumentTakesPrecedenceOverEnvironment()
    {
        var (options, error) = ServerOptions.Parse(new[] { "--phpsessid", " abc " },
            name => name == ServerOptions.PhpSessionIdVariable ? "from-env" : null);
        Assert.Null(error);
        Assert.Equal("abc", options!.PhpSessionId);
        Assert.False(options.CheckOnly);
        Assert.Equal(TimeSpan.FromSeconds(30), options.HeroTimeout);
    }

    [Fact]
    public void EnvironmentIsUsedWhenNoArgument()
    {
        var (options, _) = ServerOptions.Parse(new[] { "--check", "--timeout", "45" },
            name => name == ServerOptions.PhpSessionIdVariable ? "from-env" : null);
        Assert.Equal(("from-env", true, TimeSpan.FromSeconds(45)),
            (options!.PhpSessionId, options.CheckOnly, options.HeroTimeout));
    }

    [Theory]
    [InlineData(new string[0], "Indica el PHPSESSID")]
    [InlineData(new[] { "--phpsessid" }, "Falta el valor")]
    [InlineData(new[] { "--phpsessid", " " }, "Falta el valor")]
    [InlineData(new[] { "--phpsessid", "x", "--timeout", "0" }, "--timeout")]
    [InlineData(new[] { "--phpsessid", "x", "--verbose" }, "Argumento desconocido")]
    public void InvalidArgumentsAreReported(string[] args, string expected)
    {
        var (options, error) = ServerOptions.Parse(args, NoEnvironment);
        Assert.Null(options);
        Assert.Contains(expected, error);
    }

    [Fact]
    public void StartCollectIsAccepted()
    {
        var (options, error) = ServerOptions.Parse(new[] { "--phpsessid", "x", "--start", "Collect" }, NoEnvironment);
        Assert.Null(error);
        Assert.Equal("collect", options!.StartModule);
    }

    [Fact]
    public void StartNpcIsAccepted() =>
        Assert.Equal("npc", ServerOptions.Parse(new[] { "--phpsessid", "x", "--start", "NPC" }, NoEnvironment).Options!.StartModule);

    [Theory]
    [InlineData(new[] { "--phpsessid", "x", "--start" }, "--start admite: collect, npc o gg.")]
    [InlineData(new[] { "--phpsessid", "x", "--start", "galaxy" }, "--start admite: collect, npc o gg.")]
    [InlineData(new[] { "--phpsessid", "x", "--start", "collect", "--check" }, "no se pueden combinar")]
    public void InvalidStartIsReported(string[] args, string expected) =>
        Assert.Contains(expected, ServerOptions.Parse(args, NoEnvironment).Error);

    [Fact]
    public void SeveralAccountsAreSeparatedByCommas()
    {
        var (options, _) = ServerOptions.Parse(new[] { "--phpsessid", "a, b,,a" }, NoEnvironment);
        Assert.Equal(new[] { "a", "b" }, options!.PhpSessionIds);
    }

    [Fact]
    public void TelegramIsReadFromTheEnvironment()
    {
        var env = new Dictionary<string, string>
        {
            [ServerOptions.PhpSessionIdVariable] = "x",
            [ServerOptions.TelegramTokenVariable] = " 123:abc ",
            [ServerOptions.TelegramChatVariable] = "555"
        };
        var (options, error) = ServerOptions.Parse(Array.Empty<string>(), n => env.GetValueOrDefault(n));
        Assert.Null(error);
        Assert.True(options!.TelegramEnabled);
        Assert.Equal(("123:abc", 555L), (options.TelegramToken, options.TelegramChatId));
        Assert.DoesNotContain("123:abc", options.ToString());
    }

    [Fact]
    public void TelegramIsOptional()
    {
        var (options, _) = ServerOptions.Parse(new[] { "--phpsessid", "x" }, NoEnvironment);
        Assert.False(options!.TelegramEnabled);
    }

    [Theory]
    [InlineData("123:abc", null, "PACKETBOT_TELEGRAM_CHAT_ID")]
    [InlineData("123:abc", "-5", "PACKETBOT_TELEGRAM_CHAT_ID")]
    [InlineData(null, "555", "PACKETBOT_TELEGRAM_TOKEN")]
    public void IncompleteTelegramSettingsAreRejected(string? token, string? chat, string expected)
    {
        var env = new Dictionary<string, string?>
        {
            [ServerOptions.TelegramTokenVariable] = token,
            [ServerOptions.TelegramChatVariable] = chat
        };
        var (options, error) = ServerOptions.Parse(new[] { "--phpsessid", "x" }, n => env.GetValueOrDefault(n));
        Assert.Null(options);
        Assert.Contains(expected, error);
    }

    [Fact]
    public void HelpReturnsUsage() =>
        Assert.Equal(ServerOptions.Usage, ServerOptions.Parse(new[] { "--help" }, NoEnvironment).Error);

    [Fact]
    public void ToStringNeverPrintsThePhpSessId()
    {
        var (options, _) = ServerOptions.Parse(new[] { "--phpsessid", "very-secret-value" }, NoEnvironment);
        Assert.DoesNotContain("very-secret-value", options!.ToString());
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("300", 300)]
    public void FailureExitDelayComesFromTheEnvironment(string? value, int seconds)
    {
        var (options, error) = ServerOptions.Parse(new[] { "--phpsessid", "abc" },
            name => name == ServerOptions.FailureExitDelayVariable ? value : null);
        Assert.Null(error);
        Assert.Equal(TimeSpan.FromSeconds(seconds), options!.FailureExitDelay);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("cinco")]
    public void InvalidFailureExitDelayIsRejected(string value)
    {
        var (options, error) = ServerOptions.Parse(new[] { "--phpsessid", "abc" },
            name => name == ServerOptions.FailureExitDelayVariable ? value : null);
        Assert.Null(options);
        Assert.Contains(ServerOptions.FailureExitDelayVariable, error);
    }

    [Fact]
    public void StartModuleCanComeFromTheEnvironment()
    {
        string? Env(string name) => name == ServerOptions.StartVariable ? "NPC" : null;
        Assert.Equal("npc", ServerOptions.Parse(new[] { "--phpsessid", "abc" }, Env).Options!.StartModule);
        // The command line wins, and --check never starts a module.
        Assert.Equal("gg", ServerOptions.Parse(new[] { "--phpsessid", "abc", "--start", "gg" }, Env).Options!.StartModule);
        Assert.Null(ServerOptions.Parse(new[] { "--phpsessid", "abc", "--check" }, Env).Options!.StartModule);
        var (bad, error) = ServerOptions.Parse(new[] { "--phpsessid", "abc" },
            n => n == ServerOptions.StartVariable ? "otro" : null);
        Assert.Null(bad);
        Assert.Contains(ServerOptions.StartVariable, error);
    }

    [Fact]
    public void WebPanelNeedsALongEnoughPassword()
    {
        string? Env(string name, string? password, string? port = null) =>
            name == ServerOptions.WebPasswordVariable ? password : name == ServerOptions.WebPortVariable ? port : null;
        var off = ServerOptions.Parse(new[] { "--phpsessid", "abc" }, n => Env(n, null)).Options!;
        Assert.False(off.WebEnabled);
        var on = ServerOptions.Parse(new[] { "--phpsessid", "abc" }, n => Env(n, "long-enough", "9090")).Options!;
        Assert.True(on.WebEnabled);
        Assert.Equal(9090, on.WebPort);
        Assert.DoesNotContain("long-enough", on.ToString());
        Assert.Contains(ServerOptions.WebPasswordVariable,
            ServerOptions.Parse(new[] { "--phpsessid", "abc" }, n => Env(n, "short")).Error);
        Assert.Contains(ServerOptions.WebPortVariable,
            ServerOptions.Parse(new[] { "--phpsessid", "abc" }, n => Env(n, "long-enough", "0")).Error);
    }
}
