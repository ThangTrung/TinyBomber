using System;
using System.Net;
using System.Net.Sockets;
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
        ConnectToServer();
    }

    private void Update()
    {
        if (myId != 0 && GameManager.instance && !GameManager.instance.IsReady)
            udp?.RetryRegistration();
    }

    public void ConnectToServer()
    {
        if (tcp != null) Disconnect();
        tcp = new TCP();
        udp = new UDP();
        tcp.Connect();
    }

    private void Disconnect()
    {
        tcp?.Close();
        udp?.Close();
        myId = 0;
        GameManager.ClearSession();
    }

    private void OnDestroy()
    {
        if (instance != this) return;
        Disconnect();
        instance = null;
    }

    public class TCP
    {
        public TcpClient socket;
        private NetworkStream stream;
        private byte[] receiveBuffer;
        private TcpFrameReader frameReader;
        private volatile bool closed;

        public void Close()
        {
            closed = true;
            socket?.Close();
        }

        private void RequestDisconnect()
        {
            if (closed) return;
            ThreadManager.ExecuteOnMainThread(() =>
            {
                if (!closed && instance != null && instance.tcp == this)
                    instance.Disconnect();
            });
        }

        public void Connect()
        {
            socket = new TcpClient
            {
                ReceiveBufferSize = Constants.DATA_BUFFER_SIZE,
                SendBufferSize = Constants.DATA_BUFFER_SIZE
            };

            receiveBuffer = new byte[Constants.DATA_BUFFER_SIZE];
            frameReader = new TcpFrameReader();

            socket.BeginConnect(instance.ip, instance.port, ConnectCallback, socket);
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
                RequestDisconnect();
            }
        }

        public void SendData(Packet _packet)
        {
            try
            {
                if (!closed && socket != null)
                {
                    stream.BeginWrite(_packet.ToArray(), 0, _packet.Length(), null, null);
                }
            }
            catch (Exception _ex)
            {
                Debug.Log($"Lỗi gửi dữ liệu TCP tới server: {_ex}");
            }
        }

        private void HandleFrame(byte[] _data)
        {
            ThreadManager.ExecuteOnMainThread(() =>
            {
                if (closed || instance == null || instance.tcp != this) return;
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
                }
            });
        }

        private void ReceiveCallback(IAsyncResult _result)
        {
            try
            {
                int _byteLength = stream.EndRead(_result);
                if (_byteLength <= 0)
                {
                    RequestDisconnect();
                    return;
                }

                frameReader.Feed(receiveBuffer.AsSpan(0, _byteLength), HandleFrame);

                stream.BeginRead(receiveBuffer, 0, Constants.DATA_BUFFER_SIZE, ReceiveCallback, null);
            }
            catch (Exception _ex)
            {
                if (!closed) Debug.Log($"Lỗi khi nhận dữ liệu TCP: {_ex}");
                RequestDisconnect();
            }
        }
    }

    public class UDP
    {
        public UdpClient socket;
        public IPEndPoint endPoint;
        private volatile bool closed;
        private float nextRegistrationAt;

        public void Close()
        {
            closed = true;
            socket?.Close();
        }

        public UDP()
        {
            endPoint = new IPEndPoint(IPAddress.Parse(instance.ip), Constants.UDP_PORT);
        }

        public void Connect(int _localPort)
        {
            socket = new UdpClient(_localPort);
            socket.Connect(endPoint);
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
                byte[] _data = socket.EndReceive(_result, ref endPoint);
                socket.BeginReceive(ReceiveCallback, null);

                if (_data.Length < 4) return;

                ThreadManager.ExecuteOnMainThread(() =>
                {
                    if (closed || instance == null || instance.udp != this) return;
                    using (Packet _packet = new Packet(_data))
                    {
                        _packet.DecryptPayload();
                        int _packetLength = _packet.ReadInt();
                        int _packetId = _packet.ReadInt();

                        if (_packetId == (int)ServerPackets.playerPosition)
                        {
                            ClientHandle.PlayerPosition(_packet);
                        }
                        else if (_packetId == (int)ServerPackets.udpProbe)
                        {
                            ClientSend.ConfirmUdp(_packet.ReadInt());
                        }
                    }
                });
            }
            catch (Exception _ex)
            {
                if (!closed) Debug.Log($"Lỗi nhận UDP: {_ex}");
            }
        }
    }
}
