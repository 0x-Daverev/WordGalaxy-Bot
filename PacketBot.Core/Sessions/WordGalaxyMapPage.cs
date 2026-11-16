using System;
using System.Net;
using System.Text.RegularExpressions;

namespace FunnyProject.Sessions
{
    // Game credentials embedded in https://wordgalaxy.space/map.
    // Shared by the Windows login, instance restart and the headless server.
    public sealed record WordGalaxyMapPage(int UserId, string SessionId, string GameHost, string? AccountName)
    {
        private static readonly Regex UserIdPattern =
            new(@"""userID""\s*:\s*""?(\d+)""?", RegexOptions.IgnoreCase);
        private static readonly Regex SessionIdPattern =
            new(@"""sessionID""\s*:\s*""([^""]+)""", RegexOptions.IgnoreCase);
        private static readonly Regex LocalGsPattern =
            new(@"[""']?localGS[""']?\s*[:=]\s*[""']([^""'\s<>]+)[""']", RegexOptions.IgnoreCase);
        private static readonly Regex AccountNamePattern =
            new(@"(?:[\x22\x27]?(?:username|userName|playerName|nickname)[\x22\x27]?)\s*[:=]\s*[\x22\x27]([^\x22\x27<>]+)[\x22\x27]",
                RegexOptions.IgnoreCase);
        private static readonly Regex HostPattern = new(@"\A[a-zA-Z0-9.-]+\z");

        public const int GamePort = 8080;

        // Throws InvalidOperationException with a user-facing Spanish message.
        // Messages never include secrets.
        public static WordGalaxyMapPage Parse(string html)
        {
            ArgumentNullException.ThrowIfNull(html);
            var userMatch = UserIdPattern.Match(html);
            var sessionMatch = SessionIdPattern.Match(html);
            if (!userMatch.Success || !sessionMatch.Success ||
                !int.TryParse(userMatch.Groups[1].Value, out int userId) || userId <= 0)
                throw new InvalidOperationException(
                    "No se encontraron userID y sessionID validos en /map. Comprueba que PHPSESSID sigue activo.");

            var gsMatch = LocalGsPattern.Match(html);
            if (!gsMatch.Success)
                throw new InvalidOperationException("No se encontro localGS en /map.");

            string? accountName = null;
            var nameMatch = AccountNamePattern.Match(html);
            if (nameMatch.Success)
                accountName = WebUtility.HtmlDecode(nameMatch.Groups[1].Value);

            return new WordGalaxyMapPage(userId, sessionMatch.Groups[1].Value,
                NormalizeGameHost(gsMatch.Groups[1].Value), accountName);
        }

        public static string NormalizeGameHost(string localGs)
        {
            string gameHost = localGs.Trim();
            if (Uri.TryCreate(gameHost, UriKind.Absolute, out var gameUri))
                gameHost = gameUri.Host;
            else if (gameHost.Contains(':'))
                throw new InvalidOperationException("Formato localGS inesperado; se esperaba un host sin puerto.");

            if (gameHost.Length == 0 || gameHost.Length > 253 ||
                !HostPattern.IsMatch(gameHost) ||
                gameHost.StartsWith('-') || gameHost.EndsWith('-') ||
                gameHost.StartsWith('.') || gameHost.EndsWith('.'))
                throw new InvalidOperationException("El host de localGS no tiene un formato valido.");
            return gameHost;
        }

        // Safe for logs: never prints more than the first 4 characters.
        public static string MaskSecret(string? secret) =>
            string.IsNullOrEmpty(secret) || secret.Length < 4
                ? "****"
                : secret.Substring(0, 4) + "****";

        public override string ToString() =>
            $"WordGalaxyMapPage {{ UserId = {UserId}, SessionId = {MaskSecret(SessionId)}, GameHost = {GameHost} }}";
    }
}
