using GameShared;
using System;
using System.Text;

namespace GameServer
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.Title = "Tiny Bomber Server";
            Server.Start(Constants.MAX_PLAYERS, Constants.PORT);
            Console.WriteLine("\nNhan phim bat ky de tat may chu...\n");
            Console.ReadKey();
            Server.Shutdown();
            Console.WriteLine("Bye!");
        }
    }
}