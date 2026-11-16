using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FunnyProject.Logic
{
    public sealed class GalaxyGateInfo
    {
        public int id { get; set; }
        public string name { get; set; } = "";
        public int partsOwned { get; set; }
        public int maxParts { get; set; }
        public int lives { get; set; }
        public bool prepared { get; set; }
        public int wave { get; set; }
        public GalaxyLifeQuote? lifeQuote { get; set; }
    }

    public sealed class GalaxyLifeQuote
    {
        public bool available { get; set; }
        public long? price { get; set; }
        public int purchased { get; set; }
    }

    public sealed class GalaxyGateResponse
    {
        public bool status { get; set; }
        public string lifeCsrf { get; set; } = "";
        public string lifeRequestId { get; set; } = "";
        public long uridium { get; set; }
        public bool premium { get; set; }
        public int spinCost { get; set; }
        public List<GalaxyGateInfo> gates { get; set; } = new();
    }

    // HTTP API of Galaxy Gates (files/api_gg.php). An interface so the GG
    // module can be tested without the real server.
    public interface IGalaxyGateApi
    {
        Task<GalaxyGateResponse> ReadAsync(CancellationToken token = default);
        Task PrepareAsync(int gateId, CancellationToken token = default);
        Task SpinAsync(int gateId, int spins, string requestId, string csrf, CancellationToken token = default);
    }

    public sealed class GalaxyGateService : IGalaxyGateApi
    {
        private readonly HttpClient http;
        public GalaxyGateService(string phpSessionId)
        {
            if (string.IsNullOrWhiteSpace(phpSessionId))
                throw new ArgumentException("PHPSESSID no disponible.");
            var jar = new CookieContainer();
            jar.Add(new Uri("https://wordgalaxy.space/"),
                new Cookie("PHPSESSID", phpSessionId));
            http = new HttpClient(new HttpClientHandler
            {
                CookieContainer = jar,
                UseCookies = true,
                AllowAutoRedirect = false
            }) { Timeout = TimeSpan.FromSeconds(20) };
        }

        private async Task PostAsync(object payload, CancellationToken token)
        {
            using var body = new StringContent(JsonSerializer.Serialize(payload),
                System.Text.Encoding.UTF8, "application/json");
            using var response = await http.PostAsync(
                "https://wordgalaxy.space/files/api_gg.php", body, token);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException("POST api_gg.php: HTTP " + (int)response.StatusCode);
            string json = await response.Content.ReadAsStringAsync(token);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("status", out var status) ||
                status.ValueKind != JsonValueKind.True)
                throw new InvalidOperationException("Operacion GG rechazada por el servidor.");
        }

        public Task SpinAsync(int gateId, int spins, string requestId, string csrf,
            CancellationToken token = default)
        {
            if (gateId <= 0 || !new[] { 1, 5, 10, 100 }.Contains(spins) ||
                string.IsNullOrWhiteSpace(requestId) || string.IsNullOrWhiteSpace(csrf))
                throw new ArgumentException("Parametros de spin invalidos.");
            return PostAsync(new { action = "spin", gateId, spins, requestId, csrf }, token);
        }

        public Task PrepareAsync(int gateId, CancellationToken token = default)
        {
            if (gateId <= 0) throw new ArgumentException("GG invalida.");
            return PostAsync(new { gateId, action = "prepare" }, token);
        }

        public async Task<GalaxyGateResponse> ReadAsync(CancellationToken token = default)
        {
            using var response = await http.GetAsync(
                "https://wordgalaxy.space/files/api_gg.php", token);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException("GET api_gg.php: HTTP " + (int)response.StatusCode);
            var json = await response.Content.ReadAsStringAsync(token);
            var result = JsonSerializer.Deserialize<GalaxyGateResponse>(json);
            if (result == null || !result.status || result.gates == null)
                throw new InvalidOperationException("Respuesta de Galaxy Gates no valida.");
            return result;
        }
    }
}
