using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using GameShared;
using Stopwatch = System.Diagnostics.Stopwatch;

public class Client : MonoBehaviour
{
    public static Client instance;
    public int myId = 0;
    public string ip = "127.0.0.1";
    public int port = Constants.PORT;

    public TCP tcp;
    public UDP udp;
    public bool IsConnecting { get; private set; }

    private const int HandshakeTimeoutSeconds = 16;

    private void Awake()
    {
        if (instance == null)
        {
            instance = this;
        }
        else if (instance != this)
        {
            Debug.Log("Đã có một Client tồn tại, hủy bản sao này!");
            Destroy(this);
        }
    }

    private void Update()
    {
        long now = Stopwatch.GetTimestamp();
            if (IsConnecting)
            {
            if (CheckHandshakeDeadline()) return;
            udp?.UpdateReceive(now);
            udp?.RetryRegistration();
            }
        else if (tcp != null && tcp.Heartbeat.HasExpired(now))
            {
                Disconnect("Máy chủ không phản hồi heartbeat.");
            }
        else if (udp != null)
        {
            udp.UpdateReceive(now);
            udp.UpdateHealth(now);
        }
    }

    private bool CheckHandshakeDeadline()
    {
        if (!tcp.HandshakeHasExpired(Stopwatch.GetTimestamp())) return false;

        Disconnect(tcp.SessionToken == null
            ? "Máy chủ chưa hoàn tất bắt tay TCP."
            : "Máy chủ chưa xác nhận kết nối UDP.");
        return true;
    }

    public void ConnectToServer(string address)
    {
        if (tcp != null || !GameManager.instance) return;

        address = address.Trim();
        UriHostNameType hostType = Uri.CheckHostName(address);
        if (hostType != UriHostNameType.IPv4 && hostType != UriHostNameType.Dns)
        {
            GameManager.instance.SetConnectionStatus("Nhập địa chỉ IPv4 hoặc tên máy chủ hợp lệ.");
            return;
        }

        tcp = new TCP();
        IsConnecting = true;
        GameManager.instance.SetConnectionStatus("Đang kết nối TCP…");
        try
        {
            tcp.Connect(address, port);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Lỗi kết nối TCP: {ex.Message}");
            Disconnect("Không kết nối được tới máy chủ.");
        }
    }

    private void AcceptServerHello(int version, int slotId)
    {
        if (!IsConnecting || myId != 0 || slotId <= 0)
            throw new InvalidDataException("ServerHello không hợp lệ.");
        if (CheckHandshakeDeadline()) return;
        if (version != ProtocolV4.Version)
        {
            Disconnect($"Máy chủ dùng protocol v{version}. Client yêu cầu v{ProtocolV4.Version}.");
            return;
        }

        myId = slotId;
        tcp.AcceptedProtocolVersion = version;
        ClientSend.SendClientHello();
        GameManager.instance.SetConnectionStatus("Đang chờ cấp phiên…");
    }

    private void BeginUdpHandshake(byte[] token)
    {
        if (!IsConnecting || myId == 0 || tcp.SessionToken != null)
            throw new InvalidDataException("SessionGranted đến sai giai đoạn.");
        if (CheckHandshakeDeadline()) return;

        tcp.SessionToken = token;
        GameManager.instance.SetConnectionStatus("Đang xác nhận UDP…");

        try
        {
            // Use the actual IPv4 peer selected by TCP, including when connecting by hostname.
            var server = (IPEndPoint)tcp.socket.Client.RemoteEndPoint;
            udp = new UDP(server.Address, tcp);
            udp.Connect();
        }
        catch (SocketException ex)
        {
            Debug.LogWarning($"Lỗi khởi tạo UDP: {ex.Message}");
            Disconnect("Không mở được kết nối UDP.");
        }
    }

    private void FinishHandshake()
    {
        if (!IsConnecting) return;
        if (!tcp.UdpConfirmSent)
            throw new InvalidDataException("lobbyReady đến trước khi xác nhận UDP.");
        if (CheckHandshakeDeadline()) return;

        IsConnecting = false;
        long now = Stopwatch.GetTimestamp();
        tcp.Heartbeat.Start(now);
        udp?.StartHealth(now);
        GameManager.instance.ConfirmLobbyReady();
    }

    public void ReturnToServerSelection()
    {
        Disconnect("Nhập địa chỉ server.");
    }

    private void Disconnect(string status)
    {
        CloseConnection();
        GameManager.ClearSession(status);
    }

    private void CloseConnection()
    {
        IsConnecting = false;
        tcp?.Close();
        udp?.Close();
        tcp = null;
        udp = null;
        myId = 0;
    }

    private void OnDestroy()
    {
        if (instance != this) return;
        CloseConnection();
        instance = null;
    }

    public class TCP
    {
        internal int AcceptedProtocolVersion { get; set; }
        internal bool UsesMovementOrdering => AcceptedProtocolVersion == ProtocolV4.Version;
        private readonly long handshakeDeadline =
            Stopwatch.GetTimestamp() + HandshakeTimeoutSeconds * Stopwatch.Frequency;
        public TcpClient socket;
        private NetworkStream stream;
        private byte[] receiveBuffer;
        private TcpFrameReader frameReader;
        private volatile bool closed;
        private readonly Queue<byte[]> pendingWrites = new Queue<byte[]>();
        private bool writing;
        private bool sendFailed;
        internal byte[] SessionToken { get; set; }
        internal bool UdpConfirmSent { get; set; }
        internal HeartbeatWatchdog Heartbeat { get; } = new HeartbeatWatchdog();

        internal bool HandshakeHasExpired(long now) => now >= handshakeDeadline;

        public void Close()
        {
            lock (pendingWrites)
            {
                closed = true;
                pendingWrites.Clear();
            }
            socket?.Close();
        }

        private void RequestDisconnect(string status)
        {
            if (closed) return;
            ThreadManager.ExecuteOnMainThread(() =>
            {
                if (!closed && instance != null && instance.tcp == this)
                    instance.Disconnect(status);
            });
        }

        public void Connect(string address, int port)
        {
            socket = new TcpClient(AddressFamily.InterNetwork)
            {
                ReceiveBufferSize = Constants.DATA_BUFFER_SIZE,
                SendBufferSize = Constants.DATA_BUFFER_SIZE
            };

            receiveBuffer = new byte[Constants.DATA_BUFFER_SIZE];
            frameReader = new TcpFrameReader();

            socket.BeginConnect(address, port, ConnectCallback, null);
        }

        private void ConnectCallback(IAsyncResult _result)
        {
            try
            {
                socket.EndConnect(_result);
                if (closed) return;
                stream = socket.GetStream();
                Debug.Log($"[THÀNH CÔNG] Đã kết nối tới Server {socket.Client.RemoteEndPoint}");
                stream.BeginRead(receiveBuffer, 0, Constants.DATA_BUFFER_SIZE, ReceiveCallback, null);
            }
            catch (Exception ex)
            {
                if (!closed) Debug.Log($"Lỗi kết nối TCP: {ex.Message}");
                RequestDisconnect("Không kết nối được tới máy chủ.");
            }
        }

        public void SendData(Packet _packet)
        {
            try
            {
                lock (pendingWrites)
                {
                    if (closed || sendFailed || socket == null) return;

                    pendingWrites.Enqueue(_packet.ToArray());
                    if (writing) return;
                    writing = true;
                }
            }
            catch (Exception ex)
            {
                FailWrites(ex);
                return;
            }

            _ = WritePendingAsync();
        }

        private async Task WritePendingAsync()
        {
            try
            {
                while (true)
                {
                    byte[] frame;
                    lock (pendingWrites)
                    {
                        if (closed || pendingWrites.Count == 0)
                        {
                            writing = false;
                            return;
                        }
                        frame = pendingWrites.Dequeue();
                    }

                    await stream.WriteAsync(frame, 0, frame.Length).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                FailWrites(ex);
            }
        }

        private void FailWrites(Exception ex)
        {
            bool notify;
            lock (pendingWrites)
            {
                notify = !closed && !sendFailed;
                sendFailed = true;
                writing = false;
                pendingWrites.Clear();
            }

            if (!notify) return;
            Debug.Log($"Lỗi gửi dữ liệu TCP tới server: {ex}");
            RequestDisconnect("Đã mất kết nối với máy chủ.");
        }

        private void HandleFrame(byte[] _data)
        {
            ThreadManager.ExecuteOnMainThread(() =>
            {
                if (closed || instance == null || instance.tcp != this) return;
                try
                {
                    HandlePacket(_data);
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                    if (!closed && instance != null && instance.tcp == this)
                        instance.Disconnect("Không xử lý được dữ liệu TCP từ máy chủ.");
                }
            });
        }

        private void HandlePacket(byte[] _data)
        {
            using (Packet _packet = new Packet(_data))
            {
                _packet.DecryptPayload();
                _packet.ReadInt();
                int _packetId = _packet.ReadInt(false);

                if (_packetId == (int)ServerPackets.serverHello)
                {
                    var hello = ProtocolV2.ReadServerHello(_packet);
                    instance.AcceptServerHello(hello.Version, hello.SlotId);
                    return;
                }
                if (_packetId == (int)ServerPackets.sessionGranted)
                {
                    instance.BeginUdpHandshake(ProtocolV2.ReadSessionGranted(_packet));
                    return;
                }
                if (_packetId == (int)ServerPackets.lobbyReady)
                {
                    ProtocolV2.ReadLobbyReady(_packet);
                    instance.FinishHandshake();
                    return;
                }
                if (_packetId == (int)ServerPackets.heartbeatPing)
                {
                    long sequence = ProtocolV3.ReadHeartbeatPing(_packet);
                    if (instance.IsConnecting)
                        throw new InvalidDataException("Heartbeat đến trước lobbyReady.");

                    long now = Stopwatch.GetTimestamp();
                    if (Heartbeat.HasExpired(now))
                    {
                        instance.Disconnect("Máy chủ không phản hồi heartbeat.");
                        return;
                    }
                    if (Heartbeat.ShouldReply(sequence, now))
                    {
                        using var pong = ProtocolV3.CreateHeartbeatPong(sequence);
                        SendData(pong);
                    }
                    return;
                }

                if (UsesMovementOrdering && !instance.IsConnecting)
                {
                    if (_packetId == (int)ServerPackets.playerPositionV4)
                    {
                        var position = ProtocolV4.ReadTcpPlayerPosition(_packet);
                        var target = new Vector2(position.X, position.Y);
                        if (position.PlayerId == instance.myId)
                            GameManager.instance.QueueOwnerMovementState(position.RoomId, position.RoundId,
                                position.PlayerId, position.AcceptedMoveSeq, position.AcceptedMoveSeq, target);
                        else
                            GameManager.instance.ApplyOrderedPeerPosition(position.RoomId, position.RoundId,
                                position.PlayerId, position.AcceptedMoveSeq, target);
                        return;
                    }
                    if (_packetId == (int)ServerPackets.playerCorrectionV4)
                    {
                        var correction = ProtocolV4.ReadTcpPlayerCorrection(_packet);
                        GameManager.instance.QueueOwnerMovementState(correction.RoomId, correction.RoundId,
                            correction.PlayerId, correction.ProcessedMoveSeq, correction.LastAcceptedMoveSeq,
                            new Vector2(correction.X, correction.Y));
                        return;
                    }
                    if (_packetId == (int)ServerPackets.playerPosition) return;
                }
                _packet.ReadInt();
                if (_packetId == (int)ServerPackets.welcome && instance.IsConnecting && instance.myId == 0)
                {
                    _packet.ReadString();
                    if (_packet.UnreadLength() != sizeof(int) || _packet.ReadInt() <= 0)
                        throw new InvalidDataException("Welcome v1 không hợp lệ.");
                    instance.Disconnect($"Máy chủ dùng protocol v1. Client yêu cầu v{ProtocolV4.Version}.");
                    return;
                }
                if (instance.IsConnecting)
                    throw new InvalidDataException("TCP packet đến sai giai đoạn bắt tay.");

                if (_packetId == (int)ServerPackets.spawnPlayer)
                {
                    ClientHandle.SpawnPlayer(_packet);
                }
                else if (_packetId == (int)ServerPackets.mapData)
                {
                    ClientHandle.MapData(_packet);
                }
                else if (_packetId == (int)ServerPackets.playerDisconnected)
                {
                    ClientHandle.PlayerDisconnected(_packet);
                }
                else if (_packetId == (int)ServerPackets.bombPlaced)
                {
                    ClientHandle.BombPlaced(_packet);
                }
                else if (_packetId == (int)ServerPackets.bombExploded)
                {
                    ClientHandle.BombExploded(_packet);
                }
                else if (_packetId == (int)ServerPackets.roundBegin)
                {
                    ClientHandle.RoundBegin(_packet);
                }
                else if (_packetId == (int)ServerPackets.matchState)
                {
                    ClientHandle.MatchState(_packet);
                }
                else if (_packetId == (int)ServerPackets.playerEliminated)
                {
                    ClientHandle.PlayerEliminated(_packet);
                }
                else if (_packetId == (int)ServerPackets.roomChanged)
                {
                    GameManager.instance.ChangeRoom(_packet.ReadInt());
                }
                else if (_packetId == (int)ServerPackets.roomError)
                {
                    GameManager.instance.ApplyRoomError((RoomError)_packet.ReadInt());
                }
                else
                {
                    throw new System.IO.InvalidDataException($"Không nhận diện được TCP packet ID {_packetId}.");
                }
            }
        }

        private void ReceiveCallback(IAsyncResult _result)
        {
            try
            {
                int _byteLength = stream.EndRead(_result);
                if (closed) return;
                if (_byteLength <= 0)
                {
                    RequestDisconnect("Máy chủ đã đóng kết nối.");
                    return;
                }

                frameReader.Feed(receiveBuffer.AsSpan(0, _byteLength), HandleFrame);

                if (!closed) stream.BeginRead(receiveBuffer, 0, Constants.DATA_BUFFER_SIZE, ReceiveCallback, null);
            }
            catch (Exception _ex)
            {
                if (!closed) Debug.Log($"Lỗi khi nhận dữ liệu TCP: {_ex}");
                RequestDisconnect("Đã mất kết nối với máy chủ.");
            }
        }
    }

    public class UDP
    {
        public UdpClient socket;
        private readonly IPEndPoint serverEndPoint;
        private readonly TCP connection;
        private volatile bool closed;
        private volatile bool socketUnavailable;
        private float nextRegistrationAt;
        private long? receiveRetryAt;
        private long nextSendErrorLogAt;
        private readonly UdpHealthMonitor health = new UdpHealthMonitor();

        public void Close()
        {
            closed = true;
            socket?.Close();
        }

        public UDP(IPAddress address, TCP connection)
        {
            serverEndPoint = new IPEndPoint(address, Constants.UDP_PORT);
            this.connection = connection;
        }

        public void Connect()
        {
            socket = new UdpClient(0);
            socket.Connect(serverEndPoint);
            ArmReceive();

            RetryRegistration();
        }

        public void RetryRegistration()
        {
            if (closed || socket == null || instance.tcp.UdpConfirmSent || Time.unscaledTime < nextRegistrationAt) return;

            nextRegistrationAt = Time.unscaledTime + 1f;
            using var packet = ProtocolV2.CreateUdpRegistration(instance.myId, instance.tcp.SessionToken);
            SendData(packet);
        }

        internal void StartHealth(long now) => health.Start(now);

        internal void UpdateHealth(long now)
        {
            health.Advance(now);
            TrySendHealthProbe(now, scheduled: true);
            PublishHealth();
        }

        internal bool TrySendHealthProbe() => TrySendHealthProbe(Stopwatch.GetTimestamp(), scheduled: false);

        private bool TrySendHealthProbe(long now, bool scheduled)
        {
            if (!IsCurrent() || instance.IsConnecting || connection.SessionToken is not byte[] token ||
                health.BeginProbe(now, scheduled) is not long sequence) return false;

            using var probe = ProtocolV3.CreateUdpHealthProbe(instance.myId, token, sequence);
            if (TrySendData(probe))
            {
                health.RecordProbeSent();
                return true;
            }
            health.AbandonProbe(sequence);
            return false;
        }

        public void SendData(Packet packet) => TrySendData(packet);

        internal bool TrySendData(Packet packet)
        {
            if (closed || socketUnavailable || socket == null) return false;
            try
            {
                byte[] frame = packet.ToArray();
                socket.BeginSend(frame, frame.Length, null, null);
                return true;
            }
            catch (ObjectDisposedException) { MarkSocketUnavailable(); }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.NotSocket) { MarkSocketUnavailable(); }
            catch (Exception ex)
            {
                long now = Stopwatch.GetTimestamp();
                if (now >= nextSendErrorLogAt)
                {
                    nextSendErrorLogAt = now + 5 * Stopwatch.Frequency;
                    Debug.LogWarning($"Lỗi gửi UDP: {ex.Message}");
                }
            }
            return false;
        }

        private void ReceiveCallback(IAsyncResult _result)
        {
            byte[] data;
            try
            {
                IPEndPoint sender = null;
                data = socket.EndReceive(_result, ref sender);
            }
            catch (Exception ex)
            {
                ScheduleReceiveRecovery(ex);
                return;
            }

            ArmReceive();
            if (data.Length < 4) return;
            ThreadManager.ExecuteOnMainThread(() =>
            {
                if (IsCurrent() && !socketUnavailable) HandleData(data);
            });
        }

        private void ArmReceive()
        {
            if (closed || socketUnavailable) return;
            try { socket.BeginReceive(ReceiveCallback, null); }
            catch (Exception ex) { ScheduleReceiveRecovery(ex); }
        }

        internal void UpdateReceive(long now)
        {
            if (!IsCurrent() || receiveRetryAt is not long retryAt || now < retryAt) return;
            receiveRetryAt = null;
            ArmReceive();
        }

        private void ScheduleReceiveRecovery(Exception error)
        {
            if (closed || socketUnavailable) return;
            ThreadManager.ExecuteOnMainThread(() =>
            {
                if (!IsCurrent() || socketUnavailable) return;
                if (error is SocketException ex && IsRecoverableReceiveError(ex.SocketErrorCode))
                    receiveRetryAt = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
                else
                    MarkSocketUnavailable();
            });
        }

        private static bool IsRecoverableReceiveError(SocketError error) => error is
            SocketError.ConnectionReset or SocketError.ConnectionRefused or SocketError.TimedOut or
            SocketError.Interrupted or SocketError.NetworkDown or SocketError.NetworkReset or SocketError.NetworkUnreachable or
            SocketError.HostUnreachable or SocketError.NoBufferSpaceAvailable or SocketError.WouldBlock or SocketError.MessageSize;

        private bool IsCurrent() => !closed && instance != null && instance.udp == this && instance.tcp == connection;

        private void MarkSocketUnavailable()
        {
            if (!IsCurrent() || socketUnavailable) return;
            socketUnavailable = true;
            receiveRetryAt = null;
            health.MarkSocketUnavailable();
            socket?.Close();
            PublishHealth();
        }

        private void PublishHealth()
        {
            if (!instance.IsConnecting && GameManager.instance)
                GameManager.instance.SetUdpHealth(health.IsDegraded, health.SocketUnavailable);
        }

        private void HandleData(byte[] data)
        {
            try
            {
                using Packet packet = ProtocolV4.OpenServerDatagram(data);
                if (packet.UnreadLength() < sizeof(int)) return;
                byte[] token = connection.SessionToken;
                switch ((ServerPackets)packet.ReadInt(false))
                {
                    case ServerPackets.udpProbeV2:
                        var probe = ProtocolV2.ReadUdpProbe(packet);
                        if (!instance.IsConnecting || connection.UdpConfirmSent ||
                            !probe.Token.AsSpan().SequenceEqual(token) || instance.CheckHandshakeDeadline()) return;
                        ClientSend.ConfirmUdp(token, probe.Nonce);
                        connection.UdpConfirmSent = true;
                        break;
                    case ServerPackets.udpPlayerPositionV4 when connection.UsesMovementOrdering:
                        var ordered = ProtocolV4.ReadUdpPlayerPosition(packet);
                        if (instance.IsConnecting || !ordered.Token.AsSpan().SequenceEqual(token)) return;
                        GameManager.instance.ApplyOrderedPeerPosition(ordered.RoomId, ordered.RoundId,
                            ordered.PlayerId, ordered.AcceptedMoveSeq, new Vector2(ordered.X, ordered.Y));
                        break;
                    case ServerPackets.udpHealthEcho:
                        var echo = ProtocolV3.ReadUdpHealthEcho(packet);
                        if (!instance.IsConnecting && echo.Token.AsSpan().SequenceEqual(token) &&
                            health.AcceptEcho(echo.Sequence, Stopwatch.GetTimestamp())) PublishHealth();
                        break;
                }
            }
            catch (InvalidDataException)
            {
                // Drop this datagram. The current UDP receive stays armed.
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }
    }
}
