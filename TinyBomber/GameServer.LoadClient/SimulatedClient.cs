using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using GameShared;

namespace GameServer.LoadClient;

internal readonly record struct ClientState(
    bool Connected, bool Ready, int Id, int RoomId, int RoundId, MatchPhase Phase,
    int ReadyInRoom, bool HasRoundData, bool Alive);

internal sealed class SimulatedClient : IAsyncDisposable
{
    private const int HandshakeTimeoutSeconds = 16;
    private readonly object stateLock = new();
    private readonly CancellationTokenSource lifetime;
    private readonly RunErrors errors;
    private readonly int index;
    private readonly TaskCompletionSource<int> welcome = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<int> udpChallenge = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource lobbyReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly HashSet<int> roster = new();
    private readonly Dictionary<int, Position> spawns = new();
    private readonly HashSet<(int Col, int Row)> bombs = new();
    private TcpClient? tcp;
    private UdpClient? udp;
    private Task receiveTcp = Task.CompletedTask;
    private Task receiveUdp = Task.CompletedTask;
    private long? handshakeDeadline;
    private bool handshakeTimedOut;
    private bool connected;
    private bool ready;
    private bool alive;
    private bool corrected;
    private int id;
    private int roomId;
    private int roundId;
    private int readyInRoom;
    private MatchPhase phase;
    private int[,]? map;
    private int watchedPlayer;
    private MovementTrace? watchedTrace;

    internal MovementTrace Trace { get; } = new();

    internal SimulatedClient(int index, RunErrors errors, CancellationToken stop)
    {
        this.index = index;
        this.errors = errors;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
    }

    internal ClientState ReadState()
    {
        lock (stateLock)
            return new ClientState(connected, ready, id, roomId, roundId, phase, readyInRoom,
                map != null && roster.Contains(id) && roster.SetEquals(spawns.Keys), alive);
    }

    internal bool HasRoster(IEnumerable<int> ids)
    {
        lock (stateLock) return roster.SetEquals(ids);
    }

    internal void Watch(int playerId, MovementTrace trace)
    {
        lock (stateLock)
        {
            watchedPlayer = playerId;
            watchedTrace = trace;
        }
    }

    internal async Task ConnectAsync(IPAddress address)
    {
        long deadline = Stopwatch.GetTimestamp() + HandshakeTimeoutSeconds * Stopwatch.Frequency;
        lock (stateLock)
        {
            if (handshakeDeadline.HasValue)
                throw new InvalidOperationException("Mỗi SimulatedClient chỉ dùng cho một lần kết nối.");
            handshakeDeadline = deadline;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        Task deadlineTask = CancelAtHandshakeDeadlineAsync(deadline, timeout);
        try
        {
            await ConnectAttemptAsync(address, timeout.Token).ConfigureAwait(false);
        }
        finally
        {
            timeout.Cancel();
            await deadlineTask.ConfigureAwait(false);
        }
    }

    private static async Task CancelAtHandshakeDeadlineAsync(long deadline, CancellationTokenSource timeout)
    {
        try
        {
            await DelayUntilAsync(deadline, timeout.Token).ConfigureAwait(false);
            timeout.Cancel();
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
    }

    private async Task ConnectAttemptAsync(IPAddress address, CancellationToken token)
    {
        try
        {
            tcp = new TcpClient(AddressFamily.InterNetwork);
            await tcp.ConnectAsync(address, Constants.PORT, token).ConfigureAwait(false);
            udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Connect(address, Constants.UDP_PORT);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            ExpireHandshake("TCP connect quá hạn.");
            return;
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            Fail(ErrorKind.Connection, ex.Message);
            return;
        }

        lock (stateLock) connected = true;
        receiveTcp = ReceiveTcpAsync(lifetime.Token);
        receiveUdp = ReceiveUdpAsync(lifetime.Token);
        try
        {
            int slot = await welcome.Task.WaitAsync(token).ConfigureAwait(false);
            await SendTcpAsync(ClientPackets.welcomeReceived, packet =>
            {
                packet.Write(slot);
                packet.Write($"LoadClient_{slot}");
            }, token).ConfigureAwait(false);

            byte[] registration = BitConverter.GetBytes(slot);
            while (!udpChallenge.Task.IsCompleted)
            {
                await SendUdpAsync(registration, token).ConfigureAwait(false);
                try
                {
                    await udpChallenge.Task.WaitAsync(TimeSpan.FromMilliseconds(250), token).ConfigureAwait(false);
                }
                catch (TimeoutException) { /* Repeat registration until the handshake deadline. */ }
            }
            int challenge = await udpChallenge.Task.ConfigureAwait(false);
            await SendTcpAsync(ClientPackets.udpReady, packet => packet.Write(challenge), token).ConfigureAwait(false);
            await lobbyReady.Task.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            ExpireHandshake("Handshake TCP/UDP quá hạn.");
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            // The sending method has already recorded the failure.
        }
    }

    internal async Task SendTcpAsync(ClientPackets type, Action<Packet>? write, CancellationToken token)
    {
        // Commands are serialized by the caller for each client.
        using var packet = new Packet((int)type);
        write?.Invoke(packet);
        packet.WriteLength();
        packet.EncryptPayload();
        try
        {
            await tcp!.GetStream().WriteAsync(packet.ToArray(), token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or IOException or InvalidOperationException)
        {
            Fail(ErrorKind.Send, $"TCP {type}: {ex.Message}");
            throw new IOException($"Không gửi được TCP {type}.", ex);
        }
    }

    private async Task SendUdpAsync(byte[] data, CancellationToken token)
    {
        try
        {
            if (await udp!.SendAsync(data.AsMemory(), token).ConfigureAwait(false) != data.Length)
                throw new IOException("UDP send chưa hoàn tất datagram.");
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            if (!token.IsCancellationRequested) Fail(ErrorKind.Send, $"UDP: {ex.Message}");
            throw;
        }
    }

    private async Task ReceiveTcpAsync(CancellationToken token)
    {
        var frames = new TcpFrameReader();
        byte[] buffer = new byte[Constants.DATA_BUFFER_SIZE];
        try
        {
            var stream = tcp!.GetStream();
            while (!token.IsCancellationRequested)
            {
                int length = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
                if (length == 0) throw new EndOfStreamException("Server đã đóng TCP.");
                frames.Feed(buffer.AsSpan(0, length), data => HandlePacket(data, isUdp: false));
            }
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested) Fail(ErrorKind.Receive, $"TCP: {ex.Message}");
        }
    }

    private async Task ReceiveUdpAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var result = await udp!.ReceiveAsync(token).ConfigureAwait(false);
                HandlePacket(result.Buffer, isUdp: true);
            }
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested) Fail(ErrorKind.Receive, $"UDP: {ex.Message}");
        }
    }

    private void HandlePacket(byte[] data, bool isUdp)
    {
        using var packet = new Packet(data);
        packet.DecryptPayload();
        if (packet.ReadInt() != data.Length - sizeof(int))
            throw new InvalidDataException("Độ dài packet không khớp.");
        var type = (ServerPackets)packet.ReadInt();
        if (isUdp && type is not (ServerPackets.udpProbe or ServerPackets.playerPosition))
            throw new InvalidDataException($"Packet UDP không hợp lệ: {type}.");

        if (type == ServerPackets.lobbyReady)
        {
            FinishHandshake();
            return;
        }

        lock (stateLock)
        {
            switch (type)
            {
                case ServerPackets.welcome:
                    packet.ReadString();
                    id = packet.ReadInt();
                    if (id <= 0 || !welcome.TrySetResult(id))
                        throw new InvalidDataException("Server trả về welcome không hợp lệ.");
                    break;
                case ServerPackets.udpProbe:
                    udpChallenge.TrySetResult(packet.ReadInt());
                    break;
                case ServerPackets.roomChanged:
                    roomId = packet.ReadInt();
                    phase = MatchPhase.Waiting;
                    readyInRoom = 0;
                    ResetRound(0);
                    break;
                case ServerPackets.roomError:
                    throw new InvalidDataException($"Server từ chối thao tác phòng: {(RoomError)packet.ReadInt()}.");
                case ServerPackets.roundBegin:
                    ReadRoundBegin(packet);
                    break;
                case ServerPackets.matchState:
                    ReadMatchState(packet);
                    break;
                case ServerPackets.mapData:
                    if (IsCurrentRound(packet)) ReadMap(packet);
                    break;
                case ServerPackets.spawnPlayer:
                    if (IsCurrentRound(packet))
                    {
                        int player = packet.ReadInt();
                        Position position = ReadPosition(packet);
                        if (roster.Contains(player)) spawns[player] = position;
                    }
                    break;
                case ServerPackets.playerPosition:
                    if (IsCurrentRound(packet)) ReadPlayerPosition(packet);
                    break;
                case ServerPackets.playerEliminated:
                case ServerPackets.playerDisconnected:
                    if (IsCurrentRound(packet))
                    {
                        int player = packet.ReadInt();
                        if (player == id) alive = false;
                        if (type == ServerPackets.playerDisconnected) roster.Remove(player);
                    }
                    break;
                case ServerPackets.bombPlaced:
                    if (IsCurrentRound(packet)) bombs.Add(ReadCell(packet));
                    break;
                case ServerPackets.bombExploded:
                    if (IsCurrentRound(packet)) ReadExplosion(packet);
                    break;
                default:
                    throw new InvalidDataException($"Packet server không được nhận diện: {type}.");
            }
        }
    }

    private bool IsCurrentRound(Packet packet) =>
        packet.ReadInt() == roomId && roomId > 0 && packet.ReadInt() == roundId && roundId > 0;

    private void ResetRound(int newRound)
    {
        roundId = newRound;
        alive = false;
        corrected = false;
        map = null;
        roster.Clear();
        spawns.Clear();
        bombs.Clear();
    }

    private void ReadRoundBegin(Packet packet)
    {
        if (packet.ReadInt() != roomId || roomId <= 0) return;
        int incomingRound = packet.ReadInt();
        if (incomingRound <= roundId) return;
        int count = packet.ReadInt();
        if (count is < 2 or > Constants.MAX_PLAYERS || count > packet.UnreadLength() / sizeof(int))
            throw new InvalidDataException("Số người trong roster không hợp lệ.");
        ResetRound(incomingRound);
        for (int i = 0; i < count; i++)
            if (!roster.Add(packet.ReadInt())) throw new InvalidDataException("Roster có client trùng.");
        alive = roster.Contains(id);
    }

    private void ReadMatchState(Packet packet)
    {
        if (packet.ReadInt() != roomId || roomId <= 0) return;
        int incomingRound = packet.ReadInt();
        if (incomingRound < roundId) return;
        if (incomingRound > roundId) ResetRound(incomingRound);
        phase = (MatchPhase)packet.ReadInt();
        if (phase is not (MatchPhase.Waiting or MatchPhase.Playing or MatchPhase.Finished))
            throw new InvalidDataException("MatchPhase không hợp lệ.");
        readyInRoom = packet.ReadInt();
        packet.ReadInt(); // Winner id.
    }

    private void ReadMap(Packet packet)
    {
        var terrain = new int[Constants.MAP_WIDTH, Constants.MAP_HEIGHT];
        for (int col = 0; col < Constants.MAP_WIDTH; col++)
        for (int row = 0; row < Constants.MAP_HEIGHT; row++)
            terrain[col, row] = packet.ReadInt();
        map = terrain;
    }

    private void ReadPlayerPosition(Packet packet)
    {
        int player = packet.ReadInt();
        Position position = ReadPosition(packet);
        if (phase != MatchPhase.Playing || !roster.Contains(player)) return;
        if (player == id) corrected = true; // This is the server's TCP correction, not a peer observation.
        else if (player == watchedPlayer) watchedTrace?.Observe(id, position);
    }

    private static Position ReadPosition(Packet packet)
    {
        var position = new Position(packet.ReadFloat(), packet.ReadFloat());
        return position.IsFinite ? position : throw new InvalidDataException("Tọa độ server không hữu hạn.");
    }

    private static (int Col, int Row) ReadCell(Packet packet)
    {
        int col = packet.ReadInt(), row = packet.ReadInt();
        if (col < 0 || col >= Constants.MAP_WIDTH || row < 0 || row >= Constants.MAP_HEIGHT)
            throw new InvalidDataException("Ô bản đồ nằm ngoài giới hạn.");
        return (col, row);
    }

    private void ReadExplosion(Packet packet)
    {
        bombs.Remove(ReadCell(packet));
        int blast = ReadCellCount(packet);
        for (int i = 0; i < blast; i++) ReadCell(packet);
        int destroyed = ReadCellCount(packet);
        for (int i = 0; i < destroyed; i++)
        {
            var (col, row) = ReadCell(packet);
            if (map != null) map[col, row] = 0;
        }
    }

    private static int ReadCellCount(Packet packet)
    {
        int count = packet.ReadInt();
        if (count < 0 || count > Constants.MAP_WIDTH * Constants.MAP_HEIGHT ||
            count > packet.UnreadLength() / (2 * sizeof(int)))
            throw new InvalidDataException("Số ô của vụ nổ không hợp lệ.");
        return count;
    }

    internal bool WasCorrected
    {
        get { lock (stateLock) return corrected; }
    }

    internal async Task MoveAsync(ClientState round, WorkloadWindow window, int hz, CancellationToken stop)
    {
        using var run = CancellationTokenSource.CreateLinkedTokenSource(stop, lifetime.Token);
        CancellationToken token = run.Token;
        Position position;
        MovementPath path;
        long period = (Stopwatch.Frequency + hz - 1) / hz;
        int steps = window.TotalSeconds * hz;
        long start = window.StartsAt;
        long end = window.EndsAt;
        long lastWriteCompleted = 0;
        int step = 0;
        try
        {
            lock (stateLock)
            {
                position = spawns[id];
                path = new MovementPath(position, map!);
            }
            for (long slot = 0; slot < steps;)
            {
                Position target = path.Target(++step);
                float distance = position.DistanceTo(target);
                long travelTicks = (long)Math.Ceiling(distance / (double)MovementPath.Speed * Stopwatch.Frequency);
                long earliest = Math.Max(start + slot * period, lastWriteCompleted + travelTicks);
                if (earliest >= end) break;
                await DelayUntilAsync(earliest, token).ConfigureAwait(false);
                long now = Stopwatch.GetTimestamp();
                if (now >= end) break;
                lock (stateLock)
                {
                    if (!connected || !alive || corrected || roomId != round.RoomId ||
                        roundId != round.RoundId || phase != MatchPhase.Playing)
                        throw new InvalidDataException("Phiên/ván thay đổi hoặc server đã chỉnh vị trí.");
                    if (distance <= 0 || distance > MovementPath.MaxStep ||
                        !TerrainCollision.CanStandAt(map!, target.X, target.Y) ||
                        bombs.Any(cell => BombCollision.BlocksStep(cell.Col, cell.Row,
                            position.X, position.Y, target.X, target.Y)))
                        throw new InvalidDataException("Bước di chuyển không hợp lệ.");
                }

                using var packet = new Packet((int)ClientPackets.playerMovement);
                packet.Write(round.RoomId);
                packet.Write(round.RoundId);
                packet.Write(target.X);
                packet.Write(target.Y);
                packet.WriteLength();
                packet.EncryptPayload();
                packet.InsertInt(round.Id);
                byte[] data = packet.ToArray();
                long sendAt = Stopwatch.GetTimestamp();
                if (sendAt >= end) break;
                Trace.Prepare(position, target, window.ContainsMeasurement(sendAt));
                // Use the latest due slot. Never emit a burst to make up missed slots.
                slot = (sendAt - start) / period;
                try { await SendUdpAsync(data, token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is SocketException or IOException) { return; }
                lastWriteCompleted = Stopwatch.GetTimestamp();
                Trace.MarkSent(target, Stopwatch.GetElapsedTime(start + slot * period, sendAt).TotalMilliseconds);
                position = target;
                slot++;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Fail(ErrorKind.Workload, ex.Message);
        }
    }

    private static async Task DelayUntilAsync(long due, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            TimeSpan remaining = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), due);
            if (remaining <= TimeSpan.Zero) return;
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, remaining.TotalMilliseconds)), token)
                .ConfigureAwait(false);
        }
    }

    private void FinishHandshake()
    {
        lock (stateLock)
        {
            if (!connected || ready || handshakeTimedOut || lifetime.IsCancellationRequested) return;

            if (Stopwatch.GetTimestamp() < handshakeDeadline!.Value)
            {
                ready = true;
                lobbyReady.TrySetResult();
                return;
            }
        }

        ExpireHandshake("Handshake TCP/UDP quá hạn.");
    }

    private void ExpireHandshake(string message)
    {
        lock (stateLock)
        {
            if (ready || handshakeTimedOut || lifetime.IsCancellationRequested) return;
            handshakeTimedOut = true;
            connected = false;
            errors.Add(ErrorKind.Timeout, $"Client {index}: {message}");
        }

        lifetime.Cancel();
        tcp?.Dispose();
        udp?.Dispose();
    }

    private void Fail(ErrorKind kind, string message)
    {
        lock (stateLock)
        {
            if (handshakeTimedOut) return;
            errors.Add(kind, $"Client {index}: {message}");
            connected = ready = false;
        }
        lifetime.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        tcp?.Dispose();
        udp?.Dispose();
        await Task.WhenAll(receiveTcp, receiveUdp).ConfigureAwait(false);
        lifetime.Dispose();
    }
}
