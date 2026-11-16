using FunnyProject.Commands;
using Krypton_Core.Managers;
using Krypton_Core.Packets.Bytes;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace FunnyProject.PacketHandlers
{
    public abstract class Client
    {
        TcpClient? TcpClient { get; set; }
        NetworkStream? Stream { get; set; }
        private static readonly byte[] BoosterMarker = Encoding.ASCII.GetBytes("0|WG_BOOSTERS|v1|");
        private readonly object _sendLock = new();
        private readonly object _diagnosticLock = new();
        private readonly Queue<string> _recentSends = new();
        private const int RecentSendLimit = 10;
        public Api Api;
        public event EventHandler<EventArgs>? Disconnected;
        private volatile bool _disconnected;
        private short _lastPacketId;
        private int _lastPacketLength;
        private long _rxCount, _txCount;
        private DateTime _connectedAt, _lastRxAt, _lastTxAt;
        private string _remote = "unknown", _stage = "idle";
        private int _readBytes, _expectedBytes;
        private volatile bool _localClose;
        private string? _tcpHost;
        private int _tcpPort;
        private Task? _readerTask;
        private int _reconnectRunning;
        private volatile bool _stopReconnect;
        // Only unexpected disconnects are optional. HP=0 always uses RestartTcpAsync.
        private volatile bool _autoReconnectEnabled;
        public bool AutoReconnectEnabled
        {
            get => _autoReconnectEnabled;
            set => _autoReconnectEnabled = value;
        }
        private volatile bool _restartingTcp;
        private readonly System.Threading.SemaphoreSlim _reconnectLock = new(1, 1);
        private static string Explain(Exception ex)
        {
            var socket = ex as SocketException ?? ex.InnerException as SocketException;
            return ex.GetType().Name + ": " + ex.Message +
                (socket == null ? "" : $" SocketError={socket.SocketErrorCode} NativeError={socket.NativeErrorCode}");
        }
        private void Trace(string message) =>
            Console.WriteLine($"[TCP DIAG {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}");
        private void ReportClose(string reason)
        {
            if (!_localClose)
            {
                LastCloseReason = reason;
                LastCloseUtc = DateTime.UtcNow;
                Console.WriteLine("[TCP] Desconectado: " + reason);
            }
        }
        public bool Connected => !_disconnected && Stream != null && TcpClient != null;

        // Supervision (server /status and heartbeat log).
        public DateTime LastReceivedUtc => _lastRxAt;
        public DateTime ConnectedSinceUtc => _connectedAt;
        // Successful TCP connections of this session (1 = never reconnected).
        public int ConnectionCount => System.Threading.Volatile.Read(ref _connectionCount);
        public string? LastCloseReason { get; private set; }
        public DateTime? LastCloseUtc { get; private set; }
        private int _connectionCount;


        public Client(Api api)
        {

            Api = api;
        }
        public void Send(Command com, string? diagnosticLabel = null)
        {
            byte[] bytes = com.param1.Message.ToArray();
            // Frame: 2-byte length followed by the 2-byte packet ID.
            int id = bytes.Length >= 4 ? (short)((bytes[2] << 8) | bytes[3]) : -1;
            string kind = diagnosticLabel ?? com.GetType().Name;
            Send(bytes, id, kind);
        }
        private void Send(byte[] bytes, int id, string kind)
        {
            try
            {
                lock (_sendLock)
                {
                    if (_disconnected || Stream == null || !Stream.CanWrite || !Connected)
                    {
                        RecordSend(id, kind, bytes.Length, "omitido-desconectado");
                        return;
                    }
                    try
                    {
                        Stream.Write(bytes, 0, bytes.Length);
                        System.Threading.Interlocked.Increment(ref _txCount);
                        _lastTxAt = DateTime.UtcNow;
                        RecordSend(id, kind, bytes.Length, "OK");
                    }
                    catch (Exception ex)
                    {
                        RecordSend(id, kind, bytes.Length, "ERROR " + Explain(ex));
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                if (ex is IOException || ex is SocketException || ex is ObjectDisposedException)
                    MarkDisconnected("Error TX: " + Explain(ex));
                else Trace("Error TX inesperado: " + Explain(ex));
            }
        }
        private void RecordSend(int id, string kind, int length, string result)
        {
            // Diagnostic packet history intentionally disabled.
        }
        private void MarkDisconnected(string reason)
        {
            if (_disconnected) return;
            _disconnected = true;
            ReportClose(reason);
            Disconnected?.Invoke(this, EventArgs.Empty);
            if (AutoReconnectEnabled && !_localClose && !_stopReconnect && !_restartingTcp &&
                !string.IsNullOrWhiteSpace(_tcpHost) && _tcpPort > 0)
                _ = Task.Run(AutoReconnectAsync);
        }
        public void Connect(string ip, int port)
        {
            _disconnected = false;
            _localClose = false;
            Api.User.Boosters.Clear();
            Api.User.BoosterRequestsReady = false;
            _rxCount = 0; _txCount = 0;
            lock (_diagnosticLock)
            {
                _recentSends.Clear();
            }
            _lastRxAt = default; _lastTxAt = default; _connectedAt = default;
            _lastPacketId = 0; _lastPacketLength = 0;
            _tcpHost = ip; _tcpPort = port;
            _remote = ip + ":" + port;
            _stage = "connect"; _readBytes = 0; _expectedBytes = 0;

            TcpClient = new TcpClient();
            try { TcpClient.Connect(ip, port); }
            catch (Exception ex)
            {
                MarkDisconnected("Fallo al conectar: " + Explain(ex));
                TcpClient.Close();
                throw;
            }
            if (TcpClient.Connected)
            {
                Stream = TcpClient.GetStream();
                _connectedAt = DateTime.UtcNow;
                System.Threading.Interlocked.Increment(ref _connectionCount);
                Console.WriteLine("[TCP] Conectado.");

                _readerTask = Task.Run(ListenForPackets);
                
                return;
            }
        }

        public void Disconnect()
        {
            lock (_sendLock)
            {
                _stopReconnect = true;
                _localClose = true;
                MarkDisconnected("Cierre solicitado por el cliente");
                try { Stream?.Close(); } catch (IOException) { }
                try { TcpClient?.Close(); } catch (SocketException) { }
            }
        }

        // Explicit restart (e.g. death) does not disable automatic recovery.
        public async Task RestartTcpAsync()
        {
            await _reconnectLock.WaitAsync();
            try
            {
                _restartingTcp = true;
                _stopReconnect = false;
                await ReconnectOnceAsync();
            }
            finally
            {
                _restartingTcp = false;
                _reconnectLock.Release();
            }
        }

        private async Task ReconnectOnceAsync(bool automatic = false)
        {
            if (automatic && !AutoReconnectEnabled) return;
            if (string.IsNullOrWhiteSpace(_tcpHost) || _tcpPort <= 0)
                throw new InvalidOperationException("No hay endpoint TCP anterior.");
            int userId = Api.User.UserData.ID;
            string sid = Api.User.UserData.SID;
            if (userId <= 0 || string.IsNullOrWhiteSpace(sid))
                throw new InvalidOperationException("Faltan datos para repetir login TCP.");

            // Close the old socket without generating a deliberate disconnect event.
            _disconnected = true;
            try { Stream?.Close(); } catch (Exception ex) { Trace("Cierre stream anterior: " + Explain(ex)); }
            try { TcpClient?.Close(); } catch (Exception ex) { Trace("Cierre socket anterior: " + Explain(ex)); }
            if (_readerTask != null) await _readerTask;
            if (_stopReconnect || (automatic && !AutoReconnectEnabled)) return;
            Connect(_tcpHost, _tcpPort);
            if (_stopReconnect || (automatic && !AutoReconnectEnabled))
            {
                Disconnect();
                return;
            }
            Send(new Login(userId: userId, SessionID: sid, instanceID: 563));

        }

        private async Task AutoReconnectAsync()
        {
            if (System.Threading.Interlocked.CompareExchange(ref _reconnectRunning, 1, 0) != 0)
                return;
            try
            {
                int attempt = 0;
                while (AutoReconnectEnabled && !_stopReconnect && _disconnected)
                {
                    attempt++;
                    int delay = Math.Min(30, 3 * (1 << Math.Min(attempt - 1, 4)));
                    Trace("Reconectando TCP en " + delay + "s; intento " + attempt);
                    await Task.Delay(TimeSpan.FromSeconds(delay));
                    if (!AutoReconnectEnabled || _stopReconnect || !_disconnected) break;
                    await _reconnectLock.WaitAsync();
                    try
                    {
                        if (_stopReconnect || !AutoReconnectEnabled || !_disconnected) break;
                        _restartingTcp = true;
                        await ReconnectOnceAsync(automatic: true);
                    }
                    catch (Exception ex)
                    {
                        Trace("Reconexión TCP fallida: " + Explain(ex));
                    }
                    finally
                    {
                        _restartingTcp = false;
                        _reconnectLock.Release();
                    }
                }
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _reconnectRunning, 0);
                // Handle a disconnect during the final loop transition.
                if (AutoReconnectEnabled && !_stopReconnect && _disconnected && !_restartingTcp)
                    _ = Task.Run(AutoReconnectAsync);
            }
        }

        private async Task ListenForPackets()
        {
            try
            {
                while (Connected)
                {
                    // ReadExactly handles TCP fragmentation; length counts payload bytes.
                    byte[] header = new byte[2];
                    if (!ReadExactly(header, "cabecera")) break;
                    int length = (header[0] << 8) | header[1];
                    if (length < 2 || length > 65535)
                        throw new InvalidDataException("Invalid packet size " + length);
                    byte[] payload = new byte[length];
                    if (!ReadExactly(payload, "cuerpo")) break;
                    _lastRxAt = DateTime.UtcNow;
                    _lastPacketLength = length;
                    System.Threading.Interlocked.Increment(ref _rxCount);
                    _lastPacketId = (short)((payload[0] << 8) | payload[1]);
                    // Capture the WG_BOOSTERS response even if this server uses
                    // a different TCP wrapper than the expected text packet 4224.
                    if (_lastPacketId != 4224 && payload.Length >= BoosterMarker.Length)
                    {
                        int offset = payload.AsSpan().IndexOf(BoosterMarker);
                        if (offset >= 0)
                        {
                            string boosterMessage = Encoding.UTF8
                                .GetString(payload, offset, payload.Length - offset)
                                .TrimEnd('\0');
                            Api.User.Boosters.TryReceive(boosterMessage, _lastPacketId,
                                Api.User.UserData.ID);
                        }
                    }
                    if (_lastPacketId == 12754)
                    {
                        // Server-reported equipped formation, independent of any
                        // formation change command we have sent.
                        string[] formations = DroneFormationDiagnostics.ExtractMenuFormationIds(payload);
                        if (formations.Length == 1)
                            Api.User.UpdateCurrentDroneFormation(formations[0]);
                    }
                    if (_lastPacketId == 30106)
                        Api.User.UpdateAvailableDroneFormations(
                            DroneFormationDiagnostics.ExtractMenuFormationIds(payload));
                    else
                    {
                        // The menu's list of owned formations is not active state.
                        // Observe potential formation IDs in other inbound packets
                        // for protocol diagnosis; they are not confirmed equipment.
                        var mentions = DroneFormationDiagnostics.ExtractMenuFormationIds(payload);
                        if (mentions.Length == 1)
                            Api.User.ObserveFormationPacket(mentions[0], _lastPacketId);
                    }
                    if (_lastPacketId == 22037 && payload.Length >= 50)
                    {
                        // o10 (22037): h1A is the PET entity ID. On the wire
                        // it is at bytes 10..13 after the packet ID and ROL19.
                        uint petEncoded = ((uint)payload[10] << 24) |
                            ((uint)payload[11] << 16) |
                            ((uint)payload[12] << 8) | payload[13];
                        int petId = unchecked((int)((petEncoded << 19) | (petEncoded >> 13)));
                        Api.User.PetMap.Spawn(petId);
                    }
                    if (_lastPacketId == 22037 && payload.Length >= 50)
                    {
                        // Verified against two captures: 42,992 -> 42,987.
                        // fP starts 16 bytes before the end of the framed data,
                        // with the 2-byte frame header removed here.
                        int offset = payload.Length - 16;
                        uint encoded = ((uint)payload[offset] << 24) |
                            ((uint)payload[offset + 1] << 16) |
                            ((uint)payload[offset + 2] << 8) |
                            payload[offset + 3];
                        int fuel = (int)((encoded >> 21) | (encoded << 11));
                        if (fuel >= 0 && fuel <= 500000)
                            Api.User.PetFuel?.OnFuelUpdate(fuel);
                    }

                    try
                    {
                        Parse(new EndianBinaryReader(EndianBitConverter.Big, new MemoryStream(payload)));
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("[TCP] Error al interpretar paquete " + _lastPacketId + ": " + ex.Message);

                    }
                }
                MarkDisconnected(_localClose ? "Bucle finalizado tras cierre local" : "Bucle de lectura finalizado");
            }
            catch (Exception ex)
            {
                MarkDisconnected((_localClose ? "Lectura interrumpida por cierre local: " : "Error de lectura: ") + Explain(ex));
            }
            await Task.CompletedTask;
        }

        private bool ReadExactly(byte[] bytes, string stage)
        {
            int offset = 0;
            _stage = stage; _readBytes = 0; _expectedBytes = bytes.Length;
            while (offset < bytes.Length)
            {
                if (_disconnected || Stream == null) return false;
                int count = Stream.Read(bytes, offset, bytes.Length - offset);
                if (count == 0)
                {
                    MarkDisconnected($"EOF remoto en {stage}: {offset}/{bytes.Length} bytes; el servidor o un intermediario cerro el flujo (TCP no especifica la causa)");
                    return false;
                }
                offset += count;
                _readBytes = offset;
            }
            _stage = "esperando paquete";
            return true;
        }

        protected bool IsConnected()
        {
            if (_disconnected || TcpClient == null || TcpClient.Client == null || Stream == null || !Stream.CanWrite || !Stream.CanRead)
                return false;
            try
            {
                return !(TcpClient.Client.Poll(1, SelectMode.SelectRead) && TcpClient.Client.Available == 0);
            }
            catch (SocketException) { return false; }
        }

        public abstract void Parse(EndianBinaryReader read);
    }
    

}
