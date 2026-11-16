using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FunnyProject.Services
{
    // Hall of fame lookup behind the Telegram /rank N command, shared by
    // Windows and the server.
    public static class RankService
    {
        public static readonly Uri HallOfFameUri = new("https://wordgalaxy.space/files/api_halloffame.php");
        private const string UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
        private static readonly CultureInfo Spanish = CultureInfo.GetCultureInfo("es-ES");

        public static async Task<string> FetchAsync(string phpSessionId, int instanceIndex,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(phpSessionId))
                return "La instancia " + instanceIndex + " no tiene un PHPSESSID disponible.";

            var cookies = new CookieContainer();
            cookies.Add(HallOfFameUri, new Cookie("PHPSESSID", phpSessionId));
            using var handler = new HttpClientHandler
            {
                CookieContainer = cookies,
                UseCookies = true,
                AllowAutoRedirect = false
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            try
            {
                using var response = await http.GetAsync(HallOfFameUri, cancellationToken);
                if (!response.IsSuccessStatusCode)
                    return "No se pudo consultar el rango de la instancia " + instanceIndex +
                        " (HTTP " + (int)response.StatusCode + ").";
                return Format(await response.Content.ReadAsStringAsync(cancellationToken), instanceIndex);
            }
            catch (HttpRequestException)
            {
                return "Error HTTP al consultar el rango de la instancia " + instanceIndex + ".";
            }
            catch (TaskCanceledException)
            {
                return "La consulta de rango ha excedido el tiempo de espera.";
            }
        }

        // Formats the "me" object of the hall-of-fame JSON for Telegram.
        public static string Format(string json, int instanceIndex)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                if (!document.RootElement.TryGetProperty("me", out var me) ||
                    me.ValueKind != JsonValueKind.Object)
                    return "La respuesta del ranking no contiene datos válidos en 'me'.";

                string Field(string key)
                {
                    if (!me.TryGetProperty(key, out var value) ||
                        value.ValueKind == JsonValueKind.Null ||
                        value.ValueKind == JsonValueKind.Undefined ||
                        value.ValueKind == JsonValueKind.Object ||
                        value.ValueKind == JsonValueKind.Array)
                        return "No disponible";
                    string raw = value.ToString();
                    if (key == "rankName") return raw;
                    if (decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture,
                        out decimal number))
                        return number.ToString("#,0.################", Spanish);
                    return raw;
                }

                return "🏆 Rango — instancia " + instanceIndex +
                    "\nID: " + Field("rankId") +
                    "\nNombre: " + Field("rankName") +
                    "\nPuntos: " + Field("rankPoints") +
                    "\nExperiencia: " + Field("experience") +
                    "\nHonor: " + Field("honor");
            }
            catch (JsonException)
            {
                return "El servidor devolvió un JSON de ranking no válido.";
            }
        }
    }
}
