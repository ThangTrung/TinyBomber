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
                udpSocket.DontFragment = true;  // Prevent UDP fragmentation

                Console.WriteLine($"[THANH CONG] UDP socket da mo tren cong {Constants.UDP_PORT}.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Khong the tao UDP socket: {ex.Message}");
                throw;
            }

            StartUpdateLoop();
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
                    clients[i].tcp.Connect(_client);
                    return;
                }
            }
            Console.WriteLine($"[TỪ CHỐI] Server da day, khong the nhan them {_client.Client.RemoteEndPoint}");
        }

        private static void InitializeServerData()
        {
            for (int i = 1; i <= MaxPlayers; i++)
            {
                clients.Add(i, new Client(i));
            }
        }

        public static UdpClient GetUdpSocket()
        {
            return udpSocket;
        }
    }
}
