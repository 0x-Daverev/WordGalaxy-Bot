using FunnyProject.Sessions;
using Xunit;

namespace PacketBot.Core.Tests;

public class WordGalaxyMapPageTests
{
    // Fixture shaped like the relevant fragment of /map; values are fake.
    private const string Html = """
        <script>
          var flashembed = {
            "userID": "123456",
            "sessionID": "abcdEFGH1234",
            "localGS": "gs1.example.test",
            "username": "Capit&aacute;n"
          };
        </script>
        """;

    [Fact]
    public void ParsesCredentialsAndHost()
    {
        var page = WordGalaxyMapPage.Parse(Html);
        Assert.Equal(123456, page.UserId);
        Assert.Equal("abcdEFGH1234", page.SessionId);
        Assert.Equal("gs1.example.test", page.GameHost);
        Assert.Equal("Capitán", page.AccountName);
    }

    [Theory]
    [InlineData("\"userID\": 77, \"sessionID\": \"s\", localGS = 'host.test'", 77)]
    [InlineData("\"USERID\":\"5\",\"SESSIONID\":\"s\",'localGS':'host.test'", 5)]
    public void AcceptsVariantsSeenInTheField(string html, int expectedUser) =>
        Assert.Equal(expectedUser, WordGalaxyMapPage.Parse(html).UserId);

    [Fact]
    public void AbsoluteUrlInLocalGsIsReducedToHost() =>
        Assert.Equal("gs2.example.test",
            WordGalaxyMapPage.Parse(Html.Replace("gs1.example.test", "https://gs2.example.test/socket")).GameHost);

    [Theory]
    [InlineData("\"sessionID\": \"s\", \"localGS\": \"h.test\"")]             // no userID
    [InlineData("\"userID\": \"1\", \"localGS\": \"h.test\"")]               // no sessionID
    [InlineData("\"userID\": \"0\", \"sessionID\": \"s\", \"localGS\": \"h.test\"")] // invalid ID
    [InlineData("\"userID\": \"1\", \"sessionID\": \"s\"")]                  // no localGS
    public void MissingDataIsRejected(string html) =>
        Assert.Throws<InvalidOperationException>(() => WordGalaxyMapPage.Parse(html));

    [Theory]
    [InlineData("host.test:8080")]
    [InlineData("-host.test")]
    [InlineData("host.test.")]
    [InlineData("ho st")]
    [InlineData("")]
    public void InvalidHostsAreRejected(string localGs) =>
        Assert.Throws<InvalidOperationException>(() => WordGalaxyMapPage.NormalizeGameHost(localGs));

    [Fact]
    public void ErrorMessagesAndToStringNeverExposeTheSessionId()
    {
        var page = WordGalaxyMapPage.Parse(Html);
        Assert.DoesNotContain("abcdEFGH1234", page.ToString());
        Assert.Contains("abcd****", page.ToString());
        var ex = Assert.Throws<InvalidOperationException>(() =>
            WordGalaxyMapPage.Parse("\"userID\": \"1\", \"sessionID\": \"secret-value\""));
        Assert.DoesNotContain("secret-value", ex.Message);
    }

    [Theory]
    [InlineData(null, "****")]
    [InlineData("abc", "****")]
    [InlineData("abcdef", "abcd****")]
    public void MaskSecretKeepsAtMostFourCharacters(string? value, string expected) =>
        Assert.Equal(expected, WordGalaxyMapPage.MaskSecret(value));
}
