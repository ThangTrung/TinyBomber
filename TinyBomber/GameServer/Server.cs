using System;
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
        private static bool isRunning = false;

        // [HEARTBEAT] settings
        private const int HEARTBEAT_CHECK_INTERVAL_MS = 1000;   // how often the server scans clients
        private const int HEARTBEAT_TIMEOUT_MS = 10000;         // silence longer than this = dead client

        // [HEARTBEAT] state
        private static long[] lastSeenTicks = null!;            // index = client id, value = Environment.TickCount64
        private static Timer heartbeatTimer = null!;

        public static void Start(int _maxPlayers, int _port)
        {
            MaxPlayers = _maxPlayers;
            Port = _port;

            Console.WriteLine("Dang khoi dong may chu Tiny Bomber...");
            ThreadManager.Initialize();
            InitializeServerData();

            tcpListener = new TcpListener(IPAddress.Any, Port);
            tcpListener.Start();
            tcpListener.BeginAcceptTcpClient(TCPConnectCallback, null);

            Console.WriteLine($"[THANH CONG] May chu da mo tren cong {Port}.");

            try
            {
                udpSocket = new UdpClient(Constants.UDP_PORT);
                udpSocket.DontFragment = true;  // Pvent UDP fragmentation

                Console.WriteLine($"[THANH CONG] UDP socket da mo tren cong {Constants.UDP_PORT}.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Khong the tao UDP socket: {ex.Message}");
                throw;
            }

            StartUpdateLoop();
            StartHeartbeat(); // [HEARTBEAT]
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
            isRunning = false;

            heartbeatTimer?.Dispose(); // [HEARTBEAT]

            if (updateThread != null && updateThread.IsAlive)
            {
                updateThread.Join(5000);
            }

            tcpListener?.Stop();
            Console.WriteLine("[INFO] May chu da tat.");
        }

        private static void TCPConnectCallback(IAsyncResult _result)
        {
            TcpClient _client = tcpListener.EndAcceptTcpClient(_result);
            tcpListener.BeginAcceptTcpClient(TCPConnectCallback, null);
            Console.WriteLine($"[KẾT NỐI MỚI] Co nguoi vua truy cap tu IP: {_client.Client.RemoteEndPoint}");

            for (int i = 1; i <= MaxPlayers; i++)
            {
                if (clients[i].tcp.socket == null)
                {
                    Touch(i); // [HEARTBEAT] start the silence timer from the moment of connection
                    clients[i].tcp.Connect(_client);
                    return;
                }
            }
            Console.WriteLine($"[TỪ CHỐI] Server da day, khong the nhan them {_client.Client.RemoteEndPoint}");
        }

        private static void InitializeServerData()
        {
            lastSeenTicks = new long[MaxPlayers + 1]; // [HEARTBEAT]

            for (int i = 1; i <= MaxPlayers; i++)
            {
                clients.Add(i, new Client(i));
            }
        }

        public static UdpClient GetUdpSocket()
        {
            return udpSocket;
        }


        // ================= [HEARTBEAT] =================

        /// <summary>
        /// Call this whenever ANY packet (TCP or UDP) arrives from a client.
        /// Any traffic counts as proof that the client is alive.
        /// </summary>
        public static void Touch(int _clientId)
        {
            if (_clientId < 1 || _clientId > MaxPlayers) return;
            Interlocked.Exchange(ref lastSeenTicks[_clientId], Environment.TickCount64);
        }

        private static void StartHeartbeat()
        {
            heartbeatTimer = new Timer(HeartbeatCheck, null,
                                       HEARTBEAT_CHECK_INTERVAL_MS,
                                       HEARTBEAT_CHECK_INTERVAL_MS);
            Console.WriteLine($"[INFO] Heartbeat started (timeout {HEARTBEAT_TIMEOUT_MS / 1000}s)");
        }

        private static void HeartbeatCheck(object? _state)
        {
            if (!isRunning) return;

            long now = Environment.TickCount64;

            for (int i = 1; i <= MaxPlayers; i++)
            {
                Client _c = clients[i];
                if (_c.tcp.socket == null) continue; // slot trống

                long silence = now - Interlocked.Read(ref lastSeenTicks[i]);
                if (silence > HEARTBEAT_TIMEOUT_MS)
                {
                    Console.WriteLine($"[HEARTBEAT] Client {i} khong phan hoi {silence / 1000}s -> ngat ket noi");

                    // Đưa việc ngắt kết nối về luồng game (ThreadManager.UpdateMain)
                    ThreadManager.EnqueueAction(() =>
                    {
                        if (_c.tcp.socket != null) // có thể đã bị ngắt trong lúc chờ
                        {
                            _c.Disconnect();
                        }
                    });
                }
            }
        }
    }
}