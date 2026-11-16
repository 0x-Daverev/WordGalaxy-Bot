using System.Collections.Concurrent;

namespace PacketBot.Server.Web;

// Background pictures of the maps, from the same wordgalaxy.space URLs as the
// Windows minimap, served by the panel so the page only loads its own origin
// (strict CSP). Only an integer map ID reaches the URL; results are cached,
// misses for 10 minutes.
public sealed class MinimapImages : IDisposable
{
    private readonly HttpClient http;
    private readonly ConcurrentDictionary<int, (byte[]? Image, DateTime Until)> cache = new();

    public MinimapImages(HttpMessageHandler? handler = null)
    {
        http = handler == null ? new HttpClient() : new HttpClient(handler);
        http.Timeout = TimeSpan.FromSeconds(8);
    }

    public async Task<byte[]?> GetAsync(int mapId, CancellationToken token)
    {
        if (mapId <= 0 || mapId > 100_000) return null;
        if (cache.TryGetValue(mapId, out var hit) && (hit.Image != null || DateTime.UtcNow < hit.Until))
            return hit.Image;
        byte[]? image = null;
        foreach (string suffix in new[] { "-700", "-300", "" })
        {
            try
            {
                using var response = await http.GetAsync(
                    $"https://wordgalaxy.space/spacemap/graphics/minimaps/minimap-{mapId}{suffix}.jpg", token);
                if (!response.IsSuccessStatusCode) continue;
                if (response.Content.Headers.ContentType?.MediaType is { } type && !type.StartsWith("image/")) continue;
                image = await response.Content.ReadAsByteArrayAsync(token);
                if (image.Length > 0) break;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !token.IsCancellationRequested)
            {
                // Try the next variant; a missing picture leaves a black background.
            }
        }
        cache[mapId] = (image, DateTime.UtcNow.AddMinutes(10));
        return image;
    }

    public void Dispose() => http.Dispose();
}
