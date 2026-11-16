using PacketBot.Server;
using Xunit;

namespace PacketBot.Server.Tests;

public class LogWriterTests
{
    private static readonly DateTime Now = new(2026, 11, 11, 16, 44, 40, 305);

    [Fact]
    public void EveryLineGetsOneTimestamp()
    {
        var output = new StringWriter();
        var writer = new LogWriter(output, clock: () => Now) { NewLine = "\n" };
        writer.Write("a");
        writer.WriteLine("b");
        writer.WriteLine("c\r\nd");
        writer.WriteLine();
        Assert.Equal("16:44:40.305 ab\n16:44:40.305 c\n16:44:40.305 d\n\n", output.ToString());
    }

    [Fact]
    public void JournaldPriorityPrefixesEachLine()
    {
        var output = new StringWriter();
        var writer = new LogWriter(output, LogWriter.Error, () => Now);
        writer.WriteLine("fallo\notro");
        Assert.Equal("<3>16:44:40.305 fallo\n<3>16:44:40.305 otro\n", output.ToString());
        Assert.True(LogWriter.UnderJournald(n => n == "JOURNAL_STREAM" ? "8:12345" : null));
        Assert.False(LogWriter.UnderJournald(_ => null));
    }

    [Fact]
    public void SecretsNeverReachTheLog()
    {
        LogSecrets.Add("registered-session-value-0001");
        var output = new StringWriter();
        var writer = new LogWriter(output, clock: () => Now);
        writer.WriteLine("cookie registered-session-value-0001 rechazada");
        writer.WriteLine("Cookie: PHPSESSID=abcdef0123456789; path=/");
        writer.WriteLine("GET https://api.telegram.org/bot123456:ABC-def_789/getUpdates");
        string log = output.ToString();
        Assert.DoesNotContain("registered-session-value-0001", log);
        Assert.DoesNotContain("abcdef0123456789", log);
        Assert.DoesNotContain("ABC-def_789", log);
        Assert.Contains("cookie **** rechazada", log);
        Assert.Contains("PHPSESSID=****; path=/", log);
        Assert.Contains("/bot****/getUpdates", log);
    }

    [Fact]
    public async Task LinesCarryTheAccountOfTheCodeThatWroteThem()
    {
        var output = new StringWriter();
        var writer = new LogWriter(output, clock: () => Now);
        Task tagged = Task.Run(async () =>
        {
            var tag = LogTag.Enter("cuenta 1");
            writer.WriteLine("consultando");
            await Task.Run(() => { tag.Name = "123456"; writer.WriteLine("conectado"); });
        });
        await tagged;
        writer.WriteLine("sin cuenta");
        Assert.Equal("16:44:40.305 [cuenta 1] consultando\n16:44:40.305 [123456] conectado\n16:44:40.305 sin cuenta\n",
            output.ToString());
    }
}
