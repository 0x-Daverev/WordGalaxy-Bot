using FunnyProject.Services;
using Xunit;

namespace PacketBot.Core.Tests;

// The /rank N reply must keep reading the "me" object of api_halloffame.php.
public class RankServiceTests
{
    [Fact]
    public void FormatsAllFieldsOfMe()
    {
        const string json = """
            {"me":{"rankId":7,"rankName":"Coronel","rankPoints":1234567,"experience":"98765432.5","honor":-1500}}
            """;
        Assert.Equal(
            "🏆 Rango — instancia 2\nID: 7\nNombre: Coronel\nPuntos: 1.234.567" +
            "\nExperiencia: 98.765.432,5\nHonor: -1.500",
            RankService.Format(json, 2));
    }

    [Fact]
    public void MissingFieldsAreReportedAsUnavailable()
    {
        string text = RankService.Format("""{"me":{"rankName":null,"honor":{}}}""", 1);
        Assert.Contains("ID: No disponible", text);
        Assert.Contains("Nombre: No disponible", text);
        Assert.Contains("Honor: No disponible", text);
    }

    [Theory]
    [InlineData("""{"top":[]}""")]
    [InlineData("""{"me":[1,2]}""")]
    public void ResponseWithoutMeObjectIsRejected(string json) =>
        Assert.Equal("La respuesta del ranking no contiene datos válidos en 'me'.", RankService.Format(json, 1));

    [Fact]
    public void InvalidJsonIsReported() =>
        Assert.Equal("El servidor devolvió un JSON de ranking no válido.", RankService.Format("<html>", 1));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task MissingPhpSessIdDoesNotCallTheServer(string phpSessionId) =>
        Assert.Equal("La instancia 3 no tiene un PHPSESSID disponible.",
            await RankService.FetchAsync(phpSessionId, 3));
}
