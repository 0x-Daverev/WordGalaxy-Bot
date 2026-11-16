using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FunnyProject.Telegram
{
    // Telegram Bot API long polling. Only text messages from the authorised private
    // chat are dispatched. The token is part of the request URL, so it is
    // never logged: errors are reported by exception type only.
    public sealed class TelegramBotService : IDisposable
    {
        private readonly string token;
        private readonly long allowedChat;
        private readonly Func<string, Task<string>> dispatch;
        private readonly HttpClient http;
        private readonly Uri apiBase;
        private readonly TimeSpan retryDelay;
        private readonly CancellationTokenSource stop = new();
        private long offset;
        private Task polling = Task.CompletedTask;
        private int consecutiveFailures;
        private long lastSuccessTicks;

        // Health of the connection to Telegram (logged and shown in /status).
        public int ConsecutiveFailures => Volatile.Read(ref consecutiveFailures);
        public DateTime? LastSuccessUtc
        {
            get
            {
                long ticks = Interlocked.Read(ref lastSuccessTicks);
                return ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
            }
        }

        public TelegramBotService(string token, long allowedChat, Func<string, Task<string>> dispatch,
            HttpMessageHandler? handler = null, Uri? apiBase = null, TimeSpan? retryDelay = null)
        {
            if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("Token vacio.", nameof(token));
            if (allowedChat == 0) throw new ArgumentException("Chat ID invalido.", nameof(allowedChat));
            this.token = token.Trim();
            this.allowedChat = allowedChat;
            this.dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
            http = handler == null ? new HttpClient() : new HttpClient(handler);
            http.Timeout = TimeSpan.FromSeconds(40);
            this.apiBase = apiBase ?? new Uri("https://api.telegram.org/");
            this.retryDelay = retryDelay ?? TimeSpan.FromSeconds(5);
        }

        public long AllowedChat => allowedChat;

        // Completes when polling stops (after Dispose).
        public Task Polling => polling;

        public void Start()
        {
            _ = RegisterCommandsAsync();
            polling = PollAsync();
        }

        // Built as text: a token ("123456:ABC") at the start of a relative URI
        // would be parsed as a URI scheme.
        private Uri Method(string name) =>
            new(apiBase.AbsoluteUri.TrimEnd('/') + "/bot" + token + "/" + name);

        private async Task RegisterCommandsAsync()
        {
            try
            {
                var commands = TelegramCommandDispatcher.Menu
                    .Select(c => new { command = c.Command, description = c.Description }).ToArray();
                // A chat-specific scope takes priority over all-private-chats
                // and ensures the authorized chat sees the newest command menu.
                await RegisterCommandScopeAsync(commands, new { type = "all_private_chats" });
                await RegisterCommandScopeAsync(commands, new { type = "chat", chat_id = allowedChat });
                Console.WriteLine("[Telegram] Comandos registrados.");
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception ex)
            {
                Console.WriteLine("[Telegram] No se pudieron registrar los comandos (" +
                    ex.GetType().Name + ").");
            }
        }

        private async Task RegisterCommandScopeAsync<T>(T commands, object scope)
        {
            string serializedScope = JsonSerializer.Serialize(scope);
            using (var body = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("commands", JsonSerializer.Serialize(commands)),
                new KeyValuePair<string, string>("scope", serializedScope)
            }))
            using (var response = await http.PostAsync(Method("setMyCommands"), body, stop.Token))
            {
                response.EnsureSuccessStatusCode();
                using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                if (!result.RootElement.GetProperty("ok").GetBoolean() ||
                    !result.RootElement.GetProperty("result").GetBoolean())
                    throw new InvalidOperationException("Telegram rechazo setMyCommands.");
            }

            // Confirm /rank exists on the scope that was just registered.
            using var verifyBody = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("scope", serializedScope)
            });
            using var verification = await http.PostAsync(Method("getMyCommands"), verifyBody, stop.Token);
            verification.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await verification.Content.ReadAsStringAsync());
            if (!json.RootElement.GetProperty("ok").GetBoolean() ||
                !json.RootElement.GetProperty("result").EnumerateArray()
                    .Any(item => item.GetProperty("command").GetString() == "rank"))
                throw new InvalidOperationException("El menu de Telegram no incluye /rank.");
        }

        private async Task PollAsync()
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    var url = new Uri(Method("getUpdates") +
                        "?timeout=25&allowed_updates=%5B%22message%22%5D&offset=" + offset);
                    using var response = await http.GetAsync(url, stop.Token);
                    response.EnsureSuccessStatusCode();
                    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                    if (!json.RootElement.GetProperty("ok").GetBoolean()) continue;
                    MarkSuccess();
                    foreach (var update in json.RootElement.GetProperty("result").EnumerateArray())
                    {
                        long updateId = update.GetProperty("update_id").GetInt64();
                        offset = Math.Max(offset, updateId + 1);
                        if (!update.TryGetProperty("message", out var message) ||
                            !message.TryGetProperty("chat", out var chat) ||
                            !chat.TryGetProperty("id", out var chatId) ||
                            chatId.GetInt64() != allowedChat ||
                            !chat.TryGetProperty("type", out var chatType) ||
                            chatType.GetString() != "private" ||
                            !message.TryGetProperty("text", out var msgText))
                            continue;
                        string answer;
                        try { answer = await dispatch(msgText.GetString() ?? ""); }
                        catch (Exception ex) { answer = "Error: " + ex.Message; }
                        await SendAsync(answer);
                    }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    int failures = Interlocked.Increment(ref consecutiveFailures);
                    // Without network this would repeat every few seconds: log
                    // the first error and then every 10th; waits grow to 60 s.
                    if (failures == 1 || failures % 10 == 0)
                        Console.WriteLine("[Telegram] Error de comunicacion (" + ex.GetType().Name + "); " +
                            failures + " seguidos.");
                    try { await Task.Delay(RetryDelay(failures), stop.Token); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }

        internal TimeSpan RetryDelay(int failures) =>
            TimeSpan.FromTicks(Math.Min(TimeSpan.FromSeconds(60).Ticks,
                retryDelay.Ticks * (1L << Math.Min(failures - 1, 10))));

        private void MarkSuccess()
        {
            Interlocked.Exchange(ref lastSuccessTicks, DateTime.UtcNow.Ticks);
            int failures = Interlocked.Exchange(ref consecutiveFailures, 0);
            if (failures > 0)
                Console.WriteLine("[Telegram] Conexion recuperada tras " + failures + " errores.");
        }

        public async Task NotifyAsync(string message)
        {
            if (stop.IsCancellationRequested) return;
            try { await SendAsync(message); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception ex)
            {
                Console.WriteLine("[Telegram] No se pudo enviar la alerta (" +
                    ex.GetType().Name + ").");
            }
        }

        private async Task SendAsync(string text)
        {
            using var body = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("chat_id", allowedChat.ToString()),
                new KeyValuePair<string, string>("text", text)
            });
            using var response = await http.PostAsync(Method("sendMessage"), body, stop.Token);
            response.EnsureSuccessStatusCode();
        }

        public void Dispose()
        {
            stop.Cancel();
            // Do not dispose HttpClient while the polling request is in flight.
        }
    }
}
