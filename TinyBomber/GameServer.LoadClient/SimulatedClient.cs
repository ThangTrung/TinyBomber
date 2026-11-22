using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using GameShared;

namespace GameServer.LoadClient;

internal readonly record struct ClientState(
    bool Connected, bool Ready, int Id, int RoomId, int RoundId, MatchPhase Phase,
    int ReadyInRoom, bool HasRoundData, bool Alive);

internal readonly record struct UdpHealthStats(
    long ProbesSent, long EchoesAccepted, long ProbesExpired, long Degradations, long Recoveries,
    long SendErrors, long ReceiveErrors, bool IsDegraded, bool SocketUnavailable);

internal sealed class SimulatedClient : IAsyncDisposable
{
    private const int HandshakeTimeoutSeconds = 16;
    private readonly object stateLock = new();
    private readonly SemaphoreSlim tcpWriteGate = new(1, 1);
    private readonly HeartbeatWatchdog heartbeat = new();
    private readonly UdpHealthMonitor udpHealth = new();
    private readonly Func<UdpClient, CancellationToken, ValueTask<UdpReceiveResult>> receiveDatagram;
    private readonly CancellationTokenSource lifetime;
    private readonly RunErrors errors;
    private readonly int index;
    private readonly TaskCompletionSource<int> serverHello = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<byte[]> sessionGranted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<byte[]> udpProbe = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource lobbyReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly HashSet<int> roster = new();
    private readonly Dictionary<int, Position> spawns = new();
    private readonly Dictionary<int, long> peerMovementSequences = new();
    private readonly HashSet<(int Col, int Row)> bombs = new();
    private TcpClient? tcp;
    private UdpClient? udp;
    private Task receiveTcp = Task.CompletedTask;
    private Task receiveUdp = Task.CompletedTask;
    private Task heartbeatMonitor = Task.CompletedTask;
    private Task udpHealthMonitor = Task.CompletedTask;
    private long? handshakeDeadline;
    private bool ended;
    private bool clientHelloSent;
    private bool udpConfirmSent;
    private byte[]? sessionToken;
    private TaskCompletionSource<bool>? udpHealthResponse;
    private long udpSendErrors;
    private long udpReceiveErrors;
    private bool connected;
    private bool ready;
    private bool alive;
    private bool corrected;
    private volatile int acceptedProtocolVersion;
    private long lastSentMoveSeq;
    private long lastCorrectionMoveSeq;
    private int id;
    private int roomId;
    private int roundId;
    private int readyInRoom;
    private MatchPhase phase;
    private int[,]? map;
    private int watchedPlayer;
    private MovementTrace? watchedTrace;

    internal MovementTrace Trace { get; } = new();
    private bool UsesMovementOrdering => acceptedProtocolVersion == ProtocolV4.Version;

    internal SimulatedClient(int index, RunErrors errors, CancellationToken stop,
        Func<UdpClient, CancellationToken, ValueTask<UdpReceiveResult>>? receiveDatagram = null)
    {
        this.index = index;
        this.errors = errors;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
        this.receiveDatagram = receiveDatagram ?? ((socket, token) => socket.ReceiveAsync(token));
    }

    internal ClientState ReadState()
    {
        lock (stateLock)
            return new ClientState(connected, ready, id, roomId, roundId, phase, readyInRoom,
                map != null && roster.Contains(id) && roster.SetEquals(spawns.Keys), alive);
    }

    internal UdpHealthStats ReadUdpHealth()
    {
        lock (stateLock)
            return new UdpHealthStats(udpHealth.ProbesSent, udpHealth.EchoesAccepted, udpHealth.ProbesExpired,
                udpHealth.Degradations, udpHealth.Recoveries, udpSendErrors, udpReceiveErrors,
                udpHealth.IsDegraded, udpHealth.SocketUnavailable);
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
        Task deadlineTask = CancelAtDeadlineAsync(deadline, timeout);
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

    private static async Task CancelAtDeadlineAsync(long deadline, CancellationTokenSource timeout)
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
        try
        {
            int slot = await serverHello.Task.WaitAsync(token).ConfigureAwait(false);
            using var hello = ProtocolV4.CreateClientHello(slot, $"LoadClient_{slot}");
            lock (stateLock) clientHelloSent = true;
            await SendTcpFrameAsync(hello, ClientPackets.clientHello, token).ConfigureAwait(false);

            byte[] grantedToken = await sessionGranted.Task.WaitAsync(token).ConfigureAwait(false);
            var udpSocket = new UdpClient(AddressFamily.InterNetwork);
            try { udpSocket.Connect(address, Constants.UDP_PORT); }
            catch { udpSocket.Dispose(); throw; }

            lock (stateLock)
            {
                if (!ended && !token.IsCancellationRequested) udp = udpSocket;
            }
            if (udp == null)
            {
                udpSocket.Dispose();
                token.ThrowIfCancellationRequested();
                return;
            }
            receiveUdp = ReceiveUdpAsync(lifetime.Token);

            while (!udpProbe.Task.IsCompleted)
            {
                using var registration = ProtocolV2.CreateUdpRegistration(slot, grantedToken);
                await SendUdpAsync(registration.ToArray(), token).ConfigureAwait(false);
                try
                {
                    await udpProbe.Task.WaitAsync(TimeSpan.FromMilliseconds(250), token).ConfigureAwait(false);
                }
                catch (TimeoutException) { /* Repeat registration until the handshake deadline. */ }
            }
            byte[] nonce = await udpProbe.Task.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            using var confirm = ProtocolV2.CreateUdpConfirm(grantedToken, nonce);
            lock (stateLock) udpConfirmSent = true;
            await SendTcpFrameAsync(confirm, ClientPackets.udpConfirm, token).ConfigureAwait(false);
            await lobbyReady.Task.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            ExpireHandshake("Handshake TCP/UDP quá hạn.");
        }
        catch (SocketException ex)
        {
            if (!token.IsCancellationRequested) Fail(ErrorKind.Connection, $"UDP setup: {ex.Message}");
        }
        catch (IOException)
        {
            // The sending method has already recorded the failure.
        }
    }

    internal async Task SendTcpAsync(ClientPackets type, Action<Packet>? write, CancellationToken token)
    {
        using var packet = new Packet((int)type);
        write?.Invoke(packet);
        packet.WriteLength();
        packet.EncryptPayload();
        await SendTcpFrameAsync(packet, type, token).ConfigureAwait(false);
    }

    private async Task SendTcpFrameAsync(Packet packet, ClientPackets type, CancellationToken token)
    {
        byte[] frame = packet.ToArray();
        using var send = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        await tcpWriteGate.WaitAsync(send.Token).ConfigureAwait(false);
        try
        {
            send.Token.ThrowIfCancellationRequested();
            try
            {
                await tcp!.GetStream().WriteAsync(frame, send.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (send.IsCancellationRequested)
            {
                // A canceled write may have put part of this frame on the stream.
                Fail(ErrorKind.Send, $"Đã hủy khi đang ghi TCP {type}.");
                throw;
            }
            catch (Exception ex) when (ex is SocketException or IOException or InvalidOperationException)
            {
                Fail(ErrorKind.Send, $"TCP {type}: {ex.Message}");
                throw new IOException($"Không gửi được TCP {type}.", ex);
            }
        }
        finally { tcpWriteGate.Release(); }
    }

    private async Task SendUdpAsync(byte[] data, CancellationToken token)
    {
        try
        {
            if (await udp!.SendAsync(data.AsMemory(), token).ConfigureAwait(false) != data.Length)
                throw new IOException("UDP send chưa hoàn tất datagram.");
        }
        catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException)
        {
            if (!token.IsCancellationRequested && RecordUdpError(ex, sending: true))
                Fail(ErrorKind.Send, $"UDP: {ex.Message}");
            throw new IOException("Không gửi được UDP datagram.", ex);
        }
    }

    internal async Task<bool> ProbeUdpHealthAsync(CancellationToken token)
    {
        using var request = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        var response = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await SendHealthProbeAsync(scheduled: false, response, request.Token).ConfigureAwait(false);
            return await response.Task.WaitAsync(request.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { return false; }
    }

    private async Task SendHealthProbeAsync(bool scheduled, TaskCompletionSource<bool>? response, CancellationToken token)
    {
        UdpClient socket;
        byte[] session;
        long sequence;
        long deadline;
        lock (stateLock)
        {
            long now = Stopwatch.GetTimestamp();
            AdvanceUdpHealth(now);
            long? started = !ended && !token.IsCancellationRequested && ready && sessionToken != null && udp != null
                ? udpHealth.BeginProbe(now, scheduled) : null;
            if (started is not long pending)
            {
                response?.TrySetResult(false);
                return;
            }

            socket = udp!;
            session = sessionToken!;
            sequence = pending;
            deadline = udpHealth.ProbeDeadline!.Value;
            udpHealthResponse = response;
        }

        using var request = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        Task requestTimeout = CancelAtDeadlineAsync(deadline, request);
        bool sent = false;
        try
        {
            using var probe = ProtocolV3.CreateUdpHealthProbe(id, session, sequence);
            byte[] data = probe.ToArray();
            sent = await socket.SendAsync(data.AsMemory(), request.Token).ConfigureAwait(false) == data.Length;
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            if (!request.IsCancellationRequested) RecordUdpError(ex, sending: true);
        }
        finally
        {
            request.Cancel();
            await requestTimeout.ConfigureAwait(false);
            lock (stateLock)
            {
                AdvanceUdpHealth(Stopwatch.GetTimestamp());
                if (sent) udpHealth.RecordProbeSent();
                else if (udpHealth.AbandonProbe(sequence)) CompleteUdpHealthProbe(false);
            }
        }
    }

    // The monitor owns the request identity and deadline. The optional waiter observes that same request.
    private void AdvanceUdpHealth(long now)
    {
        bool pending = udpHealth.PendingSequence.HasValue;
        udpHealth.Advance(now);
        if (pending && !udpHealth.PendingSequence.HasValue) CompleteUdpHealthProbe(false);
    }

    private void CompleteUdpHealthProbe(bool success)
    {
        udpHealthResponse?.TrySetResult(success);
        udpHealthResponse = null;
    }

    private bool RecordUdpError(Exception error, bool sending)
    {
        UdpClient? unavailableSocket = null;
        bool duringHandshake;
        lock (stateLock)
        {
            if (ended || lifetime.IsCancellationRequested) return false;
            if (sending) udpSendErrors++;
            else udpReceiveErrors++;

            var socketError = (error as SocketException)?.SocketErrorCode;
            bool unavailable = sending
                ? error is ObjectDisposedException || socketError == SocketError.NotSocket
                : socketError is not SocketError code || !IsRecoverableReceiveError(code);
            if (unavailable)
            {
                udpHealth.MarkSocketUnavailable();
                CompleteUdpHealthProbe(false);
                unavailableSocket = udp;
            }
            duringHandshake = !ready;
        }
        unavailableSocket?.Dispose();
        return duringHandshake;
    }

    private static bool IsRecoverableReceiveError(SocketError error) => error is
        SocketError.ConnectionReset or SocketError.ConnectionRefused or SocketError.TimedOut or
        SocketError.Interrupted or SocketError.NetworkDown or SocketError.NetworkReset or SocketError.NetworkUnreachable or
        SocketError.HostUnreachable or SocketError.NoBufferSpaceAvailable or SocketError.WouldBlock or SocketError.MessageSize;

    private async Task ReceiveTcpAsync(CancellationToken token)
    {
        var frames = new TcpFrameReader();
        byte[] buffer = new byte[Constants.DATA_BUFFER_SIZE];
        var received = new List<byte[]>();
        Action<byte[]> collectFrame = received.Add;
        try
        {
            var stream = tcp!.GetStream();
            while (!token.IsCancellationRequested)
            {
                int length = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
                if (length == 0) throw new EndOfStreamException("Server đã đóng TCP.");
                frames.Feed(buffer.AsSpan(0, length), collectFrame);
                foreach (byte[] frame in received)
                {
                    if (token.IsCancellationRequested) return;
                    if (HandleTcpPacket(frame) is not long sequence) continue;
                    using var pong = ProtocolV3.CreateHeartbeatPong(sequence);
                    await SendTcpFrameAsync(pong, ClientPackets.heartbeatPong, token).ConfigureAwait(false);
                }
                received.Clear();
            }
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested) Fail(ErrorKind.Receive, $"TCP: {ex.Message}");
        }
    }

    private async Task ReceiveUdpAsync(CancellationToken token)
    {
        UdpClient socket = udp!;
        try
        {
            while (!token.IsCancellationRequested)
            {
                lock (stateLock)
                {
                    if (ended || udp != socket || udpHealth.SocketUnavailable) return;
                }
                UdpReceiveResult result;
                try { result = await receiveDatagram(socket, token).ConfigureAwait(false); }
                catch (SocketException ex) when (IsRecoverableReceiveError(ex.SocketErrorCode))
                {
                    RecordUdpError(ex, sending: false);
                    await DelayUntilAsync(Stopwatch.GetTimestamp() + Stopwatch.Frequency, token).ConfigureAwait(false);
                    continue;
                }
                try
                {
                    HandleUdpPacket(result.Buffer);
                }
                catch (InvalidDataException) { /* Discard just this datagram. */ }
            }
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested && RecordUdpError(ex, sending: false))
                Fail(ErrorKind.Receive, $"UDP: {ex.Message}");
        }
    }

    private long? HandleTcpPacket(byte[] data)
    {
        using var packet = new Packet(data);
        packet.DecryptPayload();
        if (packet.ReadInt() != data.Length - sizeof(int))
            throw new InvalidDataException("Độ dài packet không khớp.");
        var type = (ServerPackets)packet.ReadInt(false);

        switch (type)
        {
            case ServerPackets.serverHello:
                AcceptServerHello(ProtocolV2.ReadServerHello(packet));
                return null;
            case ServerPackets.sessionGranted:
                byte[] granted = ProtocolV2.ReadSessionGranted(packet);
                lock (stateLock)
                {
                    if (!connected || lifetime.IsCancellationRequested) return null;
                    if (!clientHelloSent || sessionToken != null)
                        throw new InvalidDataException("SessionGranted đến sai giai đoạn.");
                    if (HandshakeHasExpired()) return null;
                    ResetMovementOrdering();
                    sessionToken = granted;
                    sessionGranted.TrySetResult(granted);
                }
                return null;
            case ServerPackets.lobbyReady:
                ProtocolV2.ReadLobbyReady(packet);
                FinishHandshake();
                return null;
            case ServerPackets.heartbeatPing:
                return HandleHeartbeatPing(ProtocolV3.ReadHeartbeatPing(packet));
            case ServerPackets.welcome when !serverHello.Task.IsCompleted:
                packet.ReadInt();
                packet.ReadString();
                if (packet.UnreadLength() != sizeof(int) || packet.ReadInt() <= 0)
                    throw new InvalidDataException("Welcome v1 không hợp lệ.");
                RejectProtocolVersion(1);
                return null;
        }

        lock (stateLock)
        {
            if (ended || lifetime.IsCancellationRequested) return null;
            if (!ready) throw new InvalidDataException("TCP packet đến sai giai đoạn bắt tay.");

            if (UsesMovementOrdering && type == ServerPackets.playerPositionV4)
            {
                var update = ProtocolV4.ReadTcpPlayerPosition(packet);
                ApplyOrderedPeerPosition(update.RoomId, update.RoundId, update.PlayerId, update.AcceptedMoveSeq,
                    new Position(update.X, update.Y));
                return null;
            }
            if (UsesMovementOrdering && type == ServerPackets.playerCorrectionV4)
            {
                var correction = ProtocolV4.ReadTcpPlayerCorrection(packet);
                if (IsPlayingRound(correction.RoomId, correction.RoundId) && correction.PlayerId == id && alive &&
                    spawns.ContainsKey(id) && correction.ProcessedMoveSeq > lastCorrectionMoveSeq)
                {
                    var position = new Position(correction.X, correction.Y);
                    if (!position.IsFinite) throw new InvalidDataException("Tọa độ server không hữu hạn.");
                    lastCorrectionMoveSeq = correction.ProcessedMoveSeq;
                    // A correction still invalidates the workload, just as a v3 TCP snap does.
                    corrected = true;
                }
                return null;
            }

            packet.ReadInt();
            switch (type)
            {
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
                    // Legacy movement is ignored in a v4 session.
                    break;
                case ServerPackets.playerEliminated:
                case ServerPackets.playerDisconnected:
                    if (IsCurrentRound(packet))
                    {
                        int player = packet.ReadInt();
                        if (player == id) alive = false;
                        if (type == ServerPackets.playerDisconnected) roster.Remove(player);
                        spawns.Remove(player);
                        peerMovementSequences.Remove(player);
                        if (player == id) ResetLocalMovementOrdering();
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
        return null;
    }

    private long? HandleHeartbeatPing(long sequence)
    {
        lock (stateLock)
        {
            if (ended || lifetime.IsCancellationRequested) return null;
            if (!ready) throw new InvalidDataException("Heartbeat đến trước lobbyReady.");

            long now = Stopwatch.GetTimestamp();
            if (!heartbeat.HasExpired(now)) return heartbeat.ShouldReply(sequence, now) ? sequence : null;
        }

        Fail(ErrorKind.Timeout, "Máy chủ không phản hồi heartbeat.");
        return null;
    }

    private void AcceptServerHello((int Version, int SlotId) hello)
    {
        if (hello.SlotId <= 0 || serverHello.Task.IsCompleted)
            throw new InvalidDataException("ServerHello không hợp lệ.");
        if (HandshakeHasExpired()) return;
        if (hello.Version != ProtocolV4.Version)
        {
            RejectProtocolVersion(hello.Version);
            return;
        }

        lock (stateLock)
        {
            if (!connected || lifetime.IsCancellationRequested || HandshakeHasExpired()) return;
            id = hello.SlotId;
            acceptedProtocolVersion = hello.Version;
            serverHello.TrySetResult(id);
        }
    }

    private void RejectProtocolVersion(int version)
    {
        Fail(ErrorKind.Connection, $"Máy chủ dùng protocol v{version}. Client yêu cầu v{ProtocolV4.Version}.");
    }

    private void HandleUdpPacket(byte[] data)
    {
        using Packet packet = ProtocolV4.OpenServerDatagram(data);
        if (packet.UnreadLength() < sizeof(int)) return;
        switch ((ServerPackets)packet.ReadInt(false))
        {
            case ServerPackets.udpProbeV2:
                var probe = ProtocolV2.ReadUdpProbe(packet);
                lock (stateLock)
                {
                    if (connected && !ready && !HandshakeHasExpired() && sessionToken is byte[] token &&
                        probe.Token.AsSpan().SequenceEqual(token))
                        udpProbe.TrySetResult(probe.Nonce);
                }
                break;
            case ServerPackets.udpPlayerPositionV4 when UsesMovementOrdering:
                var ordered = ProtocolV4.ReadUdpPlayerPosition(packet);
                lock (stateLock)
                {
                    if (!connected || !ready || sessionToken is not byte[] token ||
                        !ordered.Token.AsSpan().SequenceEqual(token)) return;
                    ApplyOrderedPeerPosition(ordered.RoomId, ordered.RoundId, ordered.PlayerId, ordered.AcceptedMoveSeq,
                        new Position(ordered.X, ordered.Y));
                }
                break;
            case ServerPackets.udpHealthEcho:
                var echo = ProtocolV3.ReadUdpHealthEcho(packet);
                lock (stateLock)
                {
                    if (ready && !ended && sessionToken is byte[] token && echo.Token.AsSpan().SequenceEqual(token) &&
                        udpHealth.AcceptEcho(echo.Sequence, Stopwatch.GetTimestamp()))
                        CompleteUdpHealthProbe(true);
                }
                break;
        }
    }

    private bool IsCurrentRound(Packet packet) =>
        packet.ReadInt() == roomId && roomId > 0 && packet.ReadInt() == roundId && roundId > 0;

    private bool IsPlayingRound(int room, int round) =>
        phase == MatchPhase.Playing && room > 0 && room == roomId && round > 0 && round == roundId;

    private void ResetLocalMovementOrdering()
    {
        lastSentMoveSeq = 0;
        lastCorrectionMoveSeq = 0;
    }

    private void ResetMovementOrdering()
    {
        ResetLocalMovementOrdering();
        peerMovementSequences.Clear();
    }

    private void ResetRound(int newRound)
    {
        ResetMovementOrdering();
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
        if (phase == MatchPhase.Finished) ResetMovementOrdering();
    }

    private void ReadMap(Packet packet)
    {
        var terrain = new int[Constants.MAP_WIDTH, Constants.MAP_HEIGHT];
        for (int col = 0; col < Constants.MAP_WIDTH; col++)
        for (int row = 0; row < Constants.MAP_HEIGHT; row++)
            terrain[col, row] = packet.ReadInt();
        map = terrain;
    }

    private void ApplyOrderedPeerPosition(int room, int round, int player, long sequence, Position position)
    {
        // The workload starts only once round data and spawns have arrived.
        if (!IsPlayingRound(room, round) || player == id || !spawns.ContainsKey(player)) return;
        if (!position.IsFinite) throw new InvalidDataException("Tọa độ server không hữu hạn.");
        if (peerMovementSequences.TryGetValue(player, out long previous) && sequence <= previous) return;
        peerMovementSequences[player] = sequence;
        if (player == watchedPlayer) watchedTrace?.Observe(id, position);
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

    internal async Task MoveAsync(ClientState round, WorkloadWindow window, int hz, CancellationToken stop,
        bool viaUdp = true)
    {
        using var run = CancellationTokenSource.CreateLinkedTokenSource(stop, lifetime.Token);
        CancellationToken token = run.Token;
        Position position;
        MovementPath path;
        byte[] grantedToken;
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
                grantedToken = sessionToken ?? throw new InvalidDataException("Phiên chưa được cấp token.");
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
                long moveSeq;
                lock (stateLock)
                {
                    if (!connected || !UsesMovementOrdering || !alive || corrected || roomId != round.RoomId ||
                        roundId != round.RoundId || phase != MatchPhase.Playing)
                        throw new InvalidDataException("Phiên/ván thay đổi hoặc server đã chỉnh vị trí.");
                    if (distance <= 0 || distance > MovementPath.MaxStep ||
                        !TerrainCollision.CanStandAt(map!, target.X, target.Y) ||
                        bombs.Any(cell => BombCollision.BlocksStep(cell.Col, cell.Row,
                            position.X, position.Y, target.X, target.Y)))
                        throw new InvalidDataException("Bước di chuyển không hợp lệ.");
                    if (!ProtocolV4.TryGetNextMovementSequence(lastSentMoveSeq, out moveSeq))
                        throw new InvalidDataException("Đã hết sequence di chuyển của ván.");
                }

                using var packet = CreateMovementPacket(round, grantedToken, target, moveSeq, viaUdp);
                byte[]? data = viaUdp ? packet.ToArray() : null;
                long sendAt = Stopwatch.GetTimestamp();
                if (sendAt >= end) break;
                Trace.Prepare(position, target, window.ContainsMeasurement(sendAt));
                // Use the latest due slot. Never emit a burst to make up missed slots.
                slot = (sendAt - start) / period;
                try
                {
                    if (viaUdp) await SendUdpAsync(data!, token).ConfigureAwait(false);
                    else await SendTcpFrameAsync(packet, ClientPackets.playerMovementV4, token).ConfigureAwait(false);
                }
                catch (IOException ex)
                {
                    if (!token.IsCancellationRequested) errors.Add(ErrorKind.Send, $"Client {index}: {ex.Message}");
                    return;
                }
                lastWriteCompleted = Stopwatch.GetTimestamp();
                lock (stateLock)
                {
                    if (connected && alive && IsPlayingRound(round.RoomId, round.RoundId) &&
                        ReferenceEquals(sessionToken, grantedToken)) lastSentMoveSeq = moveSeq;
                }
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

    private static Packet CreateMovementPacket(ClientState round, byte[] token, Position target, long sequence,
        bool viaUdp)
    {
        // The transport only chooses the wrapper. A caller retrying a move keeps the same sequence.
        return viaUdp
            ? ProtocolV4.CreateUdpPlayerMovement(round.Id, token, round.RoomId, round.RoundId, sequence, target.X, target.Y)
            : ProtocolV4.CreateTcpPlayerMovement(round.RoomId, round.RoundId, sequence, target.X, target.Y);
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

    private bool HandshakeHasExpired() => Stopwatch.GetTimestamp() >= handshakeDeadline!.Value;

    private void FinishHandshake()
    {
        bool expired;
        lock (stateLock)
        {
            if (!connected || ready || ended || lifetime.IsCancellationRequested) return;
            if (!udpConfirmSent)
                throw new InvalidDataException("lobbyReady đến trước khi xác nhận UDP.");

            long now = Stopwatch.GetTimestamp();
            expired = now >= handshakeDeadline!.Value;
            if (!expired)
            {
                ready = true;
                heartbeat.Start(now);
                udpHealth.Start(now);
            }
        }

        if (!expired)
        {
            heartbeatMonitor = WatchHeartbeatAsync(lifetime.Token);
            udpHealthMonitor = WatchUdpHealthAsync(lifetime.Token);
            lobbyReady.TrySetResult();
        }
        else ExpireHandshake("Handshake TCP/UDP quá hạn.");
    }

    private async Task WatchUdpHealthAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                long now = Stopwatch.GetTimestamp();
                long? next;
                lock (stateLock)
                {
                    if (ended) return;
                    AdvanceUdpHealth(now);
                    next = udpHealth.NextCheckAt;
                }
                if (next is not long due) return;
                if (now < due) await DelayUntilAsync(due, token).ConfigureAwait(false);
                else await SendHealthProbeAsync(scheduled: true, response: null, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task WatchHeartbeatAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                long deadline;
                lock (stateLock)
                {
                    if (ended) return;
                    deadline = heartbeat.Deadline!.Value;
                }
                await DelayUntilAsync(deadline, token).ConfigureAwait(false);
                lock (stateLock)
                {
                    if (!heartbeat.HasExpired(Stopwatch.GetTimestamp())) continue;
                }
                Fail(ErrorKind.Timeout, "Máy chủ không phản hồi heartbeat.");
                return;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private void ExpireHandshake(string message)
    {
        lock (stateLock)
        {
            if (ready || ended || lifetime.IsCancellationRequested) return;
            ended = true;
            connected = false;
            ResetMovementOrdering();
            errors.Add(ErrorKind.Timeout, $"Client {index}: {message}");
        }

        CloseTransport();
    }

    private void Fail(ErrorKind kind, string message)
    {
        lock (stateLock)
        {
            if (ended) return;
            ended = true;
            if (!lifetime.IsCancellationRequested) errors.Add(kind, $"Client {index}: {message}");
            connected = ready = false;
            ResetMovementOrdering();
        }
        CloseTransport();
    }

    private void CloseTransport()
    {
        lifetime.Cancel();
        tcp?.Dispose();
        udp?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        lock (stateLock)
        {
            ended = true;
            connected = ready = false;
            ResetMovementOrdering();
        }
        CloseTransport();
        await Task.WhenAll(receiveTcp, receiveUdp).ConfigureAwait(false);
        await Task.WhenAll(heartbeatMonitor, udpHealthMonitor).ConfigureAwait(false);
        tcpWriteGate.Dispose();
        lifetime.Dispose();
    }
}
