using FunnyProject;
using FunnyProject.Services;

namespace PacketBot.Server;

// Settings of one account on the server: the same per-account file as on
// Windows. Read() is cheap (cached until the file changes on disk, so manual
// edits still apply); Update() is a serialized read-modify-write on a fresh
// copy, so concurrent writers (auto-buy, /npc) never lose each other's
// changes, e.g. the purchase timestamp that prevents a duplicate purchase.
internal sealed class ServerSettingsAccess : IBotSettingsAccess
{
    private readonly string? server;
    private readonly string? username;
    private readonly object sync = new();
    private BotSettings? cached;
    private DateTime cachedWriteUtc;

    public ServerSettingsAccess(string? server, string? username)
    {
        this.server = server;
        this.username = username;
    }

    public string Path => BotSettingsStore.PathFor(server, username);

    public BotSettings Read()
    {
        lock (sync)
        {
            var writeUtc = LastWriteUtc();
            if (cached == null || writeUtc != cachedWriteUtc)
            {
                cached = BotSettingsStore.Load(server, username);
                cachedWriteUtc = writeUtc;
            }
            return cached;
        }
    }

    public void Update(Action<BotSettings> change)
    {
        lock (sync)
        {
            // Fresh object: readers keep the previous snapshot untouched.
            var copy = BotSettingsStore.Load(server, username);
            change(copy);
            BotSettingsStore.Save(server, username, copy);
            cached = copy;
            cachedWriteUtc = LastWriteUtc();
        }
    }

    private DateTime LastWriteUtc()
    {
        try { return File.GetLastWriteTimeUtc(Path); }
        catch (IOException) { return DateTime.MinValue; }
    }
}
