using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;


namespace GameServer.Tests
{
    internal class Program
    {
        static TcpClient socket = new();

        static void Main(string[] args)
        {
            Console.WriteLine("Dang ket noi toi localhost:26950 ...");
            socket.BeginConnect("localhost", 26950, ConnectCallback, socket);

            Console.ReadLine();   // giữ chương trình sống để callback kịp chạy
        }

        private static void ConnectCallback(IAsyncResult _result)
        {
            try
            {
                socket.EndConnect(_result);
                Console.WriteLine("[THANH CONG] Da ket noi toi Server");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LOI] {ex.Message}");
            }
        }
    }
}
