using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace FunnyProject.Sessions
{
    // HTTP side of a WordGalaxy login: obtains /map with a PHPSESSID, or logs
    // in with username/password first.
    // Error messages are user-facing and never contain the PHPSESSID.
    public sealed class WordGalaxyWebClient
    {
        public static readonly Uri SiteUri = new("https://wordgalaxy.space/");
        // wg_calendar=continue skips the calendar page shown before the map.
        public static readonly Uri MapUri = new("https://wordgalaxy.space/map?wg_calendar=continue");
        public static readonly Uri ApiUri = new("https://wordgalaxy.space/api/");
        private const string UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

        // Tests replace the transport; production uses a cookie-aware handler.
        private readonly Func<CookieContainer, HttpMessageHandler> handlerFactory;

        public WordGalaxyWebClient(Func<CookieContainer, HttpMessageHandler>? handlerFactory = null)
        {
            this.handlerFactory = handlerFactory ?? (cookies => new HttpClientHandler
            {
                CookieContainer = cookies,
                UseCookies = true,
                AllowAutoRedirect = true
            });
        }

        public static bool IsValidPhpSessionId(string? value) =>
            !string.IsNullOrWhiteSpace(value) &&
            !value.Any(ch => char.IsControl(ch) || ch == ';' || ch == ',');

        public async Task<WordGalaxyMapPage> FetchMapAsync(string phpSessionId,
            CancellationToken cancellationToken = default)
        {
            if (!IsValidPhpSessionId(phpSessionId))
                throw new InvalidOperationException("Introduce un PHPSESSID valido.");
            var cookies = new CookieContainer();
            cookies.Add(MapUri, new Cookie("PHPSESSID", phpSessionId.Trim()));
            using var http = CreateHttpClient(cookies);
            return await GetMapAsync(http, cancellationToken);
        }

        // Returns the PHPSESSID issued by the server together with /map, read
        // with the same cookies as the login.
        public async Task<(string PhpSessionId, WordGalaxyMapPage Map)> LoginAsync(
            string username, string password, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
                throw new InvalidOperationException("Introduce usuario y contraseña.");
            if (LoginIdentifierPairs.All.Count == 0)
                throw new InvalidOperationException("No hay pares de identificadores configurados.");

            var cookies = new CookieContainer();
            using var http = CreateHttpClient(cookies);
            var identifiers = LoginIdentifierPairs.All[Random.Shared.Next(LoginIdentifierPairs.All.Count)];
            using (var landing = await http.GetAsync(SiteUri, cancellationToken))
            {
                if (!landing.IsSuccessStatusCode)
                    throw new InvalidOperationException($"GET / HTTP {(int)landing.StatusCode}");
            }
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = username.Trim(),
                ["password"] = password,
                ["action"] = "login",
                [LoginIdentifierPairs.FirstParameter] = identifiers.First,
                [LoginIdentifierPairs.SecondParameter] = identifiers.Second
            });
            using var request = new HttpRequestMessage(HttpMethod.Post, ApiUri) { Content = content };
            request.Headers.Referrer = SiteUri;
            using (var loginResponse = await http.SendAsync(request, cancellationToken))
            {
                if (!loginResponse.IsSuccessStatusCode)
                    throw new InvalidOperationException(
                        $"POST /api/: HTTP {(int)loginResponse.StatusCode} {loginResponse.ReasonPhrase}. Comprueba la respuesta del servidor.");
            }
            var issued = cookies.GetCookies(MapUri)["PHPSESSID"];
            if (issued == null || string.IsNullOrEmpty(issued.Value))
                throw new InvalidOperationException(
                    "Login sin PHPSESSID. Comprueba las credenciales y la respuesta del servidor.");
            Console.WriteLine("[WordGalaxy] Autenticacion web completada; SID obtenido.");
            return (issued.Value, await GetMapAsync(http, cancellationToken));
        }

        private System.Net.Http.HttpClient CreateHttpClient(CookieContainer cookies)
        {
            var http = new System.Net.Http.HttpClient(handlerFactory(cookies))
            {
                Timeout = TimeSpan.FromSeconds(60)
            };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml;q=0.9,*/*;q=0.8");
            return http;
        }

        private static async Task<WordGalaxyMapPage> GetMapAsync(System.Net.Http.HttpClient http,
            CancellationToken cancellationToken)
        {
            using var response = await http.GetAsync(MapUri, cancellationToken);
            var finalUri = response.RequestMessage?.RequestUri;
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"GET /map: HTTP {(int)response.StatusCode} {response.ReasonPhrase}. Ruta final: {finalUri?.AbsolutePath ?? "(desconocida)"}. " +
                    "Comprueba que puedes abrir /map en el navegador con la misma sesion y si requiere otras cookies.");
            if (finalUri?.Host != MapUri.Host)
                throw new InvalidOperationException("La pagina /map ha redirigido a otro dominio.");
            if (finalUri.AbsolutePath != MapUri.AbsolutePath)
                throw new InvalidOperationException("PHPSESSID caducado o redirigido fuera de /map.");
            return WordGalaxyMapPage.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        }
    }
}
