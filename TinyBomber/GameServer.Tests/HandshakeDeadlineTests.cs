using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using GameShared;

namespace GameServer.Tests;

[Collection("Network metrics server state")]
public sealed class HandshakeDeadlineTests
{
    private const long AcceptedAt = 100;
    private static long Deadline => AcceptedAt + 20 * Stopwatch.Frequency;

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    public void ClientHelloOnlyGrantsSessionBeforeDeadline(int offset, bool accepted)
    {
        var slot = new Client(1);
        slot.tcp.StartHandshake(AcceptedAt);

        ServerHandle.ClientHello(slot, slot.tcp, ProtocolV4.Version, slot.id, "Player", Deadline + offset);

        Assert.Equal(accepted ? "Player" : null, slot.Username);
        Assert.Equal(accepted, slot.tcp.SessionToken?.Length == 16);
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    public void RegistrationOnlyBindsBeforeDeadline(int offset, bool accepted)
    {
        var slot = new Client(1);
        slot.tcp.StartHandshake(AcceptedAt);
        ServerHandle.ClientHello(slot, slot.tcp, ProtocolV4.Version, slot.id, "Player", AcceptedAt);
        var endpoint = new IPEndPoint(IPAddress.Loopback, 12345);

        Assert.Equal(accepted, ServerHandle.RegisterUdp(slot, slot.tcp, slot.udp, endpoint,
            slot.tcp.SessionToken!, Deadline + offset));
        Assert.Equal(accepted ? endpoint : null, slot.udp.endPoint);
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    public void ConfirmOnlyMakesConnectionReadyBeforeDeadline(int offset, bool accepted)
    {
        var slot = new Client(1);
        slot.tcp.StartHandshake(AcceptedAt);
        ServerHandle.ClientHello(slot, slot.tcp, ProtocolV4.Version, slot.id, "Player", AcceptedAt);
        byte[] token = slot.tcp.SessionToken!;
        Assert.True(ServerHandle.RegisterUdp(slot, slot.tcp, slot.udp,
            new IPEndPoint(IPAddress.Loopback, 12345), token, Deadline - 2));
        Assert.True(ServerHandle.RegisterUdp(slot, slot.tcp, slot.udp, slot.udp.endPoint!, token, Deadline - 1));

        ServerHandle.ConfirmUdp(slot, slot.tcp, token, slot.udp.ProbeNonce!, Deadline + offset);

        Assert.Equal(accepted, slot.IsReady);
    }

    [Fact]
    public void PendingSlotExpiresAtDeadlineAndOldTimeoutCannotCloseReplacement()
    {
        var slot = new Client(int.MaxValue);
        using var firstSocket = new TcpClient();
        using var nextSocket = new TcpClient();
        var firstHandle = firstSocket.Client.SafeHandle;
        var first = slot.tcp;
        var firstUdp = slot.udp;
        first.socket = firstSocket;
        first.StartHandshake(AcceptedAt);
        Server.clients.Add(slot.id, slot);
        try
        {
            Server.ExpireHandshake(slot.id, first, Deadline - 1);
            Assert.Same(first, slot.tcp);

            Server.ExpireHandshake(slot.id, first, Deadline);
            var next = slot.tcp;
            Assert.NotSame(first, next);
            Assert.Null(next.socket);
            Assert.Null(slot.udp.endPoint);
            Assert.True(firstHandle.IsClosed);

            next.StartHandshake(Deadline);
            ServerHandle.ClientHello(slot, next, ProtocolV4.Version, slot.id, "Replacement", Deadline + 1);
            next.socket = nextSocket;
            Server.ExpireHandshake(slot.id, first, Deadline + 1);
            Server.Disconnect(slot.id, first);
            Assert.False(ServerHandle.RegisterUdp(slot, first, firstUdp,
                new IPEndPoint(IPAddress.Loopback, 12345), next.SessionToken!, Deadline + 1));
            Assert.Same(nextSocket, slot.tcp.socket);
            Assert.Same(next, slot.tcp);
            Assert.Equal("Replacement", slot.Username);
            Assert.Null(slot.udp.endPoint);
        }
        finally
        {
            Server.clients.Remove(slot.id);
        }
    }

    [Fact]
    public void ReadySlotSurvivesItsHandshakeDeadline()
    {
        var slot = new Client(int.MaxValue);
        var connection = slot.tcp;
        connection.StartHandshake(AcceptedAt);
        ServerHandle.ClientHello(slot, connection, ProtocolV4.Version, slot.id, "Player", AcceptedAt);
        ServerHandle.RegisterUdp(slot, connection, slot.udp, new IPEndPoint(IPAddress.Loopback, 12345),
            connection.SessionToken!, Deadline - 2);
        ServerHandle.ConfirmUdp(slot, connection, connection.SessionToken!, slot.udp.ProbeNonce!, Deadline - 1);
        using var socket = new TcpClient();
        connection.socket = socket;
        Server.clients.Add(slot.id, slot);
        try
        {
            Server.ExpireHandshake(slot.id, connection, Deadline + 1);

            Assert.True(slot.IsReady);
            Assert.Same(connection, slot.tcp);
            Assert.Same(socket, slot.tcp.socket);
        }
        finally
        {
            Server.clients.Remove(slot.id);
        }
    }

    [Fact]
    [Trait("Category", "SocketIntegration")]
    public async Task StalledPeersReleaseSlotsWhileReadyPeerCanStillStartARound()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        CancellationToken token = timeout.Token;
        var (tcpPort, udpPort) = Server.Start(7, IPAddress.Loopback, 0, 0);
        try
        {
            await AssertBootstrapRejectedAsync(tcpPort, ProtocolV2.Version, malformed: false, token);
            await AssertBootstrapRejectedAsync(tcpPort, ProtocolV3.Version, malformed: false, token);
            await AssertBootstrapRejectedAsync(tcpPort, ProtocolV4.Version, malformed: true, token);
            using var ready = await Peer.OpenAsync(tcpPort, token);
            await AssertRegistrationAndRetryAsync(ready, udpPort, token);
            using var unresponsiveReady = await Peer.OpenAsync(tcpPort, token);
            long unresponsiveStarted = Stopwatch.GetTimestamp();
            await unresponsiveReady.CompleteHandshakeAsync(udpPort, token);
            using var silent = await Peer.OpenAsync(tcpPort, token);
            using var partial = await Peer.OpenAsync(tcpPort, token);
            using var noRegistration = await Peer.OpenAsync(tcpPort, token);
            using var noConfirmation = await Peer.OpenAsync(tcpPort, token);
            using var retrying = await Peer.OpenAsync(tcpPort, token);

            using var partialHello = ProtocolV4.CreateClientHello(partial.SlotId, "Partial");
            byte[] partialFrame = partialHello.ToArray();
            await partial.Stream.WriteAsync(partialFrame.AsMemory(0, 10), token);
            await noRegistration.GrantSessionAsync(token);
            await noRegistration.SendAsync(ClientPackets.createRoom, token);
            using (Packet roomError = await noRegistration.ReadAsync(ServerPackets.roomError, token))
                Assert.Equal((int)RoomError.NotReady, roomError.ReadInt());
            await noConfirmation.BeginUdpAsync(udpPort, token);
            await retrying.BeginUdpAsync(udpPort, token);

            using (var overflow = new TcpClient())
            {
                await overflow.ConnectAsync(IPAddress.Loopback, tcpPort, token);
                await AssertTcpClosedAsync(overflow.GetStream(), token);
            }

            using var stopRetries = CancellationTokenSource.CreateLinkedTokenSource(token);
            Task retries = retrying.RepeatRegistrationAsync(stopRetries.Token);
            try
            {
                await Task.WhenAll(silent.WaitForDeadlineAsync(token), partial.WaitForDeadlineAsync(token),
                    noRegistration.WaitForDeadlineAsync(token), noConfirmation.WaitForDeadlineAsync(token),
                    retrying.WaitForDeadlineAsync(token), ready.ReplyToHeartbeatsAsync(4, token),
                    unresponsiveReady.ExpectHeartbeatTimeoutAsync(unresponsiveStarted, token));
            }
            finally
            {
                stopRetries.Cancel();
                await retries;
            }

            // Admission reuses the heartbeat-expired slot. The responsive peer keeps its original slot.
            using var replacement = await Peer.OpenAsync(tcpPort, token);
            Assert.Equal(unresponsiveReady.SlotId, replacement.SlotId);
            await replacement.CompleteHandshakeAsync(udpPort, token);
            Assert.NotEqual(unresponsiveReady.SessionToken, replacement.SessionToken);
            await AssertUdpHealthAsync(ready, udpPort, token);
            var firstRoom = await StartRoomAsync(ready, replacement, token);

            using var otherHost = await Peer.OpenAsync(tcpPort, token);
            Assert.Equal(silent.SlotId, otherHost.SlotId);
            await otherHost.CompleteHandshakeAsync(udpPort, token);
            using var otherMember = await Peer.OpenAsync(tcpPort, token);
            await otherMember.CompleteHandshakeAsync(udpPort, token);
            var secondRoom = await StartRoomAsync(otherHost, otherMember, token);
            Assert.NotEqual(firstRoom.Id, secondRoom.Id);
            await AssertMovementAsync(ready, replacement, otherMember, udpPort, firstRoom, token);
            await AssertCornerSweepAsync(ready, replacement, firstRoom, token);
            await AssertSpeedRejectAndRecoveryAsync(ready, replacement, firstRoom, token);
            await otherHost.SendMovementAsync(secondRoom.Id, secondRoom.Round, secondRoom.X + 0.125f,
                secondRoom.Y, otherHost.SessionToken, otherHost.Udp, token);
            await AssertUdpPositionAsync(otherMember, otherHost, secondRoom.Id, secondRoom.Round,
                secondRoom.X + 0.125f, secondRoom.Y, token);
            await AssertUdpQuietAsync(replacement.Udp, token);

            await AssertSameEndpointReconnectAsync(ready, firstRoom, tcpPort, udpPort, token);
            replacement.Dispose();
            otherHost.Dispose();
            otherMember.Dispose();
            while (Server.LatestMetrics is not { ConnectedClients: 0, ReadyClients: 0, RoomCount: 0 })
                await Task.Delay(50, token);
        }
        finally
        {
            Server.Shutdown();
        }
    }

    private static async Task AssertCornerSweepAsync(Peer mover, Peer recipient,
        (int Id, int Round, float X, float Y) room, CancellationToken token)
    {
        Assert.Equal((-7.5f, 5.5f), (room.X, room.Y));

        // Top-left spawn: the route turns around the hard block at (2,12).
        const float ax = -7.32f, ay = 5.22f, by = 5.32f, cx = -7.22f;
        await mover.SendMovementAsync(room.Id, room.Round, ax, ay, mover.SessionToken, mover.Udp, token);
        await AssertUdpPositionAsync(recipient, mover, room.Id, room.Round, ax, ay, token);

        // Refill after the approach to A so the corner rejection can only be due to terrain.
        await AssertUdpQuietAsync(recipient.Udp, token);

        // Omit B. C is clear but the segment from A to C cuts the corner.
        await mover.SendMovementAsync(room.Id, room.Round, cx, by, mover.SessionToken, mover.Udp, token);
        await AssertCorrectionAsync(mover, room.Id, room.Round, ax, ay, token);
        await AssertUdpQuietAsync(recipient.Udp, token);

        await mover.SendTcpMovementAsync(room.Id, room.Round, cx, by, token);
        await AssertCorrectionAsync(mover, room.Id, room.Round, ax, ay, token);
        await AssertUdpQuietAsync(recipient.Udp, token);

        await mover.SendMovementAsync(room.Id, room.Round, ax, by, mover.SessionToken, mover.Udp, token);
        await AssertUdpPositionAsync(recipient, mover, room.Id, room.Round, ax, by, token);
        await mover.SendMovementAsync(room.Id, room.Round, cx, by, mover.SessionToken, mover.Udp, token);
        await AssertUdpPositionAsync(recipient, mover, room.Id, room.Round, cx, by, token);
    }

    private static async Task AssertSpeedRejectAndRecoveryAsync(Peer mover, Peer recipient,
        (int Id, int Round, float X, float Y) room, CancellationToken token)
    {
        // The preceding corner fixture ends at this position in the clear spawn lane.
        const float lastX = -7.22f, y = 5.32f;
        const float rejectedX = -7.625f; // Clear sweep, but the distance exceeds even a full budget.

        await mover.SendMovementAsync(room.Id, room.Round, rejectedX, y, mover.SessionToken, mover.Udp, token);
        await AssertCorrectionAsync(mover, room.Id, room.Round, lastX, y, token);
        await AssertUdpQuietAsync(recipient.Udp, token);

        await mover.SendTcpMovementAsync(room.Id, room.Round, rejectedX, y, token);
        await AssertCorrectionAsync(mover, room.Id, room.Round, lastX, y, token);
        await AssertUdpQuietAsync(recipient.Udp, token);

        const float recoveredX = -7.25f;
        await mover.SendMovementAsync(room.Id, room.Round, recoveredX, y, mover.SessionToken, mover.Udp, token);
        await AssertUdpPositionAsync(recipient, mover, room.Id, room.Round, recoveredX, y, token);
    }

    private static async Task AssertBootstrapRejectedAsync(int tcpPort, int version, bool malformed, CancellationToken token)
    {
        using var peer = await Peer.OpenAsync(tcpPort, token);
        using var oldHello = ProtocolV2.CreateClientHello(peer.SlotId, "Legacy", version);
        byte[] frame = malformed ? ClientFrame(ClientPackets.clientHello, packet =>
        {
            packet.Write(version);
            packet.Write(peer.SlotId);
            packet.Write(2);
            packet.Write((byte)'A');
        }) : oldHello.ToArray();
        await peer.Stream.WriteAsync(frame, token);
        // A SessionGranted or any other payload in place of EOF fails this assertion.
        await AssertTcpClosedAsync(peer.Stream, token);
    }

    private static async Task AssertUdpHealthAsync(Peer peer, int port, CancellationToken token)
    {
        const long sequence = 17;
        using var probe = ProtocolV3.CreateUdpHealthProbe(peer.SlotId, peer.SessionToken, sequence);
        using var wrongToken = ProtocolV3.CreateUdpHealthProbe(peer.SlotId, Alter(peer.SessionToken), sequence);
        using var wrongEndpoint = new UdpClient();
        wrongEndpoint.Connect(IPAddress.Loopback, port);
        await wrongEndpoint.SendAsync(probe.ToArray(), token);
        await AssertUdpQuietAsync(wrongEndpoint, token);

        byte[] badHmac = probe.ToArray();
        badHmac[^1] ^= 1;
        await peer.Udp.SendAsync(badHmac, token);
        await peer.Udp.SendAsync(wrongToken.ToArray(), token);
        await AssertUdpQuietAsync(peer.Udp, token);

        await peer.Udp.SendAsync(probe.ToArray(), token);
        using var packet = ProtocolV3.OpenServerDatagram((await peer.Udp.ReceiveAsync(token)).Buffer);
        var echo = ProtocolV3.ReadUdpHealthEcho(packet);
        Assert.Equal(peer.SessionToken, echo.Token);
        Assert.Equal(sequence, echo.Sequence);
    }

    private static async Task AssertRegistrationAndRetryAsync(Peer peer, int port, CancellationToken token)
    {
        await peer.GrantSessionAsync(token);
        using var wrongEndpoint = new UdpClient();
        wrongEndpoint.Connect(IPAddress.Loopback, port);
        using var wrong = ProtocolV2.CreateUdpRegistration(peer.SlotId, Alter(peer.SessionToken));
        await wrongEndpoint.SendAsync(wrong.ToArray(), token);
        await AssertUdpQuietAsync(wrongEndpoint, token);

        peer.Udp.Connect(IPAddress.Loopback, port);
        using var valid = ProtocolV2.CreateUdpRegistration(peer.SlotId, peer.SessionToken);
        byte[] malformed = valid.ToArray();
        malformed[^1] ^= 1;
        await peer.Udp.SendAsync(malformed, token);
        await AssertUdpQuietAsync(peer.Udp, token);

        byte[] nonce = await peer.RegisterUdpAsync(token);
        Assert.Equal(nonce, await peer.RegisterUdpAsync(token));
        await wrongEndpoint.SendAsync(valid.ToArray(), token);
        await AssertUdpQuietAsync(wrongEndpoint, token);
        await peer.ConfirmAsync(nonce, token);
    }

    private static async Task<(int Id, int Round, float X, float Y)> StartRoomAsync(
        Peer ready, Peer replacement, CancellationToken token)
    {
        await ready.SendAsync(ClientPackets.createRoom, token);
        using Packet created = await ready.ReadAsync(ServerPackets.roomChanged, token);
        int room = created.ReadInt();
        Assert.True(room > 0);
        await replacement.SendAsync(ClientPackets.joinRoom, token, packet => packet.Write(room));
        using Packet joined = await replacement.ReadAsync(ServerPackets.roomChanged, token);
        Assert.Equal(room, joined.ReadInt());

        await ready.SendAsync(ClientPackets.requestStart, token, packet =>
        {
            packet.Write(room);
            packet.Write(0);
        });
        using Packet started = await ready.ReadAsync(ServerPackets.roundBegin, token, skipMatchState: true);
        Assert.Equal(room, started.ReadInt());
        int round = started.ReadInt();
        Assert.Equal(1, round);
        Assert.Equal(2, started.ReadInt());
        using (Packet map = await ready.ReadAsync(ServerPackets.mapData, token))
            Assert.Equal(room, map.ReadInt());
        using Packet spawn = await ready.ReadAsync(ServerPackets.spawnPlayer, token);
        Assert.Equal(room, spawn.ReadInt());
        Assert.Equal(round, spawn.ReadInt());
        Assert.Equal(ready.SlotId, spawn.ReadInt());
        float x = spawn.ReadFloat();
        float y = spawn.ReadFloat();
        using (Packet otherSpawn = await ready.ReadAsync(ServerPackets.spawnPlayer, token))
            Assert.Equal(room, otherSpawn.ReadInt());
        using (Packet match = await ready.ReadAsync(ServerPackets.matchState, token))
            Assert.Equal(room, match.ReadInt());
        return (room, round, x, y);
    }

    private static async Task AssertMovementAsync(Peer mover, Peer recipient, Peer otherRoom, int udpPort,
        (int Id, int Round, float X, float Y) room, CancellationToken token)
    {
        using var wrongEndpoint = new UdpClient();
        wrongEndpoint.Connect(IPAddress.Loopback, udpPort);
        await mover.SendMovementAsync(room.Id, room.Round, room.X + 0.0625f, room.Y,
            mover.SessionToken, wrongEndpoint, token, long.MaxValue);
        await mover.SendMovementAsync(room.Id, room.Round, room.X + 0.0625f, room.Y,
            Alter(mover.SessionToken), mover.Udp, token, long.MaxValue);
        await mover.SendMovementAsync(room.Id, room.Round + 1, room.X + 0.0625f, room.Y,
            mover.SessionToken, mover.Udp, token, long.MaxValue);
        using (var legacy = ProtocolV2.CreateUdpPlayerMovement(mover.SlotId, mover.SessionToken,
            room.Id, room.Round, room.X + 0.0625f, room.Y))
            await mover.Udp.SendAsync(legacy.ToArray(), token);
        await mover.SendAsync(ClientPackets.playerMovement, token, packet =>
        {
            packet.Write(room.Id);
            packet.Write(room.Round);
            packet.Write(room.X + 0.0625f);
            packet.Write(room.Y);
        });
        await AssertUdpQuietAsync(recipient.Udp, token);

        // A correction proves the legacy packets did not change authority, even if no peer update was sent.
        await mover.SendMovementAsync(room.Id, room.Round, 999f, 999f, mover.SessionToken, mover.Udp, token);
        Assert.Equal(1L, mover.LastSentMoveSeq);
        await AssertCorrectionAsync(mover, room.Id, room.Round, room.X, room.Y, token);

        float x = room.X + 0.125f;
        await mover.SendMovementAsync(room.Id, room.Round, x, room.Y, mover.SessionToken, mover.Udp, token);
        await AssertUdpPositionAsync(recipient, mover, room.Id, room.Round, x, room.Y, token);
        await AssertUdpQuietAsync(otherRoom.Udp, token);

        await mover.SendMovementAsync(room.Id, room.Round, room.X + 0.0625f, room.Y,
            mover.SessionToken, mover.Udp, token, mover.LastAcceptedMoveSeq);
        await mover.SendTcpMovementAsync(room.Id, room.Round, room.X + 0.0625f, room.Y,
            token, mover.LastAcceptedMoveSeq - 1);
        await AssertUdpQuietAsync(recipient.Udp, token);

        await mover.SendMovementAsync(room.Id, room.Round, 999f, 999f, mover.SessionToken, mover.Udp, token);
        await AssertCorrectionAsync(mover, room.Id, room.Round, x, room.Y, token);
    }

    private static async Task AssertCorrectionAsync(Peer mover, int room, int round,
        float x, float y, CancellationToken token)
    {
        using Packet packet = await mover.ReadAsync(ServerPackets.playerCorrectionV4, token, leaveId: true);
        Assert.Equal((room, round, mover.SlotId, mover.LastSentMoveSeq, mover.LastAcceptedMoveSeq, x, y),
            ProtocolV4.ReadTcpPlayerCorrection(packet));
        Assert.Equal(0, packet.UnreadLength());
    }

    private static async Task AssertUdpPositionAsync(Peer recipient, Peer mover, int room, int round,
        float x, float y, CancellationToken token)
    {
        using Packet packet = ProtocolV4.OpenServerDatagram((await recipient.Udp.ReceiveAsync(token)).Buffer);
        var position = ProtocolV4.ReadUdpPlayerPosition(packet);
        Assert.Equal(recipient.SessionToken, position.Token);
        Assert.Equal((room, round, mover.SlotId, mover.LastSentMoveSeq, x, y),
            (position.RoomId, position.RoundId, position.PlayerId, position.AcceptedMoveSeq, position.X, position.Y));
        Assert.Equal(0, packet.UnreadLength());
        mover.LastAcceptedMoveSeq = position.AcceptedMoveSeq;
    }

    private static async Task AssertSameEndpointReconnectAsync(Peer original,
        (int Id, int Round, float X, float Y) oldRoom, int tcpPort, int udpPort, CancellationToken token)
    {
        using var oldMovement = ProtocolV4.CreateUdpPlayerMovement(original.SlotId, original.SessionToken,
            oldRoom.Id, oldRoom.Round, long.MaxValue, oldRoom.X + 0.0625f, oldRoom.Y);
        original.CloseTcp();
        while (Server.LatestMetrics?.ConnectedClients != 3)
            await Task.Delay(50, token);

        using var replacement = await Peer.OpenAsync(tcpPort, token, original.Udp);
        Assert.Equal(original.SlotId, replacement.SlotId);
        await replacement.GrantSessionAsync(token);
        Assert.False(replacement.SessionToken.AsSpan().SequenceEqual(original.SessionToken));

        using var stale = ProtocolV2.CreateUdpRegistration(original.SlotId, original.SessionToken);
        await original.Udp.SendAsync(stale.ToArray(), token);
        await AssertUdpQuietAsync(original.Udp, token);
        byte[] nonce = await replacement.RegisterUdpAsync(token);
        await replacement.ConfirmAsync(nonce, token);
        await replacement.SendConfirmAsync(nonce, token);

        using var recipient = await Peer.OpenAsync(tcpPort, token);
        await recipient.CompleteHandshakeAsync(udpPort, token);
        // Also verifies that the repeated confirm did not produce another lobbyReady before roomChanged.
        var room = await StartRoomAsync(replacement, recipient, token);
        Assert.NotEqual(oldRoom.Id, room.Id);

        await original.Udp.SendAsync(oldMovement.ToArray(), token);
        await replacement.SendMovementAsync(room.Id, room.Round, room.X + 0.0625f, room.Y,
            original.SessionToken, original.Udp, token, long.MaxValue);
        await AssertUdpQuietAsync(recipient.Udp, token);
        await replacement.SendMovementAsync(room.Id, room.Round, 999f, 999f, replacement.SessionToken,
            replacement.Udp, token);
        Assert.Equal(1L, replacement.LastSentMoveSeq);
        await AssertCorrectionAsync(replacement, room.Id, room.Round, room.X, room.Y, token);

        float x = room.X + 0.125f;
        await replacement.SendMovementAsync(room.Id, room.Round, x, room.Y, replacement.SessionToken,
            replacement.Udp, token);
        await AssertUdpPositionAsync(recipient, replacement, room.Id, room.Round, x, room.Y, token);
    }

    private static async Task AssertUdpQuietAsync(UdpClient udp, CancellationToken token)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
        wait.CancelAfter(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await udp.ReceiveAsync(wait.Token));
        token.ThrowIfCancellationRequested();
    }

    private static byte[] Alter(byte[] token)
    {
        byte[] different = (byte[])token.Clone();
        different[0] ^= 1;
        return different;
    }

    private static byte[] ClientFrame(ClientPackets type, Action<Packet>? write)
    {
        using var packet = new Packet((int)type);
        write?.Invoke(packet);
        packet.WriteLength();
        packet.EncryptPayload();
        return packet.ToArray();
    }

    private static async Task AssertTcpClosedAsync(NetworkStream stream, CancellationToken token)
    {
        try
        {
            Assert.Equal(0, await stream.ReadAsync(new byte[1], token));
        }
        catch (IOException ex) when (ex.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionReset })
        {
            // Windows can send a reset when a socket closes with unread input.
        }
    }

    private sealed class Peer : IDisposable
    {
        private readonly TcpClient tcp = new();
        internal readonly UdpClient Udp;
        private readonly long started = Stopwatch.GetTimestamp();
        internal NetworkStream Stream => tcp.GetStream();
        internal int SlotId { get; private set; }
        internal byte[] SessionToken { get; private set; } = null!;
        internal long LastSentMoveSeq { get; private set; }
        internal long LastAcceptedMoveSeq { get; set; }
        private readonly bool ownsUdp;

        private Peer(UdpClient? reuseUdp)
        {
            Udp = reuseUdp ?? new UdpClient();
            ownsUdp = reuseUdp == null;
        }

        internal static async Task<Peer> OpenAsync(int port, CancellationToken token, UdpClient? reuseUdp = null)
        {
            var peer = new Peer(reuseUdp);
            try
            {
                await peer.tcp.ConnectAsync(IPAddress.Loopback, port, token);
                using Packet hello = await peer.ReadAsync(ServerPackets.serverHello, token, leaveId: true);
                var server = ProtocolV2.ReadServerHello(hello);
                Assert.Equal(ProtocolV4.Version, server.Version);
                peer.SlotId = server.SlotId;
                return peer;
            }
            catch
            {
                peer.Dispose();
                throw;
            }
        }

        internal async Task WaitForDeadlineAsync(CancellationToken token)
        {
            await AssertTcpClosedAsync(Stream, token);
            Assert.True(Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(20));
        }

        internal async Task ReplyToHeartbeatsAsync(int count, CancellationToken token)
        {
            for (int sequence = 1; sequence <= count; sequence++)
            {
                using Packet ping = await ReadAsync(ServerPackets.heartbeatPing, token, leaveId: true);
                Assert.Equal(sequence, ProtocolV3.ReadHeartbeatPing(ping));
                await SendPongAsync(sequence, token);
            }
        }

        internal async Task ExpectHeartbeatTimeoutAsync(long startedAt, CancellationToken token)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                using Packet ping = await ReadAsync(ServerPackets.heartbeatPing, token, leaveId: true);
                Assert.Equal(1, ProtocolV3.ReadHeartbeatPing(ping));
            }
            await AssertTcpClosedAsync(Stream, token);
            Assert.True(Stopwatch.GetElapsedTime(startedAt) >= TimeSpan.FromSeconds(20));
        }

        private async Task SendPongAsync(long sequence, CancellationToken token)
        {
            using var pong = ProtocolV3.CreateHeartbeatPong(sequence);
            await Stream.WriteAsync(pong.ToArray(), token);
        }

        internal Task SendAsync(ClientPackets type, CancellationToken token, Action<Packet>? write = null) =>
            Stream.WriteAsync(ClientFrame(type, write), token).AsTask();

        internal async Task GrantSessionAsync(CancellationToken cancellation)
        {
            using var hello = ProtocolV4.CreateClientHello(SlotId, $"Peer{SlotId}");
            await Stream.WriteAsync(hello.ToArray(), cancellation);
            using Packet granted = await ReadAsync(ServerPackets.sessionGranted, cancellation, leaveId: true);
            SessionToken = ProtocolV2.ReadSessionGranted(granted);
        }

        internal async Task<byte[]> BeginUdpAsync(int port, CancellationToken token)
        {
            await GrantSessionAsync(token);
            Udp.Connect(IPAddress.Loopback, port);
            return await RegisterUdpAsync(token);
        }

        internal async Task<byte[]> RegisterUdpAsync(CancellationToken token)
        {
            using var registration = ProtocolV2.CreateUdpRegistration(SlotId, SessionToken);
            await Udp.SendAsync(registration.ToArray(), token);
            using var packet = ProtocolV2.OpenServerDatagram((await Udp.ReceiveAsync(token)).Buffer);
            var probe = ProtocolV2.ReadUdpProbe(packet);
            Assert.Equal(SessionToken, probe.Token);
            return probe.Nonce;
        }

        internal async Task SendMovementAsync(int room, int round, float x, float y, byte[] token,
            UdpClient sender, CancellationToken cancellation, long? sequenceOverride = null)
        {
            using var movement = ProtocolV4.CreateUdpPlayerMovement(SlotId, token, room, round,
                sequenceOverride ?? ++LastSentMoveSeq, x, y);
            await sender.SendAsync(movement.ToArray(), cancellation);
        }

        internal async Task SendTcpMovementAsync(int room, int round, float x, float y, CancellationToken cancellation,
            long? sequenceOverride = null)
        {
            using var movement = ProtocolV4.CreateTcpPlayerMovement(room, round,
                sequenceOverride ?? ++LastSentMoveSeq, x, y);
            await Stream.WriteAsync(movement.ToArray(), cancellation);
        }

        internal async Task RepeatRegistrationAsync(CancellationToken token)
        {
            using var packet = ProtocolV2.CreateUdpRegistration(SlotId, SessionToken);
            byte[] registration = packet.ToArray();
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Udp.SendAsync(registration, token);
                    await Task.Delay(100, token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }

        internal async Task CompleteHandshakeAsync(int udpPort, CancellationToken token)
        {
            byte[] nonce = await BeginUdpAsync(udpPort, token);
            await ConfirmAsync(nonce, token);
        }

        internal async Task ConfirmAsync(byte[] nonce, CancellationToken token)
        {
            await SendConfirmAsync(nonce, token);
            using Packet ready = await ReadAsync(ServerPackets.lobbyReady, token, leaveId: true);
            ProtocolV2.ReadLobbyReady(ready);
            Assert.Equal(0, ready.UnreadLength());
        }

        internal async Task SendConfirmAsync(byte[] nonce, CancellationToken token)
        {
            using var confirm = ProtocolV2.CreateUdpConfirm(SessionToken, nonce);
            await Stream.WriteAsync(confirm.ToArray(), token);
        }

        internal async Task<Packet> ReadAsync(ServerPackets expected, CancellationToken token,
            bool skipMatchState = false, bool leaveId = false)
        {
            byte[] header = new byte[4];
            while (true)
            {
                await Stream.ReadExactlyAsync(header, token);
                int length = BinaryPrimitives.ReadInt32LittleEndian(header);
                Assert.InRange(length, 64, 16_384);
                var frame = new byte[4 + length];
                header.CopyTo(frame, 0);
                await Stream.ReadExactlyAsync(frame.AsMemory(4), token);
                var packet = new Packet(frame);
                try
                {
                    packet.DecryptPayload();
                    packet.ReadInt();
                    var type = (ServerPackets)packet.ReadInt(false);
                    if (type == ServerPackets.heartbeatPing && expected != type)
                    {
                        await SendPongAsync(ProtocolV3.ReadHeartbeatPing(packet), token);
                        packet.Dispose();
                        continue;
                    }
                    if (!skipMatchState || type != ServerPackets.matchState)
                    {
                        Assert.Equal(expected, type);
                        if (!leaveId) packet.ReadInt();
                        return packet;
                    }
                }
                catch
                {
                    packet.Dispose();
                    throw;
                }
                packet.Dispose();
            }
        }

        internal void CloseTcp() => tcp.Dispose();

        public void Dispose()
        {
            tcp.Dispose();
            if (ownsUdp) Udp.Dispose();
        }
    }
}
