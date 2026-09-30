using System;
using System.Net.Sockets;
using UnityEngine;
using GameShared;

public class Client : MonoBehaviour
{
    public static Client instance;
    public int myId = 0;
    public string ip = "172.31.99.137";
    public int port = Constants.PORT;

    public TCP tcp;

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
            socket.EndConnect(_result);

            if (!socket.Connected)
            {
                return;
            }

            stream = socket.GetStream();

            Debug.Log($"[THÀNH CÔNG] Đã kết nối tới Server {instance.ip}:{instance.port}");

            stream.BeginRead(receiveBuffer, 0, Constants.DATA_BUFFER_SIZE, ReceiveCallback, null);
        }

        public void SendData(Packet _packet)
        {
            try
            {
                if (socket != null)
                {
                    stream.BeginWrite(_packet.ToArray(), 0, _packet.Length(), null, null);
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

                byte[] _data = new byte[_byteLength];
                Array.Copy(receiveBuffer, _data, _byteLength);

                ThreadManager.ExecuteOnMainThread(() =>
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