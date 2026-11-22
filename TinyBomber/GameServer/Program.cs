using GameShared;
using System;
using System.Globalization;
using System.Text;

namespace GameServer
{
    class Program
    {
        private const int DefaultMaxConnections = 8;
        // 2,048 live slots budget roughly 40 MiB for 4 KiB read buffers and 16 KiB TCP frames.
        // Socket buffers, pending writes, and other allocations are additional.
        private const int MaxConnectionSlots = 2048;

        internal static bool TryParseMaxConnections(string[] args, out int maxConnections)
        {
            maxConnections = DefaultMaxConnections;
            if (args.Length == 0) return true;

            return args.Length == 2 && args[0] == "--max-connections" &&
                int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out maxConnections) &&
                maxConnections >= 1 && maxConnections <= MaxConnectionSlots;
        }

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

        static int Main(string[] args)
        {
            if (!TryParseMaxConnections(args, out int maxConnections))
            {
                Console.Error.WriteLine(
                    $"Cách dùng: GameServer [--max-connections <số nguyên từ 1 đến {MaxConnectionSlots}>]. " +
                    $"Mặc định: {DefaultMaxConnections}.");
                return 2;
            }

            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.Title = "Tiny Bomber Server";
            //TestEncryption();
            Console.WriteLine($"[INFO] MaxConnections = {maxConnections}");
            Server.Start(maxConnections, Constants.PORT);
            Console.WriteLine("\nNhan phim bat ky de tat may chu...\n");
            Console.ReadKey();
            Server.Shutdown();
            Console.WriteLine("Bye!");
            return 0;
        }
    }
}
