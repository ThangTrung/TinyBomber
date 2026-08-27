using System;
using System.Net;
using System.Net.Sockets;

namespace GameServer
{
    class Server
    {
        public static int MaxPlayers { get; private set; }
        public static int Port { get; private set; }

        private static TcpListener tcpListener = null!;

        public static void Start(int _maxPlayers, int _port)
        {
            MaxPlayers = _maxPlayers;
            Port = _port;

            Console.WriteLine("Dang khoi dong may chu Tiny Bomber...");

            // Khởi tạo người gác cổng, lắng nghe trên mọi IP (Any) ở cổng Port
            tcpListener = new TcpListener(IPAddress.Any, Port);
            tcpListener.Start();

            // Bắt đầu nhận khách (dùng Bất đồng bộ - Async để không làm treo màn hình đen)
            tcpListener.BeginAcceptTcpClient(TCPConnectCallback, null);

            Console.WriteLine($"[THANH CONG] May chu da mo tren cong {Port}.");
        }

        private static void TCPConnectCallback(IAsyncResult _result)
        {
            // 1. Nhận người khách vừa tới
            TcpClient _client = tcpListener.EndAcceptTcpClient(_result);

            // 2. NGAY LẬP TỨC quay ra cửa đón người tiếp theo (rất quan trọng)
            tcpListener.BeginAcceptTcpClient(TCPConnectCallback, null);

            // 3. In ra IP của khách vừa vào
            Console.WriteLine($"[KẾT NỐI MỚI] Co nguoi vua truy cap tu IP: {_client.Client.RemoteEndPoint}");

            // (Tạm thời để đây, bài sau chúng ta sẽ phân phát ID cho khách này)
        }
    }
}
