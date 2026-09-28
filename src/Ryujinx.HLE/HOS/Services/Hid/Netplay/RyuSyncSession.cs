using Ryujinx.Common.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace Ryujinx.HLE.HOS.Services.Hid.Netplay
{
    public enum RyuSyncRole
    {
        None,
        Host,
        Guest,
    }

    public sealed class RyuSyncSession
    {
        public const int ControlPort = 24872;
        public const int InputPort = 24873;
        public const int ProtocolVersion = 1;
        public const int DefaultInputDelayFrames = 3;
        public const int MaximumInputDelayFrames = 8;

        private const int RedundantInputCount = 6;
        private static readonly long InputFrameTicks = Stopwatch.Frequency / 60;

        private readonly object _sync = new();
        private readonly SortedDictionary<long, GamepadInput> _localFrames = [];
        private readonly Dictionary<long, GamepadInput> _remoteFrames = [];

        private TcpListener _listener;
        private TcpClient _controlClient;
        private StreamWriter _controlWriter;
        private UdpClient _udpClient;
        private IPEndPoint _remoteInputEndpoint;

        private bool _listenerStarted;
        private bool _invitationPending;
        private bool _accepted;
        private bool _localReady;
        private bool _remoteReady;
        private bool _localGameStarted;
        private bool _remoteGameStarted;
        private bool _gameplayClockStarted;
        private bool _disconnecting;

        private long _playbackTick;
        private long _nextInputTickTimestamp;
        private long _lastUdpSendTimestamp;
        private GamepadInput _currentP1;
        private GamepadInput _currentP2;
        private bool _hasCurrentPair;

        private RyuSyncRole _role;
        private string _remoteDisplayName = string.Empty;
        private string _status = "Listening for RyuSync invitations";
        private int _inputDelayFrames = DefaultInputDelayFrames;

        public static RyuSyncSession Instance { get; } = new();

        public event Action StateChanged;
        public event Action InvitationReceived;

        private RyuSyncSession()
        {
        }

        public RyuSyncRole Role
        {
            get
            {
                lock (_sync)
                {
                    return _role;
                }
            }
        }

        public string RemoteDisplayName
        {
            get
            {
                lock (_sync)
                {
                    return _remoteDisplayName;
                }
            }
        }

        public string Status
        {
            get
            {
                lock (_sync)
                {
                    return _status;
                }
            }
        }

        public bool InvitationPending
        {
            get
            {
                lock (_sync)
                {
                    return _invitationPending;
                }
            }
        }

        public bool IsSessionEstablished
        {
            get
            {
                lock (_sync)
                {
                    return _accepted && _controlClient?.Connected == true;
                }
            }
        }

        public bool LocalReady
        {
            get
            {
                lock (_sync)
                {
                    return _localReady;
                }
            }
        }

        public bool RemoteReady
        {
            get
            {
                lock (_sync)
                {
                    return _remoteReady;
                }
            }
        }

        public bool BothGamesStarted
        {
            get
            {
                lock (_sync)
                {
                    return _localGameStarted && _remoteGameStarted;
                }
            }
        }

        public bool GameplayClockStarted
        {
            get
            {
                lock (_sync)
                {
                    return _gameplayClockStarted;
                }
            }
        }

        public int InputDelayFrames
        {
            get
            {
                lock (_sync)
                {
                    return _inputDelayFrames;
                }
            }
        }

        public long PlaybackTick
        {
            get
            {
                lock (_sync)
                {
                    return _playbackTick;
                }
            }
        }

        public void StartListening()
        {
            lock (_sync)
            {
                if (_listenerStarted)
                {
                    return;
                }

                try
                {
                    _listener = new TcpListener(IPAddress.Any, ControlPort);
                    _listener.Start();
                    _listenerStarted = true;
                    _status = $"Listening on TCP {ControlPort}";
                }
                catch (SocketException ex)
                {
                    _status = $"RyuSync listener failed: {ex.Message}";
                    Logger.Error?.Print(LogClass.Hid, _status);
                    RaiseStateChanged();
                    return;
                }
            }

            _ = Task.Run(AcceptLoopAsync);
            RaiseStateChanged();
        }

        public void SetInputDelayFrames(int frames)
        {
            lock (_sync)
            {
                if (_controlClient != null)
                {
                    return;
                }

                _inputDelayFrames = Math.Clamp(frames, 0, MaximumInputDelayFrames);
            }

            RaiseStateChanged();
        }

        public async Task SendInvitationAsync(string host)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                throw new ArgumentException("A Tailscale IP or MagicDNS hostname is required.", nameof(host));
            }

            StartListening();

            lock (_sync)
            {
                if (_controlClient != null)
                {
                    throw new InvalidOperationException("A RyuSync peer is already connected.");
                }

                _status = $"Connecting to {host}...";
            }

            RaiseStateChanged();

            TcpClient client = new();

            try
            {
                await client.ConnectAsync(host.Trim(), ControlPort);
                InitializeControlConnection(client, RyuSyncRole.Host);
                SendControl($"INVITE|{ProtocolVersion}|{InputDelayFrames}");

                lock (_sync)
                {
                    _status = "Invitation sent; waiting for acceptance";
                }

                RaiseStateChanged();
            }
            catch
            {
                client.Dispose();
                DisconnectInternal("Invitation connection failed", false);
                throw;
            }
        }

        public void AcceptInvitation()
        {
            lock (_sync)
            {
                if (!_invitationPending || _controlClient == null)
                {
                    return;
                }

                _invitationPending = false;
                _accepted = true;
                _status = $"Connected to {_remoteDisplayName}";
            }

            SendControl("ACCEPT");
            RaiseStateChanged();
        }

        public void DeclineInvitation()
        {
            SendControl("DECLINE");
            DisconnectInternal("Invitation declined", false);
        }

        public void SetReady(bool ready)
        {
            lock (_sync)
            {
                if (!_accepted)
                {
                    return;
                }

                _localReady = ready;
                _status = ready ? "Ready; waiting for peer" : "Connected; not ready";
            }

            SendControl(ready ? "READY|1" : "READY|0");
            TryStartGameplayClock();
            RaiseStateChanged();
        }

        public void NotifyGameStarted()
        {
            bool shouldSend = false;

            lock (_sync)
            {
                if (!_accepted || _localGameStarted)
                {
                    return;
                }

                _localGameStarted = true;
                _status = "SSBU started; waiting at RyuSync barrier";
                shouldSend = true;
            }

            if (shouldSend)
            {
                SendControl("GAME|1");
            }

            TryStartGameplayClock();
            RaiseStateChanged();
        }

        public bool TryGetSynchronizedInputs(GamepadInput localInput, out GamepadInput p1, out GamepadInput p2)
        {
            p1 = default;
            p2 = default;

            byte[] packetToSend = null;
            bool synchronized = false;
            bool result;

            lock (_sync)
            {
                if (!_gameplayClockStarted)
                {
                    return false;
                }

                long now = Stopwatch.GetTimestamp();

                if (now >= _nextInputTickTimestamp)
                {
                    long scheduledTick = _playbackTick + _inputDelayFrames;

                    if (!_localFrames.ContainsKey(scheduledTick))
                    {
                        GamepadInput canonicalLocal = localInput;
                        canonicalLocal.PlayerId = _role == RyuSyncRole.Host ? PlayerIndex.Player1 : PlayerIndex.Player2;
                        _localFrames[scheduledTick] = canonicalLocal;
                        packetToSend = BuildInputPacketLocked();
                    }

                    if (_playbackTick < _inputDelayFrames)
                    {
                        _currentP1 = Neutral(PlayerIndex.Player1);
                        _currentP2 = Neutral(PlayerIndex.Player2);
                        _hasCurrentPair = true;
                        AdvancePlaybackTickLocked(now);
                    }
                    else if (_localFrames.TryGetValue(_playbackTick, out GamepadInput local) &&
                             _remoteFrames.TryGetValue(_playbackTick, out GamepadInput remote))
                    {
                        if (_playbackTick == _inputDelayFrames)
                        {
                            _status = $"Synchronized - {_inputDelayFrames} frame input delay";
                            synchronized = true;
                        }

                        if (_role == RyuSyncRole.Host)
                        {
                            local.PlayerId = PlayerIndex.Player1;
                            remote.PlayerId = PlayerIndex.Player2;
                            _currentP1 = local;
                            _currentP2 = remote;
                        }
                        else
                        {
                            remote.PlayerId = PlayerIndex.Player1;
                            local.PlayerId = PlayerIndex.Player2;
                            _currentP1 = remote;
                            _currentP2 = local;
                        }

                        // Retain recently consumed local frames so the peer can recover from a
                        // one-sided dropped packet. Remote frames can be discarded after use.
                        _remoteFrames.Remove(_playbackTick);
                        _hasCurrentPair = true;
                        AdvancePlaybackTickLocked(now);
                    }
                    else
                    {
                        if (packetToSend == null && now - _lastUdpSendTimestamp >= Stopwatch.Frequency / 250)
                        {
                            packetToSend = BuildInputPacketLocked();
                        }

                        result = false;
                        goto ExitLock;
                    }
                }

                result = _hasCurrentPair;
                if (result)
                {
                    p1 = _currentP1;
                    p2 = _currentP2;
                }

            ExitLock:
                ;
            }

            if (packetToSend != null)
            {
                SendInputPacket(packetToSend);
            }

            if (synchronized)
            {
                Logger.Info?.Print(LogClass.Hid, "RyuSync received peer input; synchronized playback started.");
                RaiseStateChanged();
            }

            return result;
        }

        public void Disconnect()
        {
            SendControl("BYE");
            DisconnectInternal("Disconnected", false);
        }

        private async Task AcceptLoopAsync()
        {
            while (true)
            {
                TcpListener listener;

                lock (_sync)
                {
                    if (!_listenerStarted)
                    {
                        return;
                    }

                    listener = _listener;
                }

                try
                {
                    TcpClient client = await listener.AcceptTcpClientAsync();
                    bool reject;

                    lock (_sync)
                    {
                        reject = _controlClient != null;
                    }

                    if (reject)
                    {
                        client.Dispose();
                        continue;
                    }

                    InitializeControlConnection(client, RyuSyncRole.Guest);
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (SocketException ex)
                {
                    Logger.Warning?.Print(LogClass.Hid, $"RyuSync accept loop stopped: {ex.Message}");
                    return;
                }
            }
        }

        private void InitializeControlConnection(TcpClient client, RyuSyncRole role)
        {
            IPEndPoint remoteEndPoint = client.Client.RemoteEndPoint as IPEndPoint;
            IPAddress remoteAddress = remoteEndPoint?.Address;

            // Dual-mode TCP clients expose IPv4 peers as ::ffff:a.b.c.d. Our UDP
            // socket is IPv4, so normalize before sending or comparing senders.
            if (remoteAddress?.IsIPv4MappedToIPv6 == true)
            {
                remoteAddress = remoteAddress.MapToIPv4();
            }

            lock (_sync)
            {
                _controlClient = client;
                _role = role;
                _remoteDisplayName = remoteAddress?.ToString() ?? "peer";
                _remoteInputEndpoint = remoteAddress == null ? null : new IPEndPoint(remoteAddress, InputPort);

                NetworkStream stream = client.GetStream();
                _controlWriter = new StreamWriter(stream, new UTF8Encoding(false), 1024, true)
                {
                    AutoFlush = true,
                    NewLine = "\n",
                };
            }

            Logger.Info?.Print(LogClass.Hid, $"RyuSync {role}: controller input peer is {remoteAddress}:{InputPort}.");
            EnsureUdpStarted();
            _ = Task.Run(() => ControlReadLoopAsync(client));
            RaiseStateChanged();
        }

        private async Task ControlReadLoopAsync(TcpClient owner)
        {
            try
            {
                using StreamReader reader = new(owner.GetStream(), Encoding.UTF8, false, 1024, true);

                while (owner.Connected)
                {
                    string line = await reader.ReadLineAsync();
                    if (line == null)
                    {
                        break;
                    }

                    HandleControl(line);
                }
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                bool disconnect;

                lock (_sync)
                {
                    disconnect = ReferenceEquals(owner, _controlClient) && !_disconnecting;
                }

                if (disconnect)
                {
                    DisconnectInternal("Peer disconnected", false);
                }
            }
        }

        private void HandleControl(string line)
        {
            string[] parts = line.Split('|');
            string command = parts[0];
            bool invitation = false;

            lock (_sync)
            {
                switch (command)
                {
                    case "INVITE":
                        if (_role != RyuSyncRole.Guest || parts.Length < 3 ||
                            !int.TryParse(parts[1], out int version) || version != ProtocolVersion ||
                            !int.TryParse(parts[2], out int delay))
                        {
                            _status = "Incompatible RyuSync invitation";
                            break;
                        }

                        _inputDelayFrames = Math.Clamp(delay, 0, MaximumInputDelayFrames);
                        _invitationPending = true;
                        _status = $"Invitation from {_remoteDisplayName}";
                        invitation = true;
                        break;
                    case "ACCEPT":
                        if (_role == RyuSyncRole.Host)
                        {
                            _accepted = true;
                            _status = $"Connected to {_remoteDisplayName}";
                        }
                        break;
                    case "DECLINE":
                        _status = "Invitation declined by peer";
                        break;
                    case "READY":
                        _remoteReady = parts.Length > 1 && parts[1] == "1";
                        break;
                    case "GAME":
                        _remoteGameStarted = parts.Length > 1 && parts[1] == "1";
                        break;
                    case "BYE":
                        _status = "Peer disconnected";
                        break;
                }
            }

            if (command == "DECLINE" || command == "BYE")
            {
                DisconnectInternal(command == "DECLINE" ? "Invitation declined by peer" : "Peer disconnected", false);
                return;
            }

            TryStartGameplayClock();
            RaiseStateChanged();

            if (invitation)
            {
                InvitationReceived?.Invoke();
            }
        }

        private void TryStartGameplayClock()
        {
            lock (_sync)
            {
                if (_gameplayClockStarted || !_accepted || !_localReady || !_remoteReady || !_localGameStarted || !_remoteGameStarted)
                {
                    return;
                }

                _localFrames.Clear();
                _remoteFrames.Clear();
                _playbackTick = 0;
                _hasCurrentPair = false;
                _nextInputTickTimestamp = Stopwatch.GetTimestamp();
                _lastUdpSendTimestamp = 0;
                _gameplayClockStarted = true;
                _status = "Launch barrier released; waiting for peer controller input";
            }

            Logger.Info?.Print(LogClass.Hid, "RyuSync launch barrier released; waiting for peer controller input.");
            RaiseStateChanged();
        }

        private void AdvancePlaybackTickLocked(long now)
        {
            _playbackTick++;
            _nextInputTickTimestamp = now + InputFrameTicks;

            long oldestUsefulTick = Math.Max(0, _playbackTick - RedundantInputCount);
            List<long> staleLocal = [];
            List<long> staleRemote = [];

            foreach (long tick in _localFrames.Keys)
            {
                if (tick < oldestUsefulTick)
                {
                    staleLocal.Add(tick);
                }
            }

            foreach (long tick in staleLocal)
            {
                _localFrames.Remove(tick);
            }

            foreach (long tick in _remoteFrames.Keys)
            {
                if (tick < oldestUsefulTick)
                {
                    staleRemote.Add(tick);
                }
            }

            foreach (long tick in staleRemote)
            {
                _remoteFrames.Remove(tick);
            }
        }

        private byte[] BuildInputPacketLocked()
        {
            if (_remoteInputEndpoint == null || _localFrames.Count == 0)
            {
                return null;
            }

            long oldestUsefulTick = Math.Max(0, _playbackTick - RedundantInputCount);
            List<KeyValuePair<long, GamepadInput>> frames = [];

            foreach (KeyValuePair<long, GamepadInput> pair in _localFrames)
            {
                if (pair.Key >= oldestUsefulTick)
                {
                    frames.Add(pair);
                }
            }

            if (frames.Count > RedundantInputCount)
            {
                frames.RemoveRange(0, frames.Count - RedundantInputCount);
            }

            using MemoryStream stream = new();
            using BinaryWriter writer = new(stream);
            writer.Write((byte)ProtocolVersion);
            writer.Write((byte)frames.Count);

            foreach (KeyValuePair<long, GamepadInput> pair in frames)
            {
                GamepadInput state = pair.Value;
                writer.Write(pair.Key);
                writer.Write((long)state.Buttons);
                writer.Write(state.LStick.Dx);
                writer.Write(state.LStick.Dy);
                writer.Write(state.RStick.Dx);
                writer.Write(state.RStick.Dy);
            }

            _lastUdpSendTimestamp = Stopwatch.GetTimestamp();
            return stream.ToArray();
        }

        private void SendInputPacket(byte[] packet)
        {
            UdpClient udp;
            IPEndPoint endpoint;

            lock (_sync)
            {
                udp = _udpClient;
                endpoint = _remoteInputEndpoint;
            }

            if (udp == null || endpoint == null || packet == null)
            {
                return;
            }

            try
            {
                udp.Send(packet, packet.Length, endpoint);
            }
            catch (SocketException ex)
            {
                Logger.Debug?.Print(LogClass.Hid, $"RyuSync UDP send failed: {ex.Message}");
            }
        }

        private void EnsureUdpStarted()
        {
            lock (_sync)
            {
                if (_udpClient != null)
                {
                    return;
                }

                try
                {
                    _udpClient = new UdpClient(new IPEndPoint(IPAddress.Any, InputPort));
                }
                catch (SocketException ex)
                {
                    _status = $"RyuSync UDP bind failed: {ex.Message}";
                    Logger.Error?.Print(LogClass.Hid, _status);
                    return;
                }
            }

            _ = Task.Run(UdpReceiveLoopAsync);
        }

        private async Task UdpReceiveLoopAsync()
        {
            while (true)
            {
                UdpClient udp;

                lock (_sync)
                {
                    udp = _udpClient;
                }

                if (udp == null)
                {
                    return;
                }

                try
                {
                    UdpReceiveResult result = await udp.ReceiveAsync();
                    HandleInputPacket(result.Buffer, result.RemoteEndPoint);
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (SocketException)
                {
                    return;
                }
            }
        }

        private void HandleInputPacket(byte[] data, IPEndPoint sender)
        {
            lock (_sync)
            {
                if (_remoteInputEndpoint == null || sender.Address.Equals(_remoteInputEndpoint.Address) == false)
                {
                    return;
                }
            }

            try
            {
                using MemoryStream stream = new(data, false);
                using BinaryReader reader = new(stream);

                if (reader.ReadByte() != ProtocolVersion)
                {
                    return;
                }

                int count = reader.ReadByte();
                if (count > RedundantInputCount)
                {
                    return;
                }

                lock (_sync)
                {
                    for (int i = 0; i < count; i++)
                    {
                        long tick = reader.ReadInt64();
                        GamepadInput state = new()
                        {
                            PlayerId = _role == RyuSyncRole.Host ? PlayerIndex.Player2 : PlayerIndex.Player1,
                            Buttons = (ControllerKeys)reader.ReadInt64(),
                            LStick = new JoystickPosition
                            {
                                Dx = reader.ReadInt32(),
                                Dy = reader.ReadInt32(),
                            },
                            RStick = new JoystickPosition
                            {
                                Dx = reader.ReadInt32(),
                                Dy = reader.ReadInt32(),
                            },
                        };

                        if (tick >= _playbackTick)
                        {
                            _remoteFrames[tick] = state;
                        }
                    }
                }
            }
            catch (EndOfStreamException)
            {
            }
        }

        private void SendControl(string message)
        {
            StreamWriter writer;

            lock (_sync)
            {
                writer = _controlWriter;
            }

            if (writer == null)
            {
                return;
            }

            try
            {
                lock (writer)
                {
                    writer.WriteLine(message);
                    writer.Flush();
                }
            }
            catch (IOException)
            {
                DisconnectInternal("Peer disconnected", false);
            }
        }

        private void DisconnectInternal(string status, bool stopListener)
        {
            lock (_sync)
            {
                if (_disconnecting)
                {
                    return;
                }

                _disconnecting = true;

                try
                {
                    _controlWriter?.Dispose();
                    _controlClient?.Dispose();
                    _udpClient?.Dispose();

                    if (stopListener)
                    {
                        _listener?.Stop();
                    }
                }
                catch (ObjectDisposedException)
                {
                }

                _controlWriter = null;
                _controlClient = null;
                _udpClient = null;
                _remoteInputEndpoint = null;
                _role = RyuSyncRole.None;
                _remoteDisplayName = string.Empty;
                _invitationPending = false;
                _accepted = false;
                _localReady = false;
                _remoteReady = false;
                _localGameStarted = false;
                _remoteGameStarted = false;
                _gameplayClockStarted = false;
                _localFrames.Clear();
                _remoteFrames.Clear();
                _hasCurrentPair = false;
                _status = status;

                if (stopListener)
                {
                    _listenerStarted = false;
                    _listener = null;
                }

                _disconnecting = false;
            }

            RaiseStateChanged();
        }

        private static GamepadInput Neutral(PlayerIndex player)
        {
            return new GamepadInput
            {
                PlayerId = player,
                Buttons = 0,
                LStick = default,
                RStick = default,
            };
        }

        private void RaiseStateChanged()
        {
            StateChanged?.Invoke();
        }
    }
}
