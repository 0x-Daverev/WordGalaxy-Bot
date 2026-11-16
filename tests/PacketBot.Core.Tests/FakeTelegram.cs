using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace PacketBot.Core.Tests;

// Fake Telegram Bot API: queued getUpdates responses, records every call.
// Shared with PacketBot.Server.Tests (linked file).
internal sealed class FakeTelegram : HttpMessageHandler
{
    private readonly string botToken;
    private readonly Queue<string> updates = new();

    public FakeTelegram(string botToken) => this.botToken = botToken;

    public List<(string Method, string Body)> Calls { get; } = new();
    public List<string> Sent { get; } = new();
    public TaskCompletionSource<string> FirstMessage { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void QueueUpdates(params object[] items)
    {
        lock (updates) updates.Enqueue(JsonSerializer.Serialize(new { ok = true, result = items }));
    }

    public static object Message(long updateId, long chatId, string type, string text) => new
    {
        update_id = updateId,
        message = new { chat = new { id = chatId, type }, text }
    };

    // Waits until a sendMessage containing the given text has been sent.
    public async Task<string> WaitForSentAsync(string contains, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            lock (Sent)
            {
                var match = Sent.FirstOrDefault(s => s.Contains(contains, StringComparison.Ordinal));
                if (match != null) return match;
            }
            await Task.Delay(20);
        }
        lock (Sent) throw new TimeoutException("No se envio '" + contains + "'. Enviados: " + string.Join(" || ", Sent));
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string path = request.RequestUri!.AbsolutePath;
        Assert.StartsWith("/bot" + botToken + "/", path);
        string method = path[(path.LastIndexOf('/') + 1)..];
        string body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (Calls) Calls.Add((method, body));
        string json;
        switch (method)
        {
            case "setMyCommands":
                json = "{\"ok\":true,\"result\":true}";
                break;
            case "getMyCommands":
                json = "{\"ok\":true,\"result\":[{\"command\":\"rank\",\"description\":\"x\"}]}";
                break;
            case "sendMessage":
                string text = WebUtility.UrlDecode(body);
                lock (Sent) Sent.Add(text);
                FirstMessage.TrySetResult(text);
                json = "{\"ok\":true,\"result\":{}}";
                break;
            case "getUpdates":
                bool has;
                string? next;
                lock (updates) has = updates.TryDequeue(out next);
                if (!has)
                {
                    await Task.Delay(50, cancellationToken);
                    next = "{\"ok\":true,\"result\":[]}";
                }
                json = next!;
                break;
            default:
                throw new InvalidOperationException("Metodo inesperado: " + method);
        }
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8) };
    }
}
