using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using GameShared;

namespace GameServer
{
    class Server
    {
        public static int MaxPlayers { get; private set; }
        public static int Port { get; private set; }
        public static Dictionary<int, Client> clients = new Dictionary<int, Client>();

        private static TcpListener tcpListener = null!;
        private static UdpClient udpSocket = null!;
        private static Thread updateThread = null!;
        private static volatile bool isRunning = false;
        private static volatile bool stopping;
        private static readonly object slotLock = new object();

        public static void Start(int _maxPlayers, int _port)
        {
            MaxPlayers = _maxPlayers;
            Port = _port;

            Console.WriteLine("Dang khoi dong may chu Tiny Bomber...");
            ThreadManager.Initialize();
            InitializeServerData();
            GameLogic.InitializeMap();

            tcpListener = new TcpListener(IPAddress.Any, Port);
            tcpListener.Start();
            tcpListener.BeginAcceptTcpClient(TCPConnectCallback, null);
            Console.WriteLine($"[THANH CONG] May chu da mo tren cong {Port}.");

            try
            {
                udpSocket = new UdpClient(Constants.UDP_PORT);
                udpSocket.DontFragment = true;
                udpSocket.BeginReceive(UDPReceiveCallback, null);
                Console.WriteLine($"[THANH CONG] UDP socket da mo tren cong {Constants.UDP_PORT}.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Khong the tao UDP socket: {ex.Message}");
                throw;
            }

            StartUpdateLoop();
        }

        private static void UDPReceiveCallback(IAsyncResult _result)
        {
            try
            {
                IPEndPoint _clientEndPoint = new IPEndPoint(IPAddress.Any, 0);
                byte[] _data = udpSocket.EndReceive(_result, ref _clientEndPoint!);
                udpSocket.BeginReceive(UDPReceiveCallback, null);

                if (_data.Length < 4)
                {
                    return;
                }

                int _clientId = BitConverter.ToInt32(_data, 0);
                if (_clientId < 1 || _clientId > MaxPlayers)
                {
                    return;
                }

                Client.UDP udp;
                lock (slotLock)
                {
                    Client slot = clients[_clientId];
                    if (slot.tcp.socket == null) return;
                    udp = slot.udp;
                    if (udp.endPoint == null)
                    {
                        if (_data.Length == sizeof(int)) udp.Connect(_clientEndPoint);
                        return;
                    }
                }

                if (udp.endPoint.Equals(_clientEndPoint) && _data.Length > sizeof(int))
                {
                    byte[] _encryptedPayload = new byte[_data.Length - 4];
                    Array.Copy(_data, 4, _encryptedPayload, 0, _encryptedPayload.Length);
                    udp.HandleData(_encryptedPayload);
                }
            }
            catch (Exception _ex)
            {
                Console.WriteLine($"[UDP Receive Warning] {_ex.Message}");
                if (isRunning && udpSocket != null)
                {
                    try { udpSocket.BeginReceive(UDPReceiveCallback, null); } catch { }
                }
            }
        }

        public static void SendUDPData(IPEndPoint _clientEndPoint, Packet _packet)
        {
            try
            {
                if (_clientEndPoint != null && udpSocket != null)
                {
                    udpSocket.BeginSend(_packet.ToArray(), _packet.Length(), _clientEndPoint, null, null);
                }
            }
            catch (Exception _ex)
            {
                Console.WriteLine($"[ERROR] Loi gui UDP toi {_clientEndPoint}: {_ex.Message}");
            }
        }

        private static void StartUpdateLoop()
        {
            if (isRunning)
            {
                Console.WriteLine("[WARNING] Update loop da chay roi!");
                return;
            }

            isRunning = true;
            updateThread = new Thread(UpdateLoop)
            {
                Name = "ServerUpdateThread",
                IsBackground = false
            };
            updateThread.Start();
            Console.WriteLine("[INFO] Server update loop started (30 ticks/sec)");
        }

        private static void UpdateLoop()
        {
            while (isRunning)
            {
                ThreadManager.UpdateMain();
            }
        }

        public static void Shutdown()
        {
            Console.WriteLine("[INFO] Dang tat may chu...");
            stopping = true;
            isRunning = false;

            if (updateThread != null && updateThread.IsAlive)
            {
                updateThread.Join(5000);
            }

            tcpListener?.Stop();
            udpSocket?.Close();
            lock (slotLock)
            {
                foreach (var slot in clients.Values) slot.tcp.socket?.Close();
            }
            Console.WriteLine("[INFO] May chu da tat.");
        }

        private static void TCPConnectCallback(IAsyncResult _result)
        {
            TcpClient _client;
            try
            {
                _client = tcpListener.EndAcceptTcpClient(_result);
            }
            catch (Exception) when (!isRunning)
            {
                return;
            }
            try
            {
                tcpListener.BeginAcceptTcpClient(TCPConnectCallback, null);
            }
            catch (Exception) when (!isRunning)
            {
                _client.Close();
                return;
            }
            Console.WriteLine($"[KẾT NỐI MỚI] Co nguoi vua truy cap tu IP: {_client.Client.RemoteEndPoint}");

            lock (slotLock)
            {
                if (stopping)
                {
                    _client.Close();
                    return;
                }
                for (int i = 1; i <= MaxPlayers; i++)
                {
                    if (clients[i].tcp.socket == null)
                    {
                        try { clients[i].tcp.Connect(_client); }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"TCP setup failed for slot {i}: {ex.Message}");
                            var failed = clients[i].tcp;
                            int failedId = i;
                            ThreadManager.EnqueueAction(() => Disconnect(failedId, failed));
                        }
                        return;
                    }
                }
            }
            Console.WriteLine($"[TỪ CHỐI] Server da day, khong the nhan them {_client.Client.RemoteEndPoint}");
            _client.Close();
        }

        public static void Disconnect(int id, Client.TCP connection)
        {
            bool hadPlayer;
            lock (slotLock)
            {
                Client slot = clients[id];
                if (slot.tcp != connection) return;
                connection.socket?.Close();
                hadPlayer = slot.player != null;
                slot.player = null;
                slot.tcp = new Client.TCP(id);
                slot.udp = new Client.UDP(id);
            }
            if (hadPlayer) ServerSend.PlayerDisconnected(id);
        }

        private static void InitializeServerData()
        {
            for (int i = 1; i <= MaxPlayers; i++)
            {
                clients.Add(i, new Client(i));
            }
        }
    }
}
