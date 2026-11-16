using System;
using FunnyProject.Commands;
using FunnyProject.PacketHandlers;

namespace FunnyProject.Sessions
{
    public enum SessionState
    {
        Created,
        Connected,
        Disconnected,
        Closed
    }

    // One logged-in WordGalaxy account: Api, User, PacketManager and its TCP
    // connection. Shared by the Windows UI and the headless server.
    //
    // Create() builds the objects without touching the network, so a host can
    // subscribe to PacketManager events (e.g. UserInitialize) before Connect().
    public sealed class WordGalaxySession : IDisposable
    {
        public const string ServerName = "wordgalaxy";
        public const int LoginInstanceId = 563;

        private readonly object sync = new();
        private bool connectRequested;
        private bool closed;

        public Api Api { get; }
        public PacketManager PacketManager { get; }
        public int UserId { get; }
        public string GameHost { get; }
        public int GamePort { get; }
        public string? AccountName { get; }
        // Kept for /restart and /phpsessid. Never write it to logs.
        public string PhpSessionId { get; }

        public event EventHandler? Closed;

        private WordGalaxySession(WordGalaxyMapPage map, string phpSessionId, int gamePort)
        {
            UserId = map.UserId;
            GameHost = map.GameHost;
            GamePort = gamePort;
            AccountName = map.AccountName;
            PhpSessionId = phpSessionId;

            // Username = user ID and Server = "wordgalaxy" are also the key of the
            // saved BotSettings: changing them would lose existing preferences.
            Api = new Api(new User(map.UserId.ToString(), map.SessionId, ServerName));
            Api.User.UserData.ID = map.UserId;
            Api.User.UserData.SID = map.SessionId;
            Api.User.UserData.PHPSESSID = phpSessionId;
            PacketManager = new PacketManager(Api);
            Api.User.PacketManager = PacketManager;
        }

        public static WordGalaxySession Create(WordGalaxyMapPage map, string phpSessionId,
            int gamePort = WordGalaxyMapPage.GamePort)
        {
            ArgumentNullException.ThrowIfNull(map);
            if (!WordGalaxyWebClient.IsValidPhpSessionId(phpSessionId))
                throw new ArgumentException("PHPSESSID no valido.", nameof(phpSessionId));
            return new WordGalaxySession(map, phpSessionId, gamePort);
        }

        public bool IsClosed { get { lock (sync) return closed; } }

        public SessionState State
        {
            get
            {
                lock (sync)
                {
                    if (closed) return SessionState.Closed;
                    if (!connectRequested) return SessionState.Created;
                    return PacketManager.Connected ? SessionState.Connected : SessionState.Disconnected;
                }
            }
        }

        // Opens TCP, registers the game-state handlers and sends Login 10996.
        // Handlers stay attached to this PacketManager across TCP restarts.
        // Blocking (TcpClient.Connect); call it from a background task in UI code.
        public void Connect()
        {
            lock (sync)
            {
                if (closed) throw new ObjectDisposedException(nameof(WordGalaxySession));
                if (connectRequested) throw new InvalidOperationException("La sesion ya se ha conectado.");
                connectRequested = true;
            }
            PacketManager.Res.RegisterEvents();
            PacketManager.Hero.RegisterEvents();
            PacketManager.ObjHandler.RegisterEvents();
            PacketManager.PlHandler.RegisterEvents();
            PacketManager.Connect(GameHost, GamePort);
            Console.WriteLine("[WordGalaxy] Conexion TCP establecida.");
            PacketManager.Send(new Login(userId: UserId, SessionID: Api.User.UserData.SID,
                instanceID: LoginInstanceId));
            Console.WriteLine("[WordGalaxy] Login 10996 enviado, esperando confirmacion del servidor.");
        }

        // Ordered shutdown: no more automatic reconnects, close TCP, stop the
        // instance's background loop. Idempotent.
        public void Dispose()
        {
            lock (sync)
            {
                if (closed) return;
                closed = true;
            }
            PacketManager.AutoReconnectEnabled = false;
            try { PacketManager.Disconnect(); }
            catch (Exception ex) { Console.WriteLine("[WordGalaxy] Error al cerrar TCP: " + ex.GetType().Name); }
            Api.Dispose();
            Closed?.Invoke(this, EventArgs.Empty);
        }

        public override string ToString() =>
            $"WordGalaxySession {{ UserId = {UserId}, GameHost = {GameHost}, State = {State} }}";
    }
}
