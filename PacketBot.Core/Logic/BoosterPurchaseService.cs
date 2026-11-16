using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace FunnyProject.Logic
{
    // HTTP purchase of one booster unit; an interface so auto-buy is testable.
    internal interface IBoosterPurchaseApi : IDisposable
    {
        Task<string> BuyOneAsync(int itemId, CancellationToken token);
    }

    internal sealed class BoosterPurchaseService : IBoosterPurchaseApi
    {
        private readonly HttpClient http;
        public string LastRequestDiagnostics { get; private set; } = "No se ha preparado ninguna petición.";
        public static readonly (int Id, string Code, int Category)[] Items =
        {
            (2051, "DMG_B01", 2),
            (2052, "DMG_B02", 2),
            (3124, "DMG_B03", 2),
            (2053, "HON_B01", 1),
            (2054, "HON_B02", 1),
            (3123, "HON50", 1),
            (2055, "EP_B01", 0),
            (2056, "EP_B02", 0),
            (3122, "EP50", 0)
        };

        public BoosterPurchaseService(string phpSessionId)
        {
            if (string.IsNullOrWhiteSpace(phpSessionId))
                throw new ArgumentException("PHPSESSID no disponible.");
            var cookies = new CookieContainer();
            cookies.Add(new Uri("https://wordgalaxy.space/"),
                new Cookie("PHPSESSID", phpSessionId));
            http = new HttpClient(new HttpClientHandler
            {
                CookieContainer = cookies,
                UseCookies = true,
                AllowAutoRedirect = false
            })
            {
                Timeout = TimeSpan.FromSeconds(20)
            };
        }

        // HTTP acceptance is not a verified in-game purchase. The subsequent
        // WG_BOOSTERS snapshot is the evidence for actual booster renewal.
        public async Task<string> BuyOneAsync(int itemId, CancellationToken token)
        {
            bool supported = Array.Exists(Items, item => item.Id == itemId);
            if (!supported) throw new ArgumentOutOfRangeException(nameof(itemId));
            // Equivalent to:
            // curl -X POST https://wordgalaxy.space/api
            //   -H 'Cookie: PHPSESSID=...' -F 'action=buy'
            //   -F 'amount=1' -F 'itemId=2052'
            // curl -F uses multipart/form-data, including its required boundary.
            using var body = new MultipartFormDataContent();
            // curl -F sends text fields without per-part Content-Type headers.
            body.Add(new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("buy")), "action");
            body.Add(new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("1")), "amount");
            body.Add(new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(itemId.ToString())), "itemId");
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://wordgalaxy.space/api")
            {
                Content = body
            };
            // Browser-like User-Agent for booster purchases only.
            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
            // PHPSESSID is sent via CookieContainer; Origin and Referer are not set.
            LastRequestDiagnostics =
                "POST /api HTTP/1.1\n" +
                "Host: wordgalaxy.space (HTTP transport)\n" +
                "Cookie: PHPSESSID=[OCULTO] (CookieContainer)\n" +
                "Content-Type: multipart/form-data; boundary=[generado automáticamente]\n" +
                "User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36\\n" +
                "Origin: [NO CONFIGURADO]\n" +
                "Referer: [NO CONFIGURADO]\n" +
                "\nCampos multipart (sin Content-Type por campo):\n" +
                "action=buy\n" +
                "amount=1\n" +
                "itemId=" + itemId;
            using var response = await http.SendAsync(request, token);
            string message = await response.Content.ReadAsStringAsync(token);
            if (message.Length > 100000)
                throw new InvalidOperationException("Respuesta de compra excesiva.");
            if (!response.IsSuccessStatusCode)
            {
                // Include a bounded, non-sensitive diagnostic instead of hiding
                // the server's reason behind a generic HTTP 403.
                string contentType = response.Content.Headers.ContentType?.MediaType ?? "";
                string details = message.Trim();
                if (contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
                {
                    // HTML errors often contain cookies, tokens or session data.
                    // Do not show the HTML body in the bot UI.
                    details = "Respuesta HTML (posible bloqueo del servidor o sesión no válida)";
                }
                else
                {
                    details = System.Text.RegularExpressions.Regex.Replace(
                        details, @"(?i)(PHPSESSID|csrf|token|sessionid)\s*[:=]\s*[^\s,;}""]+",
                        "$1=[oculto]");
                    if (details.Length > 220) details = details.Substring(0, 220) + "...";
                }
                throw new HttpRequestException("Compra rechazada: HTTP " +
                    (int)response.StatusCode + (details.Length > 0 ? " - " + details : ""));
            }

            // Do not treat a login redirect or a generic HTTP 200 error as success.
            if (response.Headers.Location != null ||
                message.Contains("login", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("\"status\":false", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("\"success\":false", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Respuesta de compra no confirmada; revisar sesion o saldo.");

            return "POST aceptado (pendiente de confirmar por boosters)";
        }

        public void Dispose() => http.Dispose();
    }
}
