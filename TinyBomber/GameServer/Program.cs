using GameShared;
using System;

namespace GameServer
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.Title = "Tiny Bomber Server";
            Server.Start(Constants.MAX_PLAYERS, Constants.PORT);
            Console.ReadKey();
        }
    }
}