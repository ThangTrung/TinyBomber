using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using UnityEngine;
using GameShared;

public class Client : MonoBehaviour
{
    public static Client instance;
    public int myId = 0;
    public string ip = "127.0.0.1";
    public int port = Constants.PORT;

    public TCP tcp;
    public UDP udp;
    public bool IsConnecting { get; private set; }

    private const float ConnectionTimeout = 8f;
    private float handshakeDeadline;

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
        if (!IsConnecting) return;

        if (Time.realtimeSinceStartup >= handshakeDeadline)
        {
            Disconnect(myId == 0
                ? "Máy chủ không phản hồi qua TCP."
                : "Máy chủ chưa xác nhận kết nối UDP.");
            return;
        }

        udp?.RetryRegistration();
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

        IsConnecting = true;
        handshakeDeadline = Time.realtimeSinceStartup + ConnectionTimeout;
        GameManager.instance.SetConnectionStatus("Đang kết nối TCP…");
        tcp = new TCP();
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

    public void BeginUdpHandshake(int clientId)
    {
        if (!IsConnecting || udp != null) return;

        myId = clientId;
        ClientSend.WelcomeReceived();
        handshakeDeadline = Time.realtimeSinceStartup + ConnectionTimeout;
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
        if (!IsConnecting || udp == null) return;

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
        public TcpClient socket;
        private NetworkStream stream;
        private byte[] receiveBuffer;
        private TcpFrameReader frameReader;
        private volatile bool closed;
        private readonly Queue<byte[]> pendingWrites = new Queue<byte[]>();
        private bool writing;
        private bool sendFailed;

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
                int _packetLength = _packet.ReadInt();
                int _packetId = _packet.ReadInt();

                // Điều hướng
                if (_packetId == (int)ServerPackets.welcome)
                {
                    ClientHandle.Welcome(_packet);
                }
                else if (_packetId == (int)ServerPackets.spawnPlayer)
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
                else if (_packetId == (int)ServerPackets.lobbyReady)
                {
                    instance.FinishHandshake();
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
            if (closed || socket == null || Time.unscaledTime < nextRegistrationAt) return;

            nextRegistrationAt = Time.unscaledTime + 1f;
            using (var packet = new Packet()) SendData(packet);
        }

        public void SendData(Packet _packet)
        {
            try
            {
                _packet.InsertInt(instance.myId);
                if (!closed && socket != null)
                {
                    socket.BeginSend(_packet.ToArray(), _packet.Length(), null, null);
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
            using (Packet packet = new Packet(data))
            {
                int packetId;
                try
                {
                    if (data.Length < sizeof(int) || BitConverter.ToInt32(data, 0) != data.Length - sizeof(int))
                        throw new System.IO.InvalidDataException("Header độ dài UDP không khớp datagram.");
                    packet.DecryptPayload();
                    packet.ReadInt();
                    packetId = packet.ReadInt();
                    // Validate the datagram structure before calling a handler that can change state.
                    if (packetId == (int)ServerPackets.playerPosition)
                    {
                        if (packet.UnreadLength() != 3 * sizeof(int) + 2 * sizeof(float))
                            throw new System.IO.InvalidDataException("Độ dài UDP playerPosition không hợp lệ.");
                    }
                    else if (packetId == (int)ServerPackets.udpProbe)
                    {
                        if (packet.UnreadLength() != sizeof(int))
                            throw new System.IO.InvalidDataException("Độ dài UDP udpProbe không hợp lệ.");
                    }
                    else
                    {
                        throw new System.IO.InvalidDataException($"Không nhận diện được UDP packet ID {packetId}.");
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"Bỏ gói UDP không hợp lệ: {ex}");
                    return;
                }

                try
                {
                    if (packetId == (int)ServerPackets.playerPosition)
                        ClientHandle.PlayerPosition(packet);
                    else
                        ClientSend.ConfirmUdp(packet.ReadInt());
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
}
