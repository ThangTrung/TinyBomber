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

        internal static bool TryParseMaxConnections(string[] args, out int maxConnections) =>
            TryParseOptions(args, out maxConnections, out _);

        internal static bool TryParseOptions(string[] args, out int maxConnections, out int? dashboardPort)
        {
            maxConnections = DefaultMaxConnections;
            dashboardPort = null;
            bool hasLimit = false;
            for (int i = 0; i < args.Length; i += 2)
            {
                if (i + 1 >= args.Length || !int.TryParse(args[i+1], NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int value)) return false;
                switch (args[i])
                {
                    case "--max-connections" when !hasLimit && value is >= 1 and <= MaxConnectionSlots:
                        maxConnections = value;
                        hasLimit = true;
                        break;
                    case "--dashboard-port" when dashboardPort == null && value is >= 1 and <= 65535:
                        dashboardPort = value;
                        break;
                    default: return false;
                }
            }
            return true;
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

        static async Task<int> Main(string[] args)
        {
            if (!TryParseOptions(args, out int maxConnections, out int? dashboardPort))
            {
                Console.Error.WriteLine(
                    $"Cách dùng: GameServer [--max-connections <số nguyên từ 1 đến {MaxConnectionSlots}>] [--dashboard-port <1–65535>]. " +
                    $"Mặc định: {DefaultMaxConnections}.");
                return 2;
            }

            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.Title = "Tiny Bomber Server";
            //TestEncryption();
            Console.WriteLine($"[INFO] MaxConnections = {maxConnections}");
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            Server.Start(maxConnections, Constants.PORT);
            Monitoring.MonitoringDashboard? dashboard = null;
            try
            {
                if (dashboardPort is int port)
                {
                    try
                    {
                        dashboard = await Monitoring.MonitoringDashboard.StartAsync(port, started,
                            () => Server.LatestMetrics, () => Server.ExporterStatus, () => Server.MetricsCaptureError != null);
                        if (dashboard != null) Console.WriteLine($"[MONITOR] {dashboard.Address}");
                    }
                    catch (Exception error)
                    {
                        Console.Error.WriteLine($"[MONITOR] Startup failed ({error.GetType().Name}). GameServer continues.");
                    }
                }
                Console.WriteLine("\nNhan phim bat ky de tat may chu...\n");
                Console.ReadKey();
            }
            finally
            {
                try
                {
                    if (dashboard != null) await dashboard.DisposeAsync();
                }
                finally { Server.Shutdown(); }
            }

            Console.WriteLine("Bye!");
            return 0;
        }
    }
}
