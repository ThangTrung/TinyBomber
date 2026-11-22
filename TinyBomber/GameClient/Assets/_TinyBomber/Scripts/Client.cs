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
        if (!IsConnecting || CheckHandshakeDeadline()) return;
        udp?.RetryRegistration();
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
        if (version != ProtocolV2.Version)
        {
            Disconnect($"Máy chủ dùng protocol v{version}. Client yêu cầu v{ProtocolV2.Version}.");
            return;
        }

        myId = slotId;
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
            udp = new UDP(server.Address);
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

                _packet.ReadInt();
                if (_packetId == (int)ServerPackets.welcome && instance.IsConnecting && instance.myId == 0)
                {
                    _packet.ReadString();
                    if (_packet.UnreadLength() != sizeof(int) || _packet.ReadInt() <= 0)
                        throw new InvalidDataException("Welcome v1 không hợp lệ.");
                    instance.Disconnect($"Máy chủ dùng protocol v1. Client yêu cầu v{ProtocolV2.Version}.");
                    return;
                }
                if (instance.IsConnecting)
                    throw new InvalidDataException("TCP packet đến sai giai đoạn bắt tay.");

                if (_packetId == (int)ServerPackets.spawnPlayer)
                {
                    ClientHandle.SpawnPlayer(_packet);
                }
                else if (_packetId == (int)ServerPackets.playerPosition)
                {
                    ClientHandle.PlayerPosition(_packet);
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
                if (_byteLength <= 0)
                {
                    RequestDisconnect("Máy chủ đã đóng kết nối.");
                    return;
                }

                frameReader.Feed(receiveBuffer.AsSpan(0, _byteLength), HandleFrame);

                stream.BeginRead(receiveBuffer, 0, Constants.DATA_BUFFER_SIZE, ReceiveCallback, null);
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
        private volatile bool closed;
        private float nextRegistrationAt;

        public void Close()
        {
            closed = true;
            socket?.Close();
        }

        public UDP(IPAddress address)
        {
            serverEndPoint = new IPEndPoint(address, Constants.UDP_PORT);
        }

        public void Connect()
        {
            socket = new UdpClient(0);
            socket.Connect(serverEndPoint);
            socket.BeginReceive(ReceiveCallback, null);

            RetryRegistration();
        }

        public void RetryRegistration()
        {
            if (closed || socket == null || instance.tcp.UdpConfirmSent || Time.unscaledTime < nextRegistrationAt) return;

            nextRegistrationAt = Time.unscaledTime + 1f;
            using var packet = ProtocolV2.CreateUdpRegistration(instance.myId, instance.tcp.SessionToken);
            SendData(packet);
        }

        public void SendData(Packet _packet)
        {
            try
            {
                if (!closed && socket != null)
                {
                    byte[] frame = _packet.ToArray();
                    socket.BeginSend(frame, frame.Length, null, null);
                }
            }
            catch (Exception _ex)
            {
                Debug.Log($"Lỗi gửi UDP: {_ex}");
            }
        }

        private void ReceiveCallback(IAsyncResult _result)
        {
            try
            {
                IPEndPoint sender = null;
                byte[] _data = socket.EndReceive(_result, ref sender);
                socket.BeginReceive(ReceiveCallback, null);

                if (_data.Length < 4) return;

                ThreadManager.ExecuteOnMainThread(() =>
                {
                    if (closed || instance == null || instance.udp != this) return;
                    HandleData(_data);
                });
            }
            catch (Exception _ex)
            {
                if (!closed) Debug.Log($"Lỗi nhận UDP: {_ex}");
            }
        }

        private void HandleData(byte[] data)
        {
            try
            {
                using Packet packet = ProtocolV2.OpenServerDatagram(data);
                TCP connection = instance.tcp;
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
                    case ServerPackets.udpPlayerPosition:
                        var position = ProtocolV2.ReadUdpPlayerPosition(packet);
                        if (instance.IsConnecting || !position.Token.AsSpan().SequenceEqual(token)) return;
                        ClientHandle.ApplyPlayerPosition(position.RoomId, position.RoundId, position.PlayerId,
                            position.X, position.Y);
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
                if (!closed && instance != null && instance.udp == this)
                    instance.Disconnect("Không áp dụng được dữ liệu UDP từ máy chủ.");
            }
        }
    }
}
