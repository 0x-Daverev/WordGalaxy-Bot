namespace PacketBot.Server;

// Command line: PacketBot.Server [--phpsessid <valor>] [--check] [--timeout <segundos>] [--start collect|npc|gg]
// Secrets can come from environment variables, which keeps them out of the
// process list (use a systemd EnvironmentFile with mode 600 on a VPS):
//   PACKETBOT_PHPSESSID         one or more PHPSESSID, separated by commas
//   PACKETBOT_TELEGRAM_TOKEN    BotFather token (optional)
//   PACKETBOT_TELEGRAM_CHAT_ID  authorised private chat (required with the token)
// --phpsessid takes precedence over PACKETBOT_PHPSESSID.
public sealed record ServerOptions(
    IReadOnlyList<string> PhpSessionIds,
    bool CheckOnly,
    TimeSpan HeroTimeout,
    string? StartModule = null,
    string? TelegramToken = null,
    long TelegramChatId = 0,
    TimeSpan FailureExitDelay = default,
    string? WebPassword = null,
    int WebPort = ServerOptions.DefaultWebPort)
{
    // Web panel (optional): enabled when a password is set. Plain HTTP; put
    // it behind an HTTPS reverse proxy to publish it.
    public const string WebPasswordVariable = "PACKETBOT_WEB_PASSWORD";
    public const string WebPortVariable = "PACKETBOT_WEB_PORT";
    public const int DefaultWebPort = 8080;
    public const int MinimumWebPasswordLength = 8;
    public bool WebEnabled => !string.IsNullOrEmpty(WebPassword);

    // Seconds to wait before exiting when no account could be opened.
    // Container platforms restart at once and forever; the wait avoids a
    // restart loop that would repeat the Telegram alert every few seconds.
    public const string FailureExitDelayVariable = "PACKETBOT_FAILURE_EXIT_DELAY";
    // Same as --start, for containers configured only with variables.
    public const string StartVariable = "PACKETBOT_START";

    public const string PhpSessionIdVariable = "PACKETBOT_PHPSESSID";
    public const string TelegramTokenVariable = "PACKETBOT_TELEGRAM_TOKEN";
    public const string TelegramChatVariable = "PACKETBOT_TELEGRAM_CHAT_ID";

    public string PhpSessionId => PhpSessionIds[0];
    public bool TelegramEnabled => !string.IsNullOrWhiteSpace(TelegramToken) && TelegramChatId != 0;

    public const string Usage =
        "Uso: PacketBot.Server [--phpsessid <valor>] [--check] [--timeout <segundos>] [--start collect|npc|gg]\n" +
        "  --phpsessid  PHPSESSID de wordgalaxy.space; varios separados por comas (o variable " + PhpSessionIdVariable + ").\n" +
        "  --check      Inicia sesion, espera la inicializacion del heroe y sale (codigo 0 = OK).\n" +
        "  --timeout    Segundos de espera para la inicializacion del heroe (por defecto 30).\n" +
        "  --start      Inicia un modulo en cada cuenta al arrancar: collect, npc (NPC Killer) o gg (Galaxy Gates).\n" +
        "Telegram (opcional): variables " + TelegramTokenVariable + " y " + TelegramChatVariable + ".";

    // Returns null and an error message when the arguments are invalid.
    public static (ServerOptions? Options, string? Error) Parse(string[] args, Func<string, string?> environment)
    {
        string? phpSessionIds = null;
        bool check = false;
        int timeoutSeconds = 30;
        string? start = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--phpsessid":
                    if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]))
                        return (null, "Falta el valor del argumento --phpsessid.");
                    phpSessionIds = args[++i];
                    break;
                case "--check":
                    check = true;
                    break;
                case "--timeout":
                    if (i + 1 >= args.Length || !int.TryParse(args[++i], out timeoutSeconds) ||
                        timeoutSeconds < 1 || timeoutSeconds > 600)
                        return (null, "--timeout necesita un numero de segundos entre 1 y 600.");
                    break;
                case "--start":
                    if (i + 1 >= args.Length || args[i + 1].ToLowerInvariant() is not ("collect" or "npc" or "gg"))
                        return (null, "--start admite: collect, npc o gg.");
                    start = args[++i].ToLowerInvariant();
                    break;
                case "--help":
                case "-h":
                    return (null, Usage);
                default:
                    return (null, "Argumento desconocido: " + args[i] + "\n" + Usage);
            }
        }
        phpSessionIds ??= environment(PhpSessionIdVariable);
        if (start == null && !check && environment(StartVariable)?.Trim().ToLowerInvariant() is { Length: > 0 } envStart)
        {
            if (envStart is not ("collect" or "npc" or "gg"))
                return (null, "La variable " + StartVariable + " admite: collect, npc o gg.");
            start = envStart;
        }
        var ids = (phpSessionIds ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0)
            return (null, "Indica el PHPSESSID con --phpsessid o la variable " + PhpSessionIdVariable + ".");
        if (check && start != null)
            return (null, "--check y --start no se pueden combinar.");

        string? token = environment(TelegramTokenVariable)?.Trim();
        string? chatText = environment(TelegramChatVariable)?.Trim();
        long chat = 0;
        if (!string.IsNullOrEmpty(token) || !string.IsNullOrEmpty(chatText))
        {
            if (string.IsNullOrEmpty(token))
                return (null, "Falta la variable " + TelegramTokenVariable + ".");
            if (!long.TryParse(chatText, out chat) || chat <= 0)
                return (null, "La variable " + TelegramChatVariable + " debe ser el Chat ID numerico del chat privado autorizado.");
        }
        int failureDelay = 0;
        string? delayText = environment(FailureExitDelayVariable)?.Trim();
        if (!string.IsNullOrEmpty(delayText) &&
            (!int.TryParse(delayText, out failureDelay) || failureDelay < 0 || failureDelay > 86_400))
            return (null, "La variable " + FailureExitDelayVariable + " debe ser un numero de segundos entre 0 y 86400.");
        string? webPassword = environment(WebPasswordVariable);
        if (string.IsNullOrEmpty(webPassword)) webPassword = null;
        else if (webPassword.Length < MinimumWebPasswordLength)
            return (null, "La variable " + WebPasswordVariable + " debe tener al menos " +
                MinimumWebPasswordLength + " caracteres.");
        int webPort = DefaultWebPort;
        string? portText = environment(WebPortVariable)?.Trim();
        if (!string.IsNullOrEmpty(portText) &&
            (!int.TryParse(portText, out webPort) || webPort < 1 || webPort > 65535))
            return (null, "La variable " + WebPortVariable + " debe ser un puerto entre 1 y 65535.");
        return (new ServerOptions(ids, check, TimeSpan.FromSeconds(timeoutSeconds), start,
            string.IsNullOrEmpty(token) ? null : token, chat, TimeSpan.FromSeconds(failureDelay),
            webPassword, webPort), null);
    }

    public override string ToString() =>
        $"ServerOptions {{ Cuentas = {PhpSessionIds.Count}, PhpSessionId = ****, CheckOnly = {CheckOnly}, " +
        $"HeroTimeout = {HeroTimeout}, StartModule = {StartModule}, Telegram = {(TelegramEnabled ? "activo, token ****" : "no")}, " +
        $"Web = {(WebEnabled ? "puerto " + WebPort + ", contrasena ****" : "no")} }}";
}
