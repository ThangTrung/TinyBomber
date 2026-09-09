using GameShared;
using System;
using System.Text;

namespace GameServer
{
    class Program
    {

        static void TestEncryption()
        {
            Console.WriteLine("[TEST] Starting encryption test...");

            using (Packet sendPacket = new Packet((int)ServerPackets.welcome))
            {
                sendPacket.Write("Test Message");
                sendPacket.WriteLength();
                sendPacket.EncryptPayload();
                byte[] encrypted = sendPacket.ToArray();

                Console.WriteLine($"[TEST] Encrypted packet: {encrypted.Length} bytes");

                using (Packet rxPacket = new Packet(encrypted))
                {
                    rxPacket.DecryptPayload();

                    int packetLength = rxPacket.ReadInt();

                    int id = rxPacket.ReadInt();
                    string msg = rxPacket.ReadString();

                    Console.WriteLine($"[TEST] Decrypted: ID={id}, Message={msg}");

                    if (msg == "Test Message")
                    {
                        Console.WriteLine("[TEST] ✅ ENCRYPTION WORKING!");
                        return;
                    }
                }
            }

            Console.WriteLine("[TEST] ❌ ENCRYPTION FAILED!");
        }

        static void Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.Title = "Tiny Bomber Server";
            //TestEncryption();
            Server.Start(Constants.MAX_PLAYERS, Constants.PORT);
            Console.WriteLine("\nNhan phim bat ky de tat may chu...\n");
            Console.ReadKey();
            Server.Shutdown();
            Console.WriteLine("Bye!");
        }
    }
}