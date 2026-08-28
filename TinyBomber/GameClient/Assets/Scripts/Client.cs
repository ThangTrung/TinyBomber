using System;
using System.Net.Sockets;
using UnityEngine;
using GameShared;

public class Client : MonoBehaviour
{
    public static Client instance;

    public string ip = "127.0.0.1";
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

        private void ReceiveCallback(IAsyncResult _result)
        {
            
        }
    }
}