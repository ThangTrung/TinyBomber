using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using GameShared;

namespace GameServer
{
    class Server
    {
        public static int MaxConnections { get; private set; }
        public static int Port { get; private set; }
        public static Dictionary<int, Client> clients = new Dictionary<int, Client>();

        // Lobby membership and room lifecycles are owned by the update thread.
        private static readonly Dictionary<int, GameLogic> rooms = new();
        private static int lastRoomId;

        internal static GameLogic CreateRoom()
        {
            var room = new GameLogic(checked(++lastRoomId));
            rooms.Add(room.Id, room);
            return room;
        }

        internal static GameLogic? FindRoom(int id) => rooms.GetValueOrDefault(id);

        internal static void RemoveFromRoom(Client slot)
        {
            if (slot.Room is not GameLogic room) return;

            room.RemoveMember(slot);
            if (room.Members.Count == 0) rooms.Remove(room.Id);
        }

        private static TcpListener tcpListener = null!;
        private static UdpClient udpSocket = null!;
        private static Thread updateThread = null!;
        private static volatile bool isRunning = false;
        private static volatile bool stopping;
        private static readonly object slotLock = new object();
        private static ServerMetrics metrics = null!;
        private static ProcessMetricsSampler processMetrics = null!;
        private static MetricsExporter exporter = null!;
        internal static MetricsExporterStatus? ExporterStatus => exporter?.ReadStatus();
        internal static MetricsExporterStopResult? LastExporterStopResult { get; private set; }
        internal static ServerMetricsSnapshot? LatestMetrics => metrics?.Latest;
        internal static string? MetricsCaptureError => metrics?.CaptureError;
        // One collector for the server process, including late socket completions.
        internal static NetworkMetrics Network { get; } = new();

        public static void Start(int maxConnections, int port) =>
            Start(maxConnections, IPAddress.Any, port, Constants.UDP_PORT);

        internal static (int TcpPort, int UdpPort) Start(int maxConnections, IPAddress address, int tcpPort, int udpPort)
        {
            MaxConnections = maxConnections;

            Console.WriteLine("Dang khoi dong may chu Tiny Bomber...");
            ThreadManager.Initialize();
            InitializeServerData();

            tcpListener = new TcpListener(address, tcpPort);
            tcpListener.Start();
            Port = ((IPEndPoint)tcpListener.LocalEndpoint).Port;
            Console.WriteLine($"[THANH CONG] May chu da mo tren cong {Port}.");

            try
            {
                udpSocket = new UdpClient(new IPEndPoint(address, udpPort));
                udpSocket.DontFragment = true;
            }
            catch (Exception ex)
            {
                tcpListener.Stop();
                Console.WriteLine($"[ERROR] Khong the tao UDP socket: {ex.Message}");
                throw;
            }

            int boundUdpPort = ((IPEndPoint)udpSocket.Client.LocalEndPoint!).Port;
            Console.WriteLine($"[THANH CONG] UDP socket da mo tren cong {boundUdpPort}.");
            StartUpdateLoop();
            tcpListener.BeginAcceptTcpClient(TCPConnectCallback, null);
            udpSocket.BeginReceive(UDPReceiveCallback, null);
            return (Port, boundUdpPort);
        }

        private static void UDPReceiveCallback(IAsyncResult _result)
        {
            try
            {
                IPEndPoint _clientEndPoint = new IPEndPoint(IPAddress.Any, 0);
                byte[] _data = udpSocket.EndReceive(_result, ref _clientEndPoint!);
                Network.RecordUdpReceive(_data.Length);
                udpSocket.BeginReceive(UDPReceiveCallback, null);

                if (_data.Length < 4)
                {
                    return;
                }

                int _clientId = BitConverter.ToInt32(_data, 0);
                if (_clientId < 1 || _clientId > MaxConnections)
                {
                    return;
                }

                Client.UDP udp;
                lock (slotLock)
                {
                    Client slot = clients[_clientId];
                    if (slot.tcp.socket == null) return;
                    udp = slot.udp;
                }

                if (_data.Length == sizeof(int))
                {
                    ThreadManager.EnqueueAction(() =>
                    {
                        Client slot = clients[_clientId];
                        if (ServerHandle.RegisterUdp(slot, udp, _clientEndPoint)) ServerSend.UdpProbe(slot);
                    });
                    return;
                }

                if (_clientEndPoint.Equals(udp.endPoint))
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
            var sender = udpSocket;
            var counters = Network;
            if (_clientEndPoint == null || sender == null) return;

            try
            {
                byte[] data = _packet.ToArray();
                try
                {
                    sender.BeginSend(data, data.Length, _clientEndPoint, UdpSendCallback, (sender, counters));
                }
                catch
                {
                    counters.RecordUdpSendError();
                    throw;
                }
            }
            catch (Exception _ex)
            {
                Console.WriteLine($"[ERROR] Loi gui UDP toi {_clientEndPoint}: {_ex.Message}");
            }
        }

        private static void UdpSendCallback(IAsyncResult result)
        {
            var (sender, counters) = ((UdpClient, NetworkMetrics))result.AsyncState!;
            try
            {
                counters.RecordUdpSend(sender.EndSend(result));
            }
            catch (Exception)
            {
                // Completion belongs to this socket operation, independent of client slots.
                counters.RecordUdpSendError();
            }
        }

        private static void StartUpdateLoop()
        {
            if (isRunning)
            {
                Console.WriteLine("[WARNING] Update loop da chay roi!");
                return;
            }

            metrics = new ServerMetrics(Stopwatch.GetTimestamp());
            processMetrics = new ProcessMetricsSampler();
            processMetrics.Start();
            exporter = new MetricsExporter(Path.Combine(AppContext.BaseDirectory, "metrics"));
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
            var expiredHandshakes = new List<(int Id, Client.TCP Connection)>();
            while (isRunning)
            {
                long now = Stopwatch.GetTimestamp();
                metrics.BeginTick(now);
                ExpirePendingHandshakes(now, expiredHandshakes);
                ThreadManager.UpdateMain();
                long roomStarted = Stopwatch.GetTimestamp();
                foreach (var room in rooms.Values) room.UpdateRound(roomStarted);
                metrics.EndGameplay(ThreadManager.LastBatchMetrics,
                    Stopwatch.GetElapsedTime(roomStarted).TotalMilliseconds);
                if (metrics.IsSnapshotDue(Stopwatch.GetTimestamp()))
                    CaptureMetrics();
            }
        }

        private static void ExpirePendingHandshakes(long now, List<(int Id, Client.TCP Connection)> expired)
        {
            lock (slotLock)
            {
                foreach (var slot in clients.Values)
                {
                    Client.TCP connection = slot.tcp;
                    if (connection.socket != null && !slot.IsReady && connection.HandshakeHasExpired(now))
                        expired.Add((slot.id, connection));
                }
            }

            foreach (var (id, connection) in expired)
                ExpireHandshake(id, connection, now);
            expired.Clear();
        }

        // Runs on the update thread. Ignore a timeout for a TCP wrapper that has already been removed.
        internal static void ExpireHandshake(int id, Client.TCP connection, long now)
        {
            Client slot = clients[id];
            if (slot.tcp == connection && connection.socket != null && !slot.IsReady && connection.HandshakeHasExpired(now))
                Disconnect(id, connection);
        }

        private static void CaptureMetrics()
        {
            try
            {
                long captureStarted = Stopwatch.GetTimestamp();
                int connected;
                lock (slotLock)
                    connected = clients.Values.Count(slot => slot.tcp.socket != null);
                int ready = clients.Values.Count(slot => slot.IsReady);
                var roomMetrics = rooms.Values.Select(RoomMetrics.Capture).ToArray();
                int waiting = ThreadManager.GetWaitingActionCount();
                var process = processMetrics.Latest;
                var network = Network.Capture(Stopwatch.GetTimestamp());
                var snapshot = metrics.Publish(Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow,
                    connected, ready, roomMetrics, waiting, captureStarted, process, network);
                exporter.TryEnqueue(snapshot);
            }
            catch (Exception ex)
            {
                // Keep the previous snapshot and expose the failure without doing I/O on this thread.
                metrics.RecordCaptureFailure(ex);
            }
        }

        public static void Shutdown()
        {
            Console.WriteLine("[INFO] Dang tat may chu...");
            stopping = true;
            isRunning = false;
            processMetrics?.Dispose();

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
            if (exporter != null)
            {
                LastExporterStopResult = exporter.StopAsync().GetAwaiter().GetResult();
                try { Console.Error.WriteLine(MetricsExporter.FormatStopResult(LastExporterStopResult)); }
                catch (Exception) { /* The shutdown result remains available to the host/debugger. */ }
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
                for (int i = 1; i <= MaxConnections; i++)
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
            Client slot = clients[id];
            lock (slotLock)
            {
                if (slot.tcp != connection) return;
                connection.Close();
                slot.Username = null;
                slot.UdpConfirmed = false;
                slot.tcp = new Client.TCP(id);
                slot.udp = new Client.UDP(id);
            }
            RemoveFromRoom(slot);
        }

        private static void InitializeServerData()
        {
            for (int i = 1; i <= MaxConnections; i++)
            {
                clients.Add(i, new Client(i));
            }
        }
    }
}
