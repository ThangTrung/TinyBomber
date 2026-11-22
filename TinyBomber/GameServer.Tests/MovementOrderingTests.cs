using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using GameShared;

namespace GameServer.Tests;

[Collection("Network metrics server state")]
public sealed class MovementOrderingTests
{
    private readonly GameLogic room = new(71);
    private readonly Client mover = new(1);
    private readonly Client peer = new(2);
    private readonly IPEndPoint endpoint = new(IPAddress.Loopback, 30001);
    private Player Player => mover.player!;
    private (long Processed, long Accepted) Watermarks => (Player.LastProcessedMoveSeq, Player.LastAcceptedMoveSeq);
    private (float X, float Y) Position => (Player.x, Player.y);

    public MovementOrderingTests()
    {
        MakeReady(mover);
        MakeReady(peer);
        mover.udp.endPoint = endpoint;
        Assert.True(room.TryAddMember(mover));
        Assert.True(room.TryAddMember(peer));
        room.StartRound(mover, room.Id, 0, now: 0);
        Assert.Equal(MatchPhase.Playing, room.Phase);
        Array.Clear(room.mapData);
        (Player.x, Player.y) = (0f, 0f);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TcpAndUdpAcceptNewMovesInOrderAndShareOneWatermark(bool firstViaUdp)
    {
        Move(11, 0.08f, viaUdp: firstViaUdp);
        Assert.Equal((11L, 11L), Watermarks);
        Assert.Equal((0.08f, 0f), Position);

        Move(12, 0.16f, ms: 20, viaUdp: !firstViaUdp);
        Assert.Equal((12L, 12L), Watermarks);
        Assert.Equal((0.16f, 0f), Position);
    }

    [Theory]
    [InlineData(11L)]
    [InlineData(12L)]
    public void LateAndDuplicateMovesDoNotMoveOrSpendCredit(long staleSequence)
    {
        Move(12, 0.16f, ms: 100, viaUdp: true);
        Assert.Equal((0.16f, 0f), Position);
        Move(staleSequence, 0.08f, ms: 100);
        Assert.Equal((12L, 12L), Watermarks);
        Assert.Equal((0.16f, 0f), Position);

        // Needs all of the remaining credit at the same timestamp.
        Move(13, 0.32f, ms: 100);
        Assert.Equal((13L, 13L), Watermarks);
        Assert.Equal((0.32f, 0f), Position);
    }

    [Fact]
    public void CollisionRejectIsProcessedAndAnOlderClearMoveCannotTakeItsPlace()
    {
        room.mapData[9, 8] = 1;
        (Player.x, Player.y) = (-0.32f, 0.5f);
        Assert.False(room.CanMove(Player, -0.24f, 0.5f));
        Assert.True(room.CanMove(Player, -0.40f, 0.5f));
        Move(12, -0.24f, 0.5f);
        Move(11, -0.40f, 0.5f, viaUdp: true);
        Assert.Equal((12L, 0L), Watermarks);
        Assert.Equal((-0.32f, 0.5f), Position);

        Move(13, -0.40f, 0.5f);
        Assert.Equal((13L, 13L), Watermarks);
        Assert.Equal((-0.40f, 0.5f), Position);
    }

    [Fact]
    public void SpeedRejectIsProcessedAndAnOlderAffordableMoveCannotTakeItsPlace()
    {
        Assert.True(room.CanMove(Player, 0.40f, 0f));
        Move(12, 0.40f, viaUdp: true);
        Move(11, 0.08f);
        Assert.Equal((12L, 0L), Watermarks);
        Assert.Equal((0f, 0f), Position);

        Move(13, 0.08f);
        Assert.Equal((13L, 13L), Watermarks);
        Assert.Equal((0.08f, 0f), Position);
    }

    [Theory]
    [InlineData(float.NaN, 0f)]
    [InlineData(0f, float.NaN)]
    [InlineData(float.PositiveInfinity, 0f)]
    [InlineData(0f, float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity, 0f)]
    [InlineData(0f, float.NegativeInfinity)]
    public void NonFiniteMoveIsProcessedWithoutChangingPositionOrSpendingCredit(float x, float y)
    {
        Assert.False(room.CanMove(Player, x, y));
        Move(11, x, y, viaUdp: true);
        Assert.Equal((11L, 0L), Watermarks);
        Assert.Equal((0f, 0f), Position);

        Move(12, 0.08f);
        Assert.Equal((12L, 12L), Watermarks);
        Assert.Equal((0.08f, 0f), Position);
    }

    [Fact]
    public void InvalidSessionEndpointRoomRoundOrPlayerCannotAdvanceWatermark()
    {
        var tcp = mover.tcp;
        var udp = mover.udp;
        byte[] token = tcp.SessionToken!;
        byte[] wrongToken = (byte[])token.Clone();
        wrongToken[0] ^= 1;
        const long high = long.MaxValue;
        ServerHandle.UdpMovement(mover, tcp, udp, endpoint, wrongToken,
            room.Id, room.RoundId, 0.08f, 0f, now: 0, moveSeq: high);
        ServerHandle.UdpMovement(mover, tcp, udp, new IPEndPoint(endpoint.Address, endpoint.Port + 1), token,
            room.Id, room.RoundId, 0.08f, 0f, now: 0, moveSeq: high);
        ServerHandle.UdpMovement(mover, new Client.TCP(mover.id), udp, endpoint, token,
            room.Id, room.RoundId, 0.08f, 0f, now: 0, moveSeq: high);
        ServerHandle.UdpMovement(mover, tcp, new Client.UDP(mover.id), endpoint, token,
            room.Id, room.RoundId, 0.08f, 0f, now: 0, moveSeq: high);
        ServerHandle.PlayerMovement(mover, room.Id + 1, room.RoundId, 0.08f, 0f, now: 0, moveSeq: high);
        ServerHandle.PlayerMovement(mover, room.Id, room.RoundId + 1, 0.08f, 0f, now: 0, moveSeq: high);
        mover.UdpConfirmed = false;
        Move(high, 0.08f);
        mover.UdpConfirmed = true;
        tcp.SessionToken = null;
        Move(high, 0.08f);
        tcp.SessionToken = token;
        Player.isAlive = false;
        Move(high, 0.08f);
        Player.isAlive = true;

        Assert.Equal((0L, 0L), Watermarks);
        Assert.Equal((0f, 0f), Position);
        Move(1, 0.08f);
        Assert.Equal((1L, 1L), Watermarks);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    public void NonpositiveSequenceLeavesFirstMoveAvailable(long sequence)
    {
        Move(sequence, 0.08f);
        Assert.Equal((0L, 0L), Watermarks);
        Move(1, 0.08f);
        Assert.Equal((1L, 1L), Watermarks);
    }

    [Fact]
    public void FinishedRoundCannotAdvanceWatermarkOfLivingPlayer()
    {
        Move(11, 0.08f);
        peer.player!.isAlive = false;
        room.UpdateRound(long.MaxValue);
        Assert.Equal(MatchPhase.Finished, room.Phase);
        Assert.True(Player.isAlive);
        Move(12, 0.16f, ms: 20, viaUdp: true);
        Assert.Equal((11L, 11L), Watermarks);
        Assert.Equal((0.08f, 0f), Position);
    }

    [Fact]
    public void ReplayResetsWatermarksAndOldRoundCannotSeedTheNewPlayer()
    {
        Move(long.MaxValue - 1, 0.02f);
        Move(long.MaxValue, 0.04f, viaUdp: true);
        Move(long.MaxValue, 0.06f);
        Move(1, 0.06f);
        Assert.Equal((long.MaxValue, long.MaxValue), Watermarks);
        Assert.Equal((0.04f, 0f), Position);

        Player original = Player;
        int previousRound = room.RoundId;
        peer.player!.isAlive = false;
        room.UpdateRound(long.MaxValue);
        Assert.Equal(MatchPhase.Finished, room.Phase);
        Move(long.MaxValue, 0.06f);
        Assert.Equal((0.04f, 0f), Position);
        room.StartRound(mover, room.Id, previousRound, now: 0);
        Assert.NotSame(original, Player);
        Array.Clear(room.mapData);
        var spawn = Position;
        ServerHandle.PlayerMovement(mover, room.Id, previousRound, spawn.X + 0.08f, spawn.Y,
            now: 0, moveSeq: long.MaxValue);
        Assert.Equal((0L, 0L), Watermarks);
        Move(1, spawn.X + 0.08f, spawn.Y);
        Assert.Equal((1L, 1L), Watermarks);
        Assert.Equal((spawn.X + 0.08f, spawn.Y), Position);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LeavingOrReusingASlotStartsWithFreshWatermarks(bool disconnect)
    {
        Move(100, 0.08f);
        var originalPlayer = Player;
        var oldTcp = mover.tcp;
        var oldUdp = mover.udp;
        byte[] oldToken = oldTcp.SessionToken!;
        if (disconnect)
        {
            bool hadPreviousSlot = Server.clients.TryGetValue(mover.id, out Client? previousSlot);
            Server.clients[mover.id] = mover;
            try { Server.Disconnect(mover.id, oldTcp); }
            finally
            {
                if (hadPreviousSlot) Server.clients[mover.id] = previousSlot!;
                else Server.clients.Remove(mover.id);
            }
            Assert.NotSame(oldTcp, mover.tcp);
            Assert.NotSame(oldUdp, mover.udp);
            MakeReady(mover);
        }
        else
        {
            room.RemoveMember(mover);
        }

        Assert.Null(mover.player);
        var nextRoom = new GameLogic(room.Id + 1);
        Assert.True(nextRoom.TryAddMember(mover));
        var nextPeer = new Client(3);
        MakeReady(nextPeer);
        Assert.True(nextRoom.TryAddMember(nextPeer));
        nextRoom.StartRound(mover, nextRoom.Id, 0, now: 0);
        Array.Clear(nextRoom.mapData);
        Assert.NotSame(originalPlayer, Player);
        mover.udp.endPoint = endpoint;
        var spawn = Position;
        ServerHandle.UdpMovement(mover, oldTcp, oldUdp, endpoint, oldToken, room.Id, room.RoundId,
            spawn.X + 0.08f, spawn.Y, now: 0, moveSeq: long.MaxValue);
        if (disconnect)
        {
            ServerHandle.UdpMovement(mover, mover.tcp, mover.udp, endpoint, oldToken, nextRoom.Id, nextRoom.RoundId,
                spawn.X + 0.08f, spawn.Y, now: 0, moveSeq: long.MaxValue);
        }
        Assert.Equal((0L, 0L), Watermarks);
        ServerHandle.PlayerMovement(mover, nextRoom.Id, nextRoom.RoundId, spawn.X + 0.08f, spawn.Y,
            now: 0, moveSeq: 1);
        Assert.Equal((1L, 1L), Watermarks);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MissingCornerStepStillRequiresCollisionRejectAndANewSequenceToRecover(bool receiveMiddle)
    {
        room.mapData[2, 2] = 1;
        var a = (X: -7.32f, Y: -6.22f);
        var b = (X: -7.32f, Y: -6.32f);
        var c = (X: -7.22f, Y: -6.32f);
        (Player.x, Player.y) = a;
        Assert.False(room.CanMove(Player, c.X, c.Y));
        if (receiveMiddle) Move(11, b.X, b.Y, ms: 100);
        Move(12, c.X, c.Y, ms: 100, viaUdp: true);
        Assert.Equal(receiveMiddle ? c : a, Position);
        Assert.Equal((12L, receiveMiddle ? 12L : 0L), Watermarks);
        if (receiveMiddle) return;

        Move(11, b.X, b.Y, ms: 100);
        Assert.Equal(a, Position);
        Move(13, b.X, b.Y, ms: 100);
        Move(14, c.X, c.Y, ms: 100);
        Assert.Equal((14L, 14L), Watermarks);
        Assert.Equal(c, Position);
    }

    [Theory]
    [InlineData(40, true)]
    [InlineData(80, true)]
    [InlineData(100, false)]
    [InlineData(200, false)]
    public void SequenceGapsDoNotChangeSpeedCreditCap(int ms, bool accepted)
    {
        Move(1, 0.08f);
        float target = 0.08f + 4f * ms / 1000;
        long laterSeq = 1 + ms / 20;
        Move(laterSeq, target, ms: ms, viaUdp: true);
        Assert.Equal((laterSeq, accepted ? laterSeq : 1L), Watermarks);
        Assert.Equal((accepted ? target : 0.08f, 0f), Position);
    }

    [Fact]
    [Trait("Category", "SocketIntegration")]
    public async Task OrderedDispatchSharesWatermarkAndOnlySendsCurrentPositionsAndLegitimateCorrections()
    {
        // Inject complete frames in a known order. Observe actual TCP/UDP writes on loopback.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        CancellationToken cancellation = timeout.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var ownerSocket = new TcpClient();
        using var peerSocket = new TcpClient();
        await ownerSocket.ConnectAsync((IPEndPoint)listener.LocalEndpoint, cancellation);
        await peerSocket.ConnectAsync((IPEndPoint)listener.LocalEndpoint, cancellation);
        using TcpClient ownerConnection = await listener.AcceptTcpClientAsync(cancellation);
        using TcpClient peerConnection = await listener.AcceptTcpClientAsync(cancellation);
        var ownerStream = ownerSocket.GetStream();
        var peerStream = peerSocket.GetStream();
        using var udpSender = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var udpPeer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));

        var tcpDispatch = typeof(Client.TCP).GetMethod("HandleFrame", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var udpDispatch = typeof(Server).GetMethod("HandleUdpDatagram", BindingFlags.Static | BindingFlags.NonPublic)!;
        var udpSocketField = typeof(Server).GetField("udpSocket", BindingFlags.Static | BindingFlags.NonPublic)!;
        var connectionLimit = typeof(Server).GetProperty(nameof(Server.MaxConnections))!;
        object? previousUdpSocket = udpSocketField.GetValue(null);
        int previousLimit = Server.MaxConnections;
        bool hadPreviousSlot = Server.clients.TryGetValue(mover.id, out Client? previousSlot);
        byte[] ownerToken = mover.tcp.SessionToken!;
        byte[] peerToken = peer.tcp.SessionToken!;

        ThreadManager.Initialize();
        ThreadManager.ResetStats();
        Server.clients[mover.id] = mover;
        connectionLimit.SetValue(null, 2);
        udpSocketField.SetValue(null, udpSender);
        try
        {
            mover.tcp.Attach(ownerConnection);
            peer.tcp.Attach(peerConnection);
            mover.tcp.StartReceiving();
            peer.tcp.StartReceiving();
            using (Packet hello = await ReadFrameAsync(ownerStream, cancellation))
                Assert.Equal((ProtocolV4.Version, mover.id), ProtocolV2.ReadServerHello(hello));
            using (Packet hello = await ReadFrameAsync(peerStream, cancellation))
                Assert.Equal((ProtocolV4.Version, peer.id), ProtocolV2.ReadServerHello(hello));

            mover.tcp.AcceptedProtocolVersion = ProtocolV3.Version;
            SendTcp(100, 0.06f);
            SendUdp(100, 0.06f);
            Assert.Equal(0, ThreadManager.GetWaitingActionCount());
            Assert.Equal((0L, 0L), Watermarks);
            mover.tcp.AcceptedProtocolVersion = ProtocolV4.Version;
            using (var legacy = ProtocolV2.CreateUdpPlayerMovement(mover.id, ownerToken, room.Id, room.RoundId, 0.06f, 0f))
                udpDispatch.Invoke(null, new object[] { legacy.ToArray(), endpoint });
            using (var legacy = new Packet((int)ClientPackets.playerMovement))
            {
                legacy.Write(room.Id);
                legacy.Write(room.RoundId);
                legacy.Write(0.06f);
                legacy.Write(0f);
                Seal(legacy);
                tcpDispatch.Invoke(mover.tcp, new object[] { legacy.ToArray() });
            }
            Assert.Equal(0, ThreadManager.GetWaitingActionCount());

            SendMalformed((int)ClientPackets.playerMovementV4, udp: false);
            SendMalformed((int)ClientPackets.udpPlayerMovementV4, udp: true);
            Assert.Equal(0, ThreadManager.GetWaitingActionCount());
            Assert.Equal((0L, 0L), Watermarks);

            peer.udp.endPoint = (IPEndPoint)udpPeer.Client.LocalEndPoint!;
            SendUdp(11, 0.06f);
            ThreadManager.UpdateMain();
            using (var broadcast = ProtocolV4.OpenServerDatagram((await udpPeer.ReceiveAsync(cancellation)).Buffer))
            {
                var position = ProtocolV4.ReadUdpPlayerPosition(broadcast);
                Assert.Equal(peerToken, position.Token);
                Assert.Equal((room.Id, room.RoundId, mover.id, 11L, 0.06f, 0f),
                    (position.RoomId, position.RoundId, position.PlayerId, position.AcceptedMoveSeq, position.X, position.Y));
            }
            using (Packet ack = await ReadFrameAsync(ownerStream, cancellation))
                Assert.Equal((room.Id, room.RoundId, mover.id, 11L, 0.06f, 0f), ProtocolV4.ReadTcpPlayerPosition(ack));

            peer.udp.endPoint = null; // Exercise the TCP fallback for delivery to the peer.
            SendTcp(12, 0.12f);
            SendUdp(11, 0.06f);
            SendTcp(12, 0.12f);
            SendTcp(11, float.NaN);
            Assert.Equal(4, ThreadManager.GetWaitingActionCount());
            ThreadManager.UpdateMain();
            Assert.Equal((12L, 12L), Watermarks);
            ServerSend.MatchState(room); // FIFO marker detects any extra legacy or v4 movement frames.
            using (Packet broadcast = await ReadFrameAsync(peerStream, cancellation))
                Assert.Equal((room.Id, room.RoundId, mover.id, 12L, 0.12f, 0f), ProtocolV4.ReadTcpPlayerPosition(broadcast));
            using (Packet ack = await ReadFrameAsync(ownerStream, cancellation))
                Assert.Equal((room.Id, room.RoundId, mover.id, 12L, 0.12f, 0f), ProtocolV4.ReadTcpPlayerPosition(ack));
            await ReadMatchStateAsync(ownerStream);
            await ReadMatchStateAsync(peerStream);

            SendUdp(13, 0.60f); // Above the C2 cap.
            SendTcp(14, float.PositiveInfinity);
            ThreadManager.UpdateMain();
            Assert.Equal((14L, 12L), Watermarks);
            Assert.Equal((0.12f, 0f), Position);
            ServerSend.MatchState(room);
            for (long rejected = 13; rejected <= 14; rejected++)
            {
                using Packet correction = await ReadFrameAsync(ownerStream, cancellation);
                Assert.Equal((room.Id, room.RoundId, mover.id, rejected, 12L, 0.12f, 0f),
                    ProtocolV4.ReadTcpPlayerCorrection(correction));
            }
            await ReadMatchStateAsync(ownerStream);
            await ReadMatchStateAsync(peerStream);
        }
        finally
        {
            mover.tcp.Close();
            peer.tcp.Close();
            ThreadManager.ResetStats();
            if (hadPreviousSlot) Server.clients[mover.id] = previousSlot!;
            else Server.clients.Remove(mover.id);
            connectionLimit.SetValue(null, previousLimit);
            udpSocketField.SetValue(null, previousUdpSocket);
        }

        void SendTcp(long sequence, float x)
        {
            using var packet = ProtocolV4.CreateTcpPlayerMovement(room.Id, room.RoundId, sequence, x, 0f);
            tcpDispatch.Invoke(mover.tcp, new object[] { packet.ToArray() });
        }

        void SendUdp(long sequence, float x)
        {
            using var packet = ProtocolV4.CreateUdpPlayerMovement(mover.id, ownerToken, room.Id, room.RoundId,
                sequence, x, 0f);
            udpDispatch.Invoke(null, new object[] { packet.ToArray(), endpoint });
        }

        void SendMalformed(int packetId, bool udp)
        {
            using var packet = new Packet(packetId);
            if (udp) packet.Write(ownerToken);
            packet.Write(room.Id);
            packet.Write(room.RoundId);
            packet.Write(long.MaxValue);
            packet.Write(0.06f);
            packet.Write(0f);
            packet.Write((byte)0); // Trailing byte within the same encrypted envelope length.
            Seal(packet);
            if (udp)
            {
                packet.InsertInt(mover.id);
                udpDispatch.Invoke(null, new object[] { packet.ToArray(), endpoint });
                return;
            }
            var error = Assert.Throws<TargetInvocationException>(() =>
                tcpDispatch.Invoke(mover.tcp, new object[] { packet.ToArray() }));
            Assert.IsType<InvalidDataException>(error.InnerException);
        }

        async Task ReadMatchStateAsync(NetworkStream stream)
        {
            using Packet marker = await ReadFrameAsync(stream, cancellation);
            Assert.Equal((int)ServerPackets.matchState, marker.ReadInt());
        }
    }

    [Fact]
    [Trait("Category", "SocketIntegration")]
    public async Task OwnerReceivesAcceptRejectAcceptInProcessingOrderWithHeartbeatOnTheSameQueue()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        CancellationToken cancellation = timeout.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var owner = new TcpClient();
        await owner.ConnectAsync((IPEndPoint)listener.LocalEndpoint, cancellation);
        using TcpClient connection = await listener.AcceptTcpClientAsync(cancellation);
        mover.tcp.Attach(connection);
        mover.tcp.StartReceiving();
        try
        {
            var stream = owner.GetStream();
            using (Packet hello = await ReadFrameAsync(stream, cancellation))
                Assert.Equal((ProtocolV4.Version, mover.id), ProtocolV2.ReadServerHello(hello));

            Move(11, 0.08f, ms: 0, viaUdp: true);
            Move(12, 0.16f, ms: 1);
            Assert.Equal((12L, 11L), Watermarks);
            Move(13, 0.24f, ms: 40, viaUdp: true);
            Move(12, 0.16f, ms: 40);
            Move(13, 0.24f, ms: 40);
            // Sequence 14 was lost before the server. No frame for it may appear before the FIFO markers.
            Assert.Equal((13L, 13L), Watermarks);
            Assert.Equal((0.24f, 0f), Position);
            mover.tcp.StartHeartbeat(0);
            mover.tcp.SendHeartbeatIfDue(Stopwatch.Frequency * 5);
            ServerSend.MatchState(room);

            using (Packet ack = await ReadFrameAsync(stream, cancellation))
                Assert.Equal((room.Id, room.RoundId, mover.id, 11L, 0.08f, 0f), ProtocolV4.ReadTcpPlayerPosition(ack));
            using (Packet correction = await ReadFrameAsync(stream, cancellation))
                Assert.Equal((room.Id, room.RoundId, mover.id, 12L, 11L, 0.08f, 0f),
                    ProtocolV4.ReadTcpPlayerCorrection(correction));
            using (Packet ack = await ReadFrameAsync(stream, cancellation))
                Assert.Equal((room.Id, room.RoundId, mover.id, 13L, 0.24f, 0f), ProtocolV4.ReadTcpPlayerPosition(ack));
            using (Packet ping = await ReadFrameAsync(stream, cancellation))
                Assert.Equal(1L, ProtocolV3.ReadHeartbeatPing(ping));
            using Packet marker = await ReadFrameAsync(stream, cancellation);
            Assert.Equal((int)ServerPackets.matchState, marker.ReadInt());
        }
        finally
        {
            mover.tcp.Close();
        }
    }

    [Theory]
    [InlineData(0)] // Original UDP datagram lost
    [InlineData(1)] // Original UDP arrives before TCP
    [InlineData(2)] // TCP arrives before original UDP
    [Trait("Category", "SocketIntegration")]
    public async Task TailRetryAcceptsOnceAndFinalRefreshReachesPeerThatMissedUdp(int arrivalOrder)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        CancellationToken token = timeout.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var owner = new TcpClient();
        using var recipient = new TcpClient();
        await owner.ConnectAsync((IPEndPoint)listener.LocalEndpoint, token);
        await recipient.ConnectAsync((IPEndPoint)listener.LocalEndpoint, token);
        using TcpClient ownerConnection = await listener.AcceptTcpClientAsync(token);
        using TcpClient recipientConnection = await listener.AcceptTcpClientAsync(token);
        using var udpSender = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var udpRecipient = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var udpField = typeof(Server).GetField("udpSocket", BindingFlags.Static | BindingFlags.NonPublic)!;
        object? previousUdp = udpField.GetValue(null);
        udpField.SetValue(null, udpSender);
        try
        {
            mover.tcp.Attach(ownerConnection);
            peer.tcp.Attach(recipientConnection);
            mover.tcp.StartReceiving();
            peer.tcp.StartReceiving();
            var ownerStream = owner.GetStream();
            var peerStream = recipient.GetStream();
            using (Packet hello = await ReadFrameAsync(ownerStream, token))
                Assert.Equal((ProtocolV4.Version, mover.id), ProtocolV2.ReadServerHello(hello));
            using (Packet hello = await ReadFrameAsync(peerStream, token))
                Assert.Equal((ProtocolV4.Version, peer.id), ProtocolV2.ReadServerHello(hello));
            peer.udp.endPoint = (IPEndPoint)udpRecipient.Client.LocalEndPoint!;

            if (arrivalOrder == 1) Move(11, 0.08f, viaUdp: true);
            Move(11, 0.08f);
            if (arrivalOrder == 2) Move(11, 0.08f, viaUdp: true);
            // If a stale datagram were incorrectly applied, the subsequent affordable step would fail.
            Move(11, 0.04f, viaUdp: true);

            Assert.Equal((11L, 11L), Watermarks);
            Assert.Equal((0.08f, 0f), Position);
            using (Packet ack = await ReadFrameAsync(ownerStream, token))
                Assert.Equal((room.Id, room.RoundId, mover.id, 11L, 0.08f, 0f), ProtocolV4.ReadTcpPlayerPosition(ack));
            // Simulate loss at the receiving peer by discarding the only UDP snapshot.
            await udpRecipient.ReceiveAsync(token);
            using (var quiet = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                quiet.CancelAfter(TimeSpan.FromMilliseconds(70));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                    await udpRecipient.ReceiveAsync(quiet.Token));
            }
            token.ThrowIfCancellationRequested();

            room.UpdateRound(Stopwatch.Frequency * 199 / 1000);
            ServerSend.MatchState(room);
            await AssertMatchStateAsync(ownerStream);
            await AssertMatchStateAsync(peerStream);

            room.UpdateRound(Stopwatch.Frequency / 5);
            using (Packet refresh = await ReadFrameAsync(peerStream, token))
                Assert.Equal((room.Id, room.RoundId, mover.id, 11L, 0.08f, 0f), ProtocolV4.ReadTcpPlayerPosition(refresh));

            room.UpdateRound(Stopwatch.Frequency * 10);
            ServerSend.MatchState(room);
            await AssertMatchStateAsync(ownerStream);
            await AssertMatchStateAsync(peerStream);
            Move(12, 0.16f, ms: 20);
            Assert.Equal((12L, 12L), Watermarks);
            Assert.Equal((0.16f, 0f), Position);
        }
        finally
        {
            mover.tcp.Close();
            peer.tcp.Close();
            udpField.SetValue(null, previousUdp);
        }

        async Task AssertMatchStateAsync(NetworkStream stream)
        {
            using Packet frame = await ReadFrameAsync(stream, token);
            Assert.Equal((int)ServerPackets.matchState, frame.ReadInt());
        }
    }

    [Theory]
    [InlineData("terrain")]
    [InlineData("bomb")]
    [InlineData("speed")]
    public void TcpTailRetryUsesTheSameValidationAndPreservesCreditOnReject(string rejection)
    {
        if (rejection == "terrain") room.mapData[9, 8] = 1;
        if (rejection == "bomb")
        {
            (peer.player!.x, peer.player.y) = (0.5f, 0.5f);
            room.PlaceBomb(peer, room.Id, room.RoundId);
        }
        bool speed = rejection == "speed";
        (Player.x, Player.y) = (-0.32f, 0.5f);
        float target = speed ? -0.66f : -0.24f;
        Assert.Equal(speed, room.CanMove(Player, target, 0.5f));
        Move(11, target, 0.5f, ms: 250);
        Assert.Equal((11L, 0L), Watermarks);
        Assert.Equal((-0.32f, 0.5f), Position);

        Move(11, -0.40f, 0.5f, ms: 250, viaUdp: true);
        Assert.Equal((11L, 0L), Watermarks);
        Move(12, -0.64f, 0.5f, ms: 250);
        Assert.Equal((12L, 12L), Watermarks);
        Assert.Equal((-0.64f, 0.5f), Position);
    }

    [Fact]
    public void NewAcceptMovesRefreshDeadlineAndNewRoundDropsThePreviousPlayerSchedule()
    {
        var delay = TimeSpan.FromMilliseconds(200);
        Move(11, 0.08f);
        Move(12, 0.16f, ms: 150);
        Assert.False(Player.TryTakeFinalPositionRefresh(Stopwatch.Frequency / 5, delay));
        Assert.True(Player.TryTakeFinalPositionRefresh(Stopwatch.Frequency, delay));
        Assert.False(Player.TryTakeFinalPositionRefresh(2 * Stopwatch.Frequency, delay));

        Move(13, 0.24f, ms: 1000);
        peer.player!.isAlive = false;
        room.UpdateRound(Stopwatch.Frequency);
        Assert.Equal(MatchPhase.Finished, room.Phase);
        room.StartRound(mover, room.Id, room.RoundId, 2 * Stopwatch.Frequency);
        Assert.False(Player.TryTakeFinalPositionRefresh(10 * Stopwatch.Frequency, delay));
        Assert.Equal((0L, 0L), Watermarks);
    }

    private static void Seal(Packet packet)
    {
        packet.WriteLength();
        packet.EncryptPayload();
    }

    private static async Task<Packet> ReadFrameAsync(NetworkStream stream, CancellationToken cancellation)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellation);
        int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header);
        Assert.InRange(payloadLength, 64, 16_384);
        byte[] frame = new byte[header.Length + payloadLength];
        header.CopyTo(frame, 0);
        await stream.ReadExactlyAsync(frame.AsMemory(header.Length), cancellation);
        var packet = new Packet(frame);
        packet.DecryptPayload();
        packet.ReadInt();
        return packet;
    }

    private void Move(long sequence, float x, float y = 0, int ms = 0, bool viaUdp = false)
    {
        long now = Stopwatch.Frequency * ms / 1000;
        if (viaUdp)
            ServerHandle.UdpMovement(mover, mover.tcp, mover.udp, endpoint, mover.tcp.SessionToken!,
                room.Id, room.RoundId, x, y, now, sequence);
        else
            ServerHandle.PlayerMovement(mover, room.Id, room.RoundId, x, y, now, sequence);
    }

    private static void MakeReady(Client slot)
    {
        slot.Username = $"Player {slot.id}";
        slot.UdpConfirmed = true;
        slot.tcp.AcceptedProtocolVersion = ProtocolV4.Version;
        slot.tcp.SessionToken = ProtocolV2.NewSessionToken();
    }
}
