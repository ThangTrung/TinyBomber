using System;
using System.Collections.Generic;
using System.Buffers.Binary;
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
        private static readonly int finalPositionIdleMs =
            int.TryParse(Environment.GetEnvironmentVariable("TINYBOMBER_FINAL_POSITION_IDLE_MS"), out int delay) &&
            delay is >= 50 and <= 5000 ? delay : GameLogic.DefaultFinalPositionIdleMs;
        private static int lastRoomId;

        internal static GameLogic CreateRoom()
        {
            var room = new GameLogic(checked(++lastRoomId), finalPositionIdleMs);
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
        internal static (bool Current, bool Stopping) WriteContext(int id, Client.TCP connection)
        {
            lock (slotLock) return (clients.TryGetValue(id, out var slot) && slot.tcp == connection, stopping);
        }

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
            var endpoint = new IPEndPoint(IPAddress.Any, 0);
            byte[]? data = null;
            try
            {
                data = udpSocket.EndReceive(_result, ref endpoint!);
                EventMetrics.Record(ServerEvent.UdpDatagramReceived);
                Network.RecordUdpReceive(data.Length);
            }
            catch (ObjectDisposedException) when (!isRunning) { return; }
            catch (SocketException ex)
            {
                if (isRunning) Console.WriteLine($"[UDP Receive Warning] {ex.Message}");
            }

            if (!isRunning) return;
            try
            {
                udpSocket.BeginReceive(UDPReceiveCallback, null);
            }
            catch (ObjectDisposedException) when (!isRunning) { return; }
            catch (SocketException ex)
            {
                if (isRunning) Console.WriteLine($"[UDP Receive Warning] {ex.Message}");
                return;
            }

            if (data != null) HandleUdpDatagram(data, endpoint);
        }

        private static void HandleUdpDatagram(byte[] data, IPEndPoint endpoint)
        {
            if (data.Length <= sizeof(int)) { EventMetrics.Record(ServerEvent.UdpMalformed); return; }
            int slotId = BinaryPrimitives.ReadInt32LittleEndian(data);
            if (slotId < 1 || slotId > MaxConnections) return;

            Client.TCP connection;
            Client.UDP udp;
            lock (slotLock)
            {
                Client slot = clients[slotId];
                connection = slot.tcp;
                if (connection.socket == null) return;
                udp = slot.udp;
            }
            if (udp.endPoint is IPEndPoint bound && !endpoint.Equals(bound)) return;

            try
            {
                using Packet packet = ProtocolV2.OpenClientDatagram(data, out _);
                if (packet.UnreadLength() < sizeof(int)) return;
                switch ((ClientPackets)packet.ReadInt(false))
                {
                    case ClientPackets.udpRegistration:
                        byte[] token = ProtocolV2.ReadUdpRegistration(packet);
                        ThreadManager.EnqueueAction(() =>
                        {
                            Client slot = clients[slotId];
                            if (ServerHandle.RegisterUdp(slot, connection, udp, endpoint, token))
                                ServerSend.UdpProbe(slot);
                        });
                        break;
                    case ClientPackets.udpPlayerMovement when !connection.UsesMovementOrdering:
                        var move = ProtocolV2.ReadUdpPlayerMovement(packet);
                        ThreadManager.EnqueueAction(() => ServerHandle.UdpMovement(clients[slotId], connection, udp,
                            endpoint, move.Token, move.RoomId, move.RoundId, move.X, move.Y));
                        break;
                    case ClientPackets.udpPlayerMovementV4 when connection.UsesMovementOrdering:
                        var orderedMove = ProtocolV4.ReadUdpPlayerMovement(packet);
                        EventMetrics.Record(ServerEvent.MoveUdp);
                        ThreadManager.EnqueueAction(() => ServerHandle.UdpMovement(clients[slotId], connection, udp,
                            endpoint, orderedMove.Token, orderedMove.RoomId, orderedMove.RoundId,
                            orderedMove.X, orderedMove.Y, moveSeq: orderedMove.MoveSeq));
                        break;
                    case ClientPackets.udpHealthProbe:
                        var probe = ProtocolV3.ReadUdpHealthProbe(packet);
                        ThreadManager.EnqueueAction(() => ServerHandle.UdpHealthProbe(clients[slotId], connection, udp,
                            endpoint, probe.Token, probe.Sequence));
                        break;
                }
            }
            catch (InvalidDataException)
            {
                EventMetrics.Record(ServerEvent.UdpMalformed);
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
                EventMetrics.Record(ServerEvent.UdpDatagramSent);
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
            var dueConnections = new List<(int Id, Client.TCP Connection)>();
            long deadline = Stopwatch.GetTimestamp() + TickPacer.Period;
            while (isRunning)
            {
                long now = Stopwatch.GetTimestamp();
                metrics.BeginTick(now);
                UpdateConnections(now, dueConnections);
                var actions = ThreadManager.DrainActions();
                long roomStarted = Stopwatch.GetTimestamp();
                foreach (var room in rooms.Values) room.UpdateRound(roomStarted);
                double roomMs = Stopwatch.GetElapsedTime(roomStarted).TotalMilliseconds;
                if (metrics.IsSnapshotDue(Stopwatch.GetTimestamp()))
                    CaptureMetrics();
                metrics.EndGameplay(TickPacer.WaitUntil(deadline, actions), roomMs);
                deadline = TickPacer.Next(deadline, Stopwatch.GetTimestamp());
            }
        }

        private static void UpdateConnections(long now, List<(int Id, Client.TCP Connection)> due)
        {
            lock (slotLock)
            {
                foreach (var slot in clients.Values)
                {
                    Client.TCP connection = slot.tcp;
                    if (connection.socket != null && (slot.IsReady
                        ? connection.HeartbeatIsDue(now)
                        : connection.HandshakeHasExpired(now)))
                        due.Add((slot.id, connection));
                }
            }

            foreach (var (id, connection) in due)
                UpdateConnection(id, connection, Stopwatch.GetTimestamp());
            due.Clear();
        }

        internal static void UpdateConnection(int id, Client.TCP connection, long now)
        {
            Client slot = clients[id];
            if (slot.tcp != connection || connection.socket == null) return;
            if (!slot.IsReady)
            {
                ExpireHandshake(id, connection, now);
                return;
            }

            if (connection.HeartbeatHasExpired(now)) Disconnect(id, connection);
            else connection.SendHeartbeatIfDue(now);
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
                var transports = clients.Values.Select(slot => slot.tcp.ReadTransport()).ToArray();
                var transport = new TransportSample(transports.Sum(t => t.Pending), transports.Max(t => t.HighWater),
                    transports.Sum(t => t.InFlight), transports.Sum(t => t.HeartbeatSamples), transports.Sum(t => t.HeartbeatTotalMs));
                var snapshot = metrics.Publish(Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow,
                    connected, ready, roomMetrics, waiting, captureStarted, process, network, transport);
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
            Client.TCP[] connections;
            lock (slotLock)
                connections = clients.Values.Select(slot => slot.tcp).ToArray();
            foreach (var connection in connections) connection.Close();
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

            Client.TCP? connection = null;
            int slotId = 0;
            lock (slotLock)
            {
                for (int i = 1; !stopping && i <= MaxConnections; i++)
                {
                    if (clients[i].tcp.socket == null)
                    {
                        connection = clients[i].tcp;
                        connection.Attach(_client);
                        slotId = i;
                        break;
                    }
                }
            }
            if (connection == null)
            {
                if (!stopping)
                    Console.WriteLine($"[TỪ CHỐI] Server da day, khong the nhan them {_client.Client.RemoteEndPoint}");
                _client.Close();
                return;
            }

            try { connection.StartReceiving(); }
            catch (Exception ex)
            {
                if (stopping) return;
                Console.WriteLine($"TCP setup failed for slot {slotId}: {ex.Message}");
                ThreadManager.EnqueueAction(() => Disconnect(slotId, connection));
            }
        }

        public static void Disconnect(int id, Client.TCP connection)
        {
            Client slot = clients[id];
            lock (slotLock)
            {
                if (slot.tcp != connection) return;
                slot.Username = null;
                slot.UdpConfirmed = false;
                slot.tcp = new Client.TCP(id);
                slot.udp = new Client.UDP(id);
            }
            connection.Close();
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
