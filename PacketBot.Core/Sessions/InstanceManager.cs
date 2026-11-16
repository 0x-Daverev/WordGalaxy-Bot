using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FunnyProject.Sessions
{
    // Headless registry of WordGalaxy sessions, one per account.
    //
    // Identity: the WordGalaxy user ID read from /map, stable across restarts.
    // Telegram's "/command N" uses Number, the 1-based position in opening
    // order; a restart keeps the position, closing an instance shifts the ones
    // after it (same as closing a tab in the Windows UI).
    public sealed class InstanceManager : IDisposable
    {
        private readonly WordGalaxyWebClient web;
        private readonly int gamePort;
        private readonly SemaphoreSlim gate = new(1, 1);
        private readonly List<WordGalaxySession> sessions = new();
        private bool disposed;

        public event EventHandler? InstancesChanged;

        public InstanceManager(WordGalaxyWebClient? web = null, int gamePort = WordGalaxyMapPage.GamePort)
        {
            this.web = web ?? new WordGalaxyWebClient();
            this.gamePort = gamePort;
        }

        public IReadOnlyList<WordGalaxySession> Instances
        {
            get { lock (sessions) return sessions.ToArray(); }
        }

        public WordGalaxySession? GetByNumber(int number)
        {
            lock (sessions)
                return number >= 1 && number <= sessions.Count ? sessions[number - 1] : null;
        }

        public WordGalaxySession? GetByUserId(int userId)
        {
            lock (sessions) return sessions.FirstOrDefault(s => s.UserId == userId);
        }

        public int NumberOf(WordGalaxySession session)
        {
            lock (sessions) return sessions.IndexOf(session) + 1;
        }

        // Validates the PHPSESSID against /map, then connects TCP and logs in.
        public Task<WordGalaxySession> OpenAsync(string phpSessionId,
            CancellationToken cancellationToken = default) =>
            OpenCoreAsync(async () => (phpSessionId.Trim(), await web.FetchMapAsync(phpSessionId, cancellationToken)),
                cancellationToken);

        // Windows "Usuario y contraseña": logs in on the website first.
        public Task<WordGalaxySession> OpenWithCredentialsAsync(string username, string password,
            CancellationToken cancellationToken = default) =>
            OpenCoreAsync(() => web.LoginAsync(username, password, cancellationToken), cancellationToken);

        private async Task<WordGalaxySession> OpenCoreAsync(
            Func<Task<(string PhpSessionId, WordGalaxyMapPage Map)>> login, CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                ThrowIfDisposed();
                var (phpSessionId, map) = await login();
                if (GetByUserId(map.UserId) != null)
                    throw new InvalidOperationException(
                        "La cuenta " + map.UserId + " ya tiene una instancia abierta. Usa /restart.");
                var session = await ConnectNewAsync(map, phpSessionId.Trim(), cancellationToken);
                lock (sessions) sessions.Add(session);
                InstancesChanged?.Invoke(this, EventArgs.Empty);
                return session;
            }
            finally
            {
                gate.Release();
            }
        }

        // The old session is only closed after /map has been validated with the
        // stored PHPSESSID.
        public async Task<WordGalaxySession> RestartAsync(int userId,
            CancellationToken cancellationToken = default)
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                ThrowIfDisposed();
                var old = GetByUserId(userId)
                    ?? throw new InvalidOperationException("No existe la instancia " + userId + ".");
                var map = await web.FetchMapAsync(old.PhpSessionId, cancellationToken);
                if (map.UserId != old.UserId)
                    throw new InvalidOperationException("El PHPSESSID pertenece ahora a otra cuenta.");
                old.Dispose();
                WordGalaxySession replacement;
                try
                {
                    replacement = await ConnectNewAsync(map, old.PhpSessionId, cancellationToken);
                }
                catch
                {
                    lock (sessions) sessions.Remove(old);
                    InstancesChanged?.Invoke(this, EventArgs.Empty);
                    throw;
                }
                lock (sessions) sessions[sessions.IndexOf(old)] = replacement;
                InstancesChanged?.Invoke(this, EventArgs.Empty);
                return replacement;
            }
            finally
            {
                gate.Release();
            }
        }

        public bool Close(int userId)
        {
            WordGalaxySession? session;
            lock (sessions)
            {
                session = sessions.FirstOrDefault(s => s.UserId == userId);
                if (session == null) return false;
                sessions.Remove(session);
            }
            session.Dispose();
            InstancesChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }

        private async Task<WordGalaxySession> ConnectNewAsync(WordGalaxyMapPage map, string phpSessionId,
            CancellationToken cancellationToken)
        {
            var session = WordGalaxySession.Create(map, phpSessionId, gamePort);
            try
            {
                await Task.Run(session.Connect, cancellationToken);
                return session;
            }
            catch
            {
                session.Dispose();
                throw;
            }
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(InstanceManager));
        }

        // Closes every session (TCP, reconnect loops, background tasks).
        public void Dispose()
        {
            WordGalaxySession[] all;
            lock (sessions)
            {
                if (disposed) return;
                disposed = true;
                all = sessions.ToArray();
                sessions.Clear();
            }
            foreach (var session in all) session.Dispose();
        }
    }
}
