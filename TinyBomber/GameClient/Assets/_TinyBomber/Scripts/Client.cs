using System;
using System.Diagnostics;      // [HEARTBEAT]
using System.Net.Sockets;
using System.Threading;        // [HEARTBEAT]
using UnityEngine;
using GameShared;
using Debug = UnityEngine.Debug; // [HEARTBEAT] tránh trùng tên với System.Diagnostics.Debug

public class Client : MonoBehaviour
{
    public static Client instance;
    public int myId = 0;
    public string ip = "localhost";
    public int port = Constants.PORT;

    public TCP tcp;

    // ===== [HEARTBEAT] =====
    private const long PING_INTERVAL_MS = 3000;     // im lặng bao lâu thì gửi ping
    private const long SERVER_TIMEOUT_MS = 10000;   // không nhận gì từ server bao lâu thì coi là mất kết nối

    private static readonly Stopwatch clock = Stopwatch.StartNew();
    private long lastSentMs;
    private long lastReceivedMs;
    private bool serverLostReported = false;

    public void MarkSent() { Interlocked.Exchange(ref lastSentMs, clock.ElapsedMilliseconds); }
    public void MarkReceived() { Interlocked.Exchange(ref lastReceivedMs, clock.ElapsedMilliseconds); }

    private void Update()
    {
        if (tcp == null || !tcp.isReady) return;

        long now = clock.ElapsedMilliseconds;

        // 1) Chỉ gửi ping khi đã im lặng đủ lâu (đang di chuyển thì không cần)
        if (now - Interlocked.Read(ref lastSentMs) >= PING_INTERVAL_MS)
        {
            SendPing();
        }

        // 2) Phát hiện server chết (chỉ báo một lần)
        if (!serverLostReported && now - Interlocked.Read(ref lastReceivedMs) > SERVER_TIMEOUT_MS)
        {
            serverLostReported = true;
            Debug.Log("[HEARTBEAT] Mat ket noi voi server (khong nhan duoc gi trong 10s)");
            // TODO: hiện UI "Mất kết nối" / quay về menu / thử kết nối lại
        }
    }

    private void SendPing()
    {
        ClientSend.Ping();
    }
    // ===== hết phần [HEARTBEAT] =====

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

    private void Start()
    {
        tcp = new TCP();
        ConnectToServer();
    }

    public void ConnectToServer()
    {
        tcp.Connect();
    }

    public class TCP
    {
        public TcpClient socket;
        private NetworkStream stream;
        private byte[] receiveBuffer;

        public volatile bool isReady = false; // [HEARTBEAT] true khi đã kết nối xong và có stream

        public void Connect()
        {
            socket = new TcpClient
            {
                ReceiveBufferSize = Constants.DATA_BUFFER_SIZE,
                SendBufferSize = Constants.DATA_BUFFER_SIZE
            };

            receiveBuffer = new byte[Constants.DATA_BUFFER_SIZE];

            socket.BeginConnect(instance.ip, instance.port, ConnectCallback, socket);
        }

        private void ConnectCallback(IAsyncResult _result)
        {
            try   // [DEBUG]
            {
                socket.EndConnect(_result);
                

                if (!socket.Connected)
                {
                    Debug.Log("[LOI] Ket noi that bai (socket.Connected = false)");
                    return;
                }

                stream = socket.GetStream();

                Debug.Log($"[THÀNH CÔNG] Đã kết nối tới Server {instance.ip}:{instance.port}");

                instance.MarkSent();
                instance.MarkReceived();
                isReady = true;

                stream.BeginRead(receiveBuffer, 0, Constants.DATA_BUFFER_SIZE, ReceiveCallback, null);
            }
            catch (Exception _ex)   // [DEBUG]
            {
                Debug.Log($"[LOI] Khong the ket noi toi {instance.ip}:{instance.port} -> {_ex.Message}");
            }
        }

        public void SendData(Packet _packet)
        {
            try
            {
                if (socket != null)
                {
                    stream.BeginWrite(_packet.ToArray(), 0, _packet.Length(), null, null);
                    instance.MarkSent();   // [HEARTBEAT]
                }
            }
            catch (Exception _ex)
            {
                Debug.Log($"Lỗi gửi dữ liệu TCP tới server: {_ex}");
            }
        }

        private void ReceiveCallback(IAsyncResult _result)
        {
            
            try
            {
                int _byteLength = stream.EndRead(_result);
                if (_byteLength <= 0)
                {
                    // disconnect
                    return;
                }

                instance.MarkReceived();   // [HEARTBEAT] có dữ liệu từ server = server còn sống

                byte[] _data = new byte[_byteLength];
                Array.Copy(receiveBuffer, _data, _byteLength);

                ThreadManager.ExecuteOnMainThread(() =>
                {
                    using (Packet _packet = new Packet(_data))
                    {
                        _packet.DecryptPayload();
                        int _packetLength = _packet.ReadInt();
                        int _packetId = _packet.ReadInt();
                        Debug.Log(_packetId);

                        // Điều hướng
                        if (_packetId == (int)ServerPackets.welcome)
                        {
                            Debug.Log("Nhận welcome từ server");
                            ClientHandle.Welcome(_packet);
                        }
                        else if (_packetId == (int)ServerPackets.spawnPlayer)
                        {
                            Debug.Log("Nhận spawn player từ server");
                            ClientHandle.SpawnPlayer(_packet);
                        }
                        else if (_packetId == (int)ServerPackets.playerPosition)
                        {
                            Debug.Log("Nhận player position từ server");
                            ClientHandle.PlayerPosition(_packet);
                        }
                        else
                        {
                            Debug.Log("Nhận pong từ server");
                        }
                        // [HEARTBEAT] gói pong không cần xử lý, MarkReceived ở trên đã đủ.
                    }
                });

                stream.BeginRead(receiveBuffer, 0, Constants.DATA_BUFFER_SIZE, ReceiveCallback, null);
            }
            catch (Exception _ex)
            {
                Debug.Log($"Lỗi khi nhận dữ liệu TCP: {_ex}");
                // Disconnect();
            }
        }
    }
}