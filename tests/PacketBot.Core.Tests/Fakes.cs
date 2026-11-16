using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PacketBot.Core.Tests;

// HTTP transport stand-in. Reads cookies from the container the production
// HttpClientHandler would use, so cookie handling is still exercised.
internal sealed class FakeWordGalaxyHttp : HttpMessageHandler
{
    private readonly CookieContainer cookies;
    private readonly Func<HttpRequestMessage, CookieContainer, HttpResponseMessage> respond;
    public List<(HttpMethod Method, string Path, string? PhpSessionId)> Requests { get; } = new();
    public List<string> PathsAndQueries { get; } = new();

    public FakeWordGalaxyHttp(CookieContainer cookies,
        Func<HttpRequestMessage, CookieContainer, HttpResponseMessage> respond)
    {
        this.cookies = cookies;
        this.respond = respond;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        string? cookie = cookies.GetCookies(request.RequestUri!)["PHPSESSID"]?.Value;
        lock (Requests)
        {
            Requests.Add((request.Method, request.RequestUri!.AbsolutePath, cookie));
            PathsAndQueries.Add(request.RequestUri.PathAndQuery);
        }
        var response = respond(request, cookies);
        response.RequestMessage ??= request;
        return Task.FromResult(response);
    }

    public static string MapHtml(int userId = 123456, string sessionId = "0123456789abcdef0123456789abcdef",
        string host = "127.0.0.1") =>
        $"<script>var cfg = {{\"userID\": \"{userId}\", \"sessionID\": \"{sessionId}\", \"localGS\": \"{host}\"}};</script>";

    public static HttpResponseMessage Html(string html, string? finalUrl = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html) };
        if (finalUrl != null)
            response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, finalUrl);
        return response;
    }
}

// Minimal game server: records every connection and the bytes it receives,
// and can push framed packets to the latest client.
internal sealed class FakeGameServer : IDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly BlockingCollection<Connection> accepted = new();
    private readonly List<Connection> all = new();
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public int ConnectionCount;

    public sealed class Connection
    {
        public required TcpClient Client { get; init; }
        public MemoryStream Received { get; } = new();
        public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public byte[] Snapshot() { lock (Received) return Received.ToArray(); }
    }

    public FakeGameServer()
    {
        listener.Start();
        _ = AcceptLoop();
    }

    private async Task AcceptLoop()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stop.Token);
                Interlocked.Increment(ref ConnectionCount);
                var connection = new Connection { Client = client };
                lock (all) all.Add(connection);
                accepted.Add(connection);
                _ = ReadLoop(connection);
            }
        }
        catch (Exception) when (stop.IsCancellationRequested) { }
        catch (ObjectDisposedException) { }
    }

    private async Task ReadLoop(Connection connection)
    {
        var buffer = new byte[4096];
        try
        {
            var stream = connection.Client.GetStream();
            int read;
            while ((read = await stream.ReadAsync(buffer, stop.Token)) > 0)
                lock (connection.Received) connection.Received.Write(buffer, 0, read);
        }
        catch (Exception) { }
        connection.Closed.TrySetResult();
    }

    public Connection NextConnection(int timeoutMs = 5000) =>
        accepted.TryTake(out var connection, timeoutMs, stop.Token)
            ? connection
            : throw new TimeoutException("No TCP connection arrived.");

    public bool NoConnectionWithin(int timeoutMs) => !accepted.TryTake(out _, timeoutMs);

    // Frame: [u16 payload length][u16 packet id][body], big-endian.
    public static byte[] Frame(short packetId, byte[] body)
    {
        var payload = new byte[2 + body.Length];
        payload[0] = (byte)(packetId >> 8); payload[1] = (byte)packetId;
        body.CopyTo(payload, 2);
        var framed = new byte[2 + payload.Length];
        framed[0] = (byte)(payload.Length >> 8); framed[1] = (byte)payload.Length;
        payload.CopyTo(framed, 2);
        return framed;
    }

    public static byte[] Utf(string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        var body = new byte[2 + bytes.Length];
        body[0] = (byte)(bytes.Length >> 8); body[1] = (byte)bytes.Length;
        bytes.CopyTo(body, 2);
        return body;
    }

    public static void Send(Connection connection, byte[] frame) =>
        connection.Client.GetStream().Write(frame);

    public static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met.");
            await Task.Delay(20);
        }
    }

    public void Dispose()
    {
        stop.Cancel();
        listener.Stop();
        lock (all) foreach (var connection in all) connection.Client.Dispose();
    }
}
