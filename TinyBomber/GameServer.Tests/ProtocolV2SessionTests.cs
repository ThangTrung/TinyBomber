using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using GameShared;

namespace GameServer.Tests;

[Collection("Network metrics server state")]
public sealed class ProtocolV2SessionTests
{
    [Theory]
    [InlineData(1, 42)]
    [InlineData(2, 42)]
    [InlineData(ProtocolV3.Version, 42)]
    [InlineData(ProtocolV4.Version, 43)]
    public void ClientHelloWithWrongVersionOrSlotReleasesConnectionBeforeGrant(int version, int slotId)
    {
        var slot = new Client(42);
        var connection = slot.tcp;
        using var socket = new TcpClient();
        var handle = socket.Client.SafeHandle;
        connection.socket = socket;
        connection.StartHandshake(100);
        Server.clients.Add(slot.id, slot);
        try
        {
            ServerHandle.ClientHello(slot, connection, version, slotId, "Player", 100);

            Assert.Null(connection.SessionToken);
            Assert.Null(slot.Username);
            Assert.Null(slot.udp.endPoint);
            Assert.NotSame(connection, slot.tcp);
            Assert.True(handle.IsClosed);
        }
        finally
        {
            Server.clients.Remove(slot.id);
        }
    }

    [Fact]
    public void DuplicateHelloAndRegistrationRetainTheTokenAndNonce()
    {
        var slot = new Client(1);
        var endpoint = new IPEndPoint(IPAddress.Loopback, 12345);
        slot.tcp.StartHandshake(100);

        Assert.False(ServerHandle.RegisterUdp(slot, slot.tcp, slot.udp, endpoint, new byte[16], 100));
        Assert.Null(slot.udp.endPoint);
        ServerHandle.ClientHello(slot, slot.tcp, ProtocolV4.Version, slot.id, "Player", 100);
        byte[] token = Assert.IsType<byte[]>(slot.tcp.SessionToken);
        ServerHandle.ClientHello(slot, slot.tcp, ProtocolV4.Version, slot.id, "Repeated", 101);
        Assert.Same(token, slot.tcp.SessionToken);
        Assert.Equal("Player", slot.Username);

        Assert.True(ServerHandle.RegisterUdp(slot, slot.tcp, slot.udp, endpoint, token, 101));
        byte[] nonce = Assert.IsType<byte[]>(slot.udp.ProbeNonce);
        Assert.True(ServerHandle.RegisterUdp(slot, slot.tcp, slot.udp, endpoint, token, 102));
        Assert.Same(nonce, slot.udp.ProbeNonce);
        Assert.False(ServerHandle.RegisterUdp(slot, slot.tcp, slot.udp,
            new IPEndPoint(IPAddress.Loopback, endpoint.Port + 1), token, 102));
        Assert.Equal(endpoint, slot.udp.endPoint);
    }

    [Fact]
    public void ConfirmRequiresCurrentWrapperValidTokenAndBoundEndpointNonce()
    {
        var slot = new Client(1);
        var endpoint = new IPEndPoint(IPAddress.Loopback, 12345);
        slot.tcp.StartHandshake(100);
        ServerHandle.ClientHello(slot, slot.tcp, ProtocolV4.Version, slot.id, "Player", 100);
        byte[] token = slot.tcp.SessionToken!;
        ServerHandle.ConfirmUdp(slot, slot.tcp, token, new byte[16], 101);
        Assert.False(slot.IsReady);

        Assert.True(ServerHandle.RegisterUdp(slot, slot.tcp, slot.udp, endpoint, token, 101));
        byte[] nonce = slot.udp.ProbeNonce!;
        ServerHandle.ConfirmUdp(slot, new Client.TCP(slot.id), token, nonce, 102);
        ServerHandle.ConfirmUdp(slot, slot.tcp, Different(token), nonce, 102);
        ServerHandle.ConfirmUdp(slot, slot.tcp, token, Different(nonce), 102);
        Assert.False(slot.IsReady);

        ServerHandle.ConfirmUdp(slot, slot.tcp, token, nonce, 102);
        Assert.True(slot.IsReady);
        Assert.False(ServerHandle.RegisterUdp(slot, slot.tcp, slot.udp, endpoint, token, 103));
        Assert.Equal(endpoint, slot.udp.endPoint);
    }

    [Fact]
    public void QueuedTcpFrameFromOldConnectionCannotCreateRoomOnReplacement()
    {
        var slot = new Client(int.MaxValue);
        var first = slot.tcp;
        MethodInfo handleFrame = typeof(Client.TCP).GetMethod("HandleFrame",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        using var createRoom = new Packet((int)ClientPackets.createRoom);
        createRoom.WriteLength();
        createRoom.EncryptPayload();
        byte[] frame = createRoom.ToArray();

        ThreadManager.Initialize();
        Server.clients.Add(slot.id, slot);
        try
        {
            handleFrame.Invoke(first, new object[] { frame });
            Assert.Equal(1, ThreadManager.GetWaitingActionCount());

            Server.Disconnect(slot.id, first);
            var next = slot.tcp;
            Assert.NotSame(first, next);
            ServerHandle.ClientHello(slot, next, ProtocolV4.Version, slot.id, "Replacement", 100);
            byte[] token = next.SessionToken!;
            Assert.True(ServerHandle.RegisterUdp(slot, next, slot.udp,
                new IPEndPoint(IPAddress.Loopback, 12345), token, 100));
            ServerHandle.ConfirmUdp(slot, next, token, slot.udp.ProbeNonce!, 100);
            Assert.True(slot.IsReady);

            ThreadManager.UpdateMain();

            Assert.Same(next, slot.tcp);
            Assert.True(slot.IsReady);
            Assert.Null(slot.Room);

            handleFrame.Invoke(next, new object[] { frame });
            ThreadManager.UpdateMain();
            Assert.Contains(slot, Assert.IsType<GameLogic>(slot.Room).Members);
        }
        finally
        {
            Server.Disconnect(slot.id, slot.tcp);
            Server.clients.Remove(slot.id);
        }
    }

    [Fact]
    public async Task BroadcastFallbackCarriesV4PeerPositionAndAcceptedSequence()
    {
        var mover = new Client(1) { Username = "Mover", UdpConfirmed = true };
        var recipient = new Client(2) { Username = "Recipient", UdpConfirmed = true };
        var room = new GameLogic(42);
        Assert.True(room.TryAddMember(mover));
        Assert.True(room.TryAddMember(recipient));
        room.StartRound(mover, room.Id, 0);
        Player player = Assert.IsType<Player>(mover.player);
        recipient.UdpConfirmed = false;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        CancellationToken token = timeout.Token;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            using var peer = new TcpClient();
            await peer.ConnectAsync((IPEndPoint)listener.LocalEndpoint, token);
            using TcpClient accepted = await listener.AcceptTcpClientAsync(token);
            recipient.tcp.Attach(accepted);
            recipient.tcp.StartReceiving();
            try
            {
                using Packet hello = await ReadFrameAsync(peer.GetStream(), token);
                Assert.Equal((ProtocolV4.Version, recipient.id), ProtocolV2.ReadServerHello(hello));

                recipient.tcp.AcceptedProtocolVersion = ProtocolV4.Version;
                Assert.True(player.TryMarkMovementProcessed(11));
                player.AcceptMovement(player.x, player.y, 11);
                ServerSend.BroadcastPlayerPosition(room, player);

                using Packet position = await ReadFrameAsync(peer.GetStream(), token);
                Assert.Equal((room.Id, room.RoundId, mover.id, 11L, player.x, player.y),
                    ProtocolV4.ReadTcpPlayerPosition(position));
                Assert.Equal(0, position.UnreadLength());
            }
            finally
            {
                recipient.tcp.Close();
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    [Trait("Category", "LoadClientIntegration")]
    public async Task ReplacementLoadClientIgnoresOldProbeAndPosition()
    {
        Assembly clientAssembly = LoadClientAssembly();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        CancellationToken token = timeout.Token;
        var address = IPAddress.Parse("127.0.0.3");
        using var udp = new UdpClient(new IPEndPoint(address, Constants.UDP_PORT));
        var listener = new TcpListener(address, Constants.PORT);
        listener.Start();
        try
        {
            const int slot = 1;
            const int actor = 2;
            const int room = 7;
            const int round = 1;
            const float oldX = 2f;
            const float newX = 3f;
            const float y = 1f;
            byte[] firstToken = ProtocolV2.NewSessionToken();
            byte[] firstNonce = ProtocolV2.NewProbeNonce();
            using var staleProbe = ProtocolV2.CreateUdpProbe(firstToken, firstNonce);
            using var stalePosition = ProtocolV4.CreateUdpPlayerPosition(firstToken, room, round, actor, long.MaxValue, oldX, y);

            await using (IAsyncDisposable first = NewLoadClient(clientAssembly, token))
            {
                var connect = (Task)Invoke(first, "ConnectAsync", address)!;
                using TcpClient tcp = await listener.AcceptTcpClientAsync(token);
                IPEndPoint endpoint = await GrantLoadClientAsync(tcp.GetStream(), udp, slot, firstToken, token);
                await udp.SendAsync(staleProbe.ToArray(), endpoint, token);
                await ConfirmLoadClientAsync(tcp.GetStream(), firstToken, firstNonce, token);
                await connect.WaitAsync(token);
                Assert.True(ReadReady(first));

                var checkedHealth = (Task<bool>)Invoke(first, "ProbeUdpHealthAsync", token)!;
                var health = await ReadHealthProbeAsync(udp, token);
                Assert.Equal(endpoint, health.Endpoint);
                using var echo = ProtocolV3.CreateUdpHealthEcho(firstToken, health.Sequence);
                await udp.SendAsync(echo.ToArray(), endpoint, token);
                Assert.True(await checkedHealth.WaitAsync(token));
                Assert.Equal(1L, ReadHealth<long>(first, "EchoesAccepted"));
            }

            await using IAsyncDisposable replacement = NewLoadClient(clientAssembly, token);
            Task reconnect = (Task)Invoke(replacement, "ConnectAsync", address)!;
            using TcpClient next = await listener.AcceptTcpClientAsync(token);
            NetworkStream stream = next.GetStream();
            byte[] nextToken = Different(firstToken);
            byte[] nextNonce = Different(firstNonce);
            IPEndPoint nextEndpoint = await GrantLoadClientAsync(stream, udp, slot, nextToken, token);

            // Deliver the old wire bytes, then wait for the replacement's registration retry.
            await udp.SendAsync(staleProbe.ToArray(), nextEndpoint, token);
            Assert.Equal(nextEndpoint, await ReadRegistrationAsync(udp, slot, nextToken, token));
            using var currentProbe = ProtocolV2.CreateUdpProbe(nextToken, nextNonce);
            await udp.SendAsync(currentProbe.ToArray(), nextEndpoint, token);
            await ConfirmLoadClientAsync(stream, nextToken, nextNonce, token);
            await reconnect.WaitAsync(token);
            Assert.True(ReadReady(replacement));
            Assert.Equal(0L, ReadHealth<long>(replacement, "EchoesAccepted"));
            Assert.False(ReadHealth<bool>(replacement, "IsDegraded"));

            var replacementHealth = (Task<bool>)Invoke(replacement, "ProbeUdpHealthAsync", token)!;
            var newHealth = await ReadHealthProbeAsync(udp, token);
            Assert.Equal((nextEndpoint, 1L), (newHealth.Endpoint, newHealth.Sequence));
            using var retiredEcho = ProtocolV3.CreateUdpHealthEcho(firstToken, newHealth.Sequence);
            Invoke(replacement, "HandleUdpPacket", retiredEcho.ToArray());
            Assert.False(replacementHealth.IsCompleted);
            using var currentEcho = ProtocolV3.CreateUdpHealthEcho(nextToken, newHealth.Sequence);
            await udp.SendAsync(currentEcho.ToArray(), nextEndpoint, token);
            Assert.True(await replacementHealth.WaitAsync(token));

            await SendServerPacketAsync(stream, ServerPackets.roomChanged, new[] { room }, token);
            await SendServerPacketAsync(stream, ServerPackets.roundBegin, new[] { room, round, 2, slot, actor }, token);
            await SendSpawnAsync(stream, room, round, slot, 0.5f, y, token);
            await SendSpawnAsync(stream, room, round, actor, 1f, y, token);
            await SendServerPacketAsync(stream, ServerPackets.matchState,
                new[] { room, round, (int)MatchPhase.Playing, 2, 0 }, token);
            while ((MatchPhase)ReadProperty(Invoke(replacement, "ReadState")!, "Phase") != MatchPhase.Playing)
                await Task.Delay(10, token);

            object trace = ReadProperty(replacement, "Trace");
            Type positionType = clientAssembly.GetType("GameServer.LoadClient.Position", throwOnError: true)!;
            object origin = Activator.CreateInstance(positionType, 1f, y)!;
            object oldTarget = Activator.CreateInstance(positionType, oldX, y)!;
            object newTarget = Activator.CreateInstance(positionType, newX, y)!;
            Invoke(trace, "Prepare", origin, oldTarget, true);
            Invoke(trace, "MarkSent", oldTarget, 0d);
            Invoke(trace, "Prepare", origin, newTarget, true);
            Invoke(trace, "MarkSent", newTarget, 0d);
            Invoke(replacement, "Watch", actor, trace);

            // Run the actual receive handler synchronously once so the negative assertion cannot race socket delivery.
            Invoke(replacement, "HandleUdpPacket", stalePosition.ToArray());
            Assert.Equal(0, ReadObserved(trace));
            await udp.SendAsync(stalePosition.ToArray(), nextEndpoint, token);
            using var currentPosition = ProtocolV4.CreateUdpPlayerPosition(nextToken, room, round, actor, 1, newX, y);
            await udp.SendAsync(currentPosition.ToArray(), nextEndpoint, token);
            while (ReadObserved(trace) == 0)
                await Task.Delay(10, token);
            object counts = Invoke(trace, "Read")!;
            Assert.Equal(1, ReadProperty(counts, "Observed"));
            Assert.Equal(newTarget, ReadProperty(ReadProperty(counts, "FirstMatch"), "Sent"));
            Assert.True(ReadReady(replacement));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Theory]
    [InlineData(ProtocolV2.Version, false)]
    [InlineData(ProtocolV3.Version, false)]
    [InlineData(ProtocolV4.Version, true)]
    [Trait("Category", "LoadClientIntegration")]
    public async Task V4LoadClientClosesBeforeClientHelloOnOldOrMalformedServerHello(int version, bool malformed)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        CancellationToken token = timeout.Token;
        var address = IPAddress.Parse("127.0.0.3");
        using var listener = new TcpListener(address, Constants.PORT);
        listener.Start();
        await using var client = NewLoadClient(LoadClientAssembly(), token);
        var connect = (Task)Invoke(client, "ConnectAsync", address)!;
        using TcpClient peer = await listener.AcceptTcpClientAsync(token);
        NetworkStream stream = peer.GetStream();
        if (malformed)
            await SendServerPacketAsync(stream, ServerPackets.serverHello, [version], token);
        else
        {
            using var hello = ProtocolV2.CreateServerHello(1, version);
            await stream.WriteAsync(hello.ToArray(), token);
        }

        Assert.Equal(0, await stream.ReadAsync(new byte[1], token));
        await connect.WaitAsync(token);
        Assert.False(ReadReady(client));
        var errors = ReadField<object>(client, "errors");
        string detail = Assert.Single((string[])Invoke(errors, "Details")!);
        Assert.StartsWith(malformed ? "Receive:" : "Connection:", detail);
        Assert.Contains(malformed ? "Invalid v2 packet payload length" : $"protocol v{version}. Client yêu cầu v4", detail);
    }

    [Fact]
    [Trait("Category", "LoadClientIntegration")]
    public async Task LoadClientSerializesPongAndRoomCommandThenTimesOutWhileServerKeepsTcpOpen()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        CancellationToken token = timeout.Token;
        var address = IPAddress.Parse("127.0.0.3");
        using var udp = new UdpClient(new IPEndPoint(address, Constants.UDP_PORT));
        using var listener = new TcpListener(address, Constants.PORT);
        listener.Start();
        await using var client = NewLoadClient(LoadClientAssembly(), token);
        var connect = (Task)Invoke(client, "ConnectAsync", address)!;
        using TcpClient peer = await listener.AcceptTcpClientAsync(token);
        NetworkStream stream = peer.GetStream();
        byte[] session = ProtocolV2.NewSessionToken();
        byte[] nonce = ProtocolV2.NewProbeNonce();
        IPEndPoint endpoint = await GrantLoadClientAsync(stream, udp, 1, session, token);
        using (var probe = ProtocolV2.CreateUdpProbe(session, nonce))
            await udp.SendAsync(probe.ToArray(), endpoint, token);
        await ConfirmLoadClientAsync(stream, session, nonce, token);
        await connect.WaitAsync(token);
        Assert.True(ReadReady(client));

        var gate = ReadField<SemaphoreSlim>(client, "tcpWriteGate");
        var heartbeat = ReadField<HeartbeatWatchdog>(client, "heartbeat");
        var stateLock = ReadField<object>(client, "stateLock");
        long? ReadDeadline() { lock (stateLock) return heartbeat.Deadline; }
        long? startDeadline = ReadDeadline();
        const long sequence = 41;
        long sentAt;
        Task command;
        await gate.WaitAsync(token);
        try
        {
            using var cancelBeforeWrite = CancellationTokenSource.CreateLinkedTokenSource(token);
            var canceled = (Task)Invoke(client, "SendTcpAsync", ClientPackets.leaveRoom, null!, cancelBeforeWrite.Token)!;
            cancelBeforeWrite.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
            Assert.True(ReadReady(client));

            command = (Task)Invoke(client, "SendTcpAsync", ClientPackets.createRoom, null!, token)!;
            using var ping = ProtocolV3.CreateHeartbeatPing(sequence);
            sentAt = Stopwatch.GetTimestamp();
            await stream.WriteAsync(ping.ToArray(), token);
            while (ReadDeadline() == startDeadline) await Task.Delay(5, token);
            Assert.False(command.IsCompleted);
            Assert.False(stream.DataAvailable);
        }
        finally { gate.Release(); }

        var types = new HashSet<ClientPackets>();
        for (int i = 0; i < 2; i++)
        {
            using Packet packet = await ReadFrameAsync(stream, token);
            var type = (ClientPackets)packet.ReadInt(false);
            Assert.True(types.Add(type));
            if (type == ClientPackets.heartbeatPong) Assert.Equal(sequence, ProtocolV3.ReadHeartbeatPong(packet));
            else Assert.Equal((int)ClientPackets.createRoom, packet.ReadInt());
            Assert.Equal(0, packet.UnreadLength());
        }
        Assert.True(types.SetEquals(new[] { ClientPackets.heartbeatPong, ClientPackets.createRoom }));
        await command.WaitAsync(token);

        await AssertLoadClientHealthEchoAsync(client, udp, endpoint, session, token);

        // Keep the server socket open and silent. EOF must come from the LoadClient watchdog.
        var clientTcp = ReadField<TcpClient>(client, "tcp").Client.SafeHandle;
        var clientUdp = ReadField<UdpClient>(client, "udp").Client.SafeHandle;
        Assert.Equal(0, await stream.ReadAsync(new byte[1], token));
        Assert.True(Stopwatch.GetElapsedTime(sentAt) >= TimeSpan.FromSeconds(25));
        Assert.False(ReadReady(client));
        await ReadField<Task>(client, "heartbeatMonitor").WaitAsync(token);
        Assert.True(clientTcp.IsClosed);
        Assert.True(clientUdp.IsClosed);
        var errors = ReadField<object>(client, "errors");
        Assert.StartsWith("Timeout:", Assert.Single((string[])Invoke(errors, "Details")!));
        Assert.Equal(1, ReadProperty(errors, "Total"));
    }

    private static async Task AssertLoadClientHealthEchoAsync(IAsyncDisposable client, UdpClient udp,
        IPEndPoint endpoint, byte[] session, CancellationToken token)
    {
        var pending = (Task<bool>)Invoke(client, "ProbeUdpHealthAsync", token)!;
        var request = await udp.ReceiveAsync(token);
        Assert.Equal(endpoint, request.RemoteEndPoint);
        using Packet packet = ProtocolV2.OpenClientDatagram(request.Buffer, out int slotId);
        var health = ProtocolV3.ReadUdpHealthProbe(packet);
        Assert.Equal(1, slotId);
        Assert.Equal(session, health.Token);
        Assert.False(await (Task<bool>)Invoke(client, "ProbeUdpHealthAsync", token)!);

        using var wrongToken = ProtocolV3.CreateUdpHealthEcho(Different(session), health.Sequence);
        using var wrongSequence = ProtocolV3.CreateUdpHealthEcho(session, health.Sequence + 1);
        using var right = ProtocolV3.CreateUdpHealthEcho(session, health.Sequence);
        Invoke(client, "HandleUdpPacket", wrongToken.ToArray());
        Invoke(client, "HandleUdpPacket", wrongSequence.ToArray());
        Assert.False(pending.IsCompleted);
        byte[] badHmac = right.ToArray();
        badHmac[^1] ^= 1;
        using var wrongEndpoint = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await wrongEndpoint.SendAsync(right.ToArray(), endpoint, token);
        await udp.SendAsync(badHmac, endpoint, token);
        await udp.SendAsync(right.ToArray(), endpoint, token);
        Assert.True(await pending.WaitAsync(token));

        // A new request receives its matching echo only from the wrong source and must expire.
        long startedAt = Stopwatch.GetTimestamp();
        var expired = (Task<bool>)Invoke(client, "ProbeUdpHealthAsync", token)!;
        var nextRequest = await udp.ReceiveAsync(token);
        using Packet nextPacket = ProtocolV2.OpenClientDatagram(nextRequest.Buffer, out int nextSlotId);
        var next = ProtocolV3.ReadUdpHealthProbe(nextPacket);
        Assert.Equal(slotId, nextSlotId);
        Assert.Equal(session, next.Token);
        Assert.True(next.Sequence > health.Sequence);
        using var mismatchedEndpoint = ProtocolV3.CreateUdpHealthEcho(session, next.Sequence);
        await wrongEndpoint.SendAsync(mismatchedEndpoint.ToArray(), endpoint, token);
        await udp.SendAsync(right.ToArray(), endpoint, token);
        Assert.False(await expired.WaitAsync(token));
        Assert.True(Stopwatch.GetElapsedTime(startedAt) >= TimeSpan.FromSeconds(5));
        Assert.True(ReadReady(client));
    }

    [Fact]
    [Trait("Category", "LoadClientIntegration")]
    public async Task PeriodicUdpLossDegradesAndRecoversWithoutLeavingTcpSessionOrRound()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        CancellationToken token = timeout.Token;
        var address = IPAddress.Parse("127.0.0.3");
        using var udp = new UdpClient(new IPEndPoint(address, Constants.UDP_PORT));
        using var listener = new TcpListener(address, Constants.PORT);
        listener.Start();
        var peers = new Dictionary<IPEndPoint, (IAsyncDisposable Client, TcpClient Tcp, int Slot, byte[] Session)>();
        var clients = new List<IAsyncDisposable>();
        var sockets = new List<TcpClient>();
        const int room = 9;
        const int round = 1;
        try
        {
            // Relay policies by slot: drop outbound, drop inbound, drop both.
            for (int slot = 1; slot <= 3; slot++)
            {
                var client = NewLoadClient(LoadClientAssembly(), token);
                clients.Add(client);
                Task connect = (Task)Invoke(client, "ConnectAsync", address)!;
                TcpClient tcp = await listener.AcceptTcpClientAsync(token);
                sockets.Add(tcp);
                byte[] session = ProtocolV2.NewSessionToken();
                byte[] nonce = ProtocolV2.NewProbeNonce();
                NetworkStream stream = tcp.GetStream();
                IPEndPoint endpoint = await GrantLoadClientAsync(stream, udp, slot, session, token);
                using (var probe = ProtocolV2.CreateUdpProbe(session, nonce))
                    await udp.SendAsync(probe.ToArray(), endpoint, token);
                await ConfirmLoadClientAsync(stream, session, nonce, token);
                await connect.WaitAsync(token);
                peers.Add(endpoint, (client, tcp, slot, session));
                await SendServerPacketAsync(stream, ServerPackets.roomChanged, [room], token);
                await SendServerPacketAsync(stream, ServerPackets.roundBegin, [room, round, 3, 1, 2, 3], token);
                for (int actorSlot = 1; actorSlot <= 3; actorSlot++)
                    await SendSpawnAsync(stream, room, round, actorSlot, 1f, 1f, token);
                await SendServerPacketAsync(stream, ServerPackets.matchState, [room, round, (int)MatchPhase.Playing, 3, 0], token);
            }

            for (long sequence = 1; sequence <= 3; sequence++)
            {
                foreach (var peer in peers.Values)
                    await ExchangeHeartbeatAsync(peer.Tcp.GetStream(), sequence, token);

                var seen = new HashSet<IPEndPoint>();
                for (int i = 0; i < peers.Count; i++)
                {
                    var datagram = await udp.ReceiveAsync(token);
                    Assert.True(seen.Add(datagram.RemoteEndPoint));
                    var peer = peers[datagram.RemoteEndPoint];
                    bool dropProbe = peer.Slot is 1 or 3;
                    bool dropEcho = peer.Slot is 2 or 3;
                    if (!dropProbe)
                    {
                        using Packet packet = ProtocolV2.OpenClientDatagram(datagram.Buffer, out int slotId);
                        var health = ProtocolV3.ReadUdpHealthProbe(packet);
                        Assert.Equal(peer.Slot, slotId);
                        Assert.Equal(peer.Session, health.Token);
                        Assert.Equal(sequence, health.Sequence);
                        using var echo = ProtocolV3.CreateUdpHealthEcho(peer.Session, sequence);
                        if (!dropEcho) await udp.SendAsync(echo.ToArray(), datagram.RemoteEndPoint, token);
                    }
                    Assert.True(ReadReady(peer.Client));
                }
            }

            Assert.All(clients, client =>
            {
                Assert.True(ReadHealth<bool>(client, "IsDegraded"));
                Assert.Equal(0L, ReadHealth<long>(client, "EchoesAccepted"));
                Assert.Equal(1L, ReadHealth<long>(client, "Degradations"));
            });

            for (int i = 0; i < peers.Count; i++)
            {
                var request = await ReadHealthProbeAsync(udp, token);
                var peer = peers[request.Endpoint];
                Assert.Equal((peer.Slot, 4L), (request.Slot, request.Sequence));
                Assert.Equal(peer.Session, request.Token);
                using var echo = ProtocolV3.CreateUdpHealthEcho(peer.Session, request.Sequence);
                await udp.SendAsync(echo.ToArray(), request.Endpoint, token);
            }

            foreach (var peer in peers.Values)
            {
                while (ReadHealth<bool>(peer.Client, "IsDegraded")) await Task.Delay(5, token);
                Assert.Equal(1L, ReadHealth<long>(peer.Client, "Recoveries"));
                Assert.Equal(1L, ReadHealth<long>(peer.Client, "EchoesAccepted"));
                Assert.True(ReadReady(peer.Client));
                object state = Invoke(peer.Client, "ReadState")!;
                Assert.Equal((room, round, MatchPhase.Playing), ((int)ReadProperty(state, "RoomId"),
                    (int)ReadProperty(state, "RoundId"), (MatchPhase)ReadProperty(state, "Phase")));
                await ExchangeHeartbeatAsync(peer.Tcp.GetStream(), 4, token);
            }

            var sender = peers.First();
            var observer = peers.Skip(1).First();
            const float x = 1.0625f;
            const float y = 1f;
            object trace = ReadProperty(observer.Value.Client, "Trace");
            Type positionType = LoadClientAssembly().GetType("GameServer.LoadClient.Position", throwOnError: true)!;
            object origin = Activator.CreateInstance(positionType, 1f, y)!;
            object target = Activator.CreateInstance(positionType, x, y)!;
            Invoke(trace, "Prepare", origin, target, true);
            Invoke(trace, "MarkSent", target, 0d);
            Invoke(observer.Value.Client, "Watch", sender.Value.Slot, trace);
            using var movement = ProtocolV4.CreateUdpPlayerMovement(sender.Value.Slot, sender.Value.Session,
                room, round, 1, x, y);
            await (Task)Invoke(sender.Value.Client, "SendUdpAsync", movement.ToArray(), token)!;
            var moved = await udp.ReceiveAsync(token);
            Assert.Equal(sender.Key, moved.RemoteEndPoint);
            using Packet frame = ProtocolV2.OpenClientDatagram(moved.Buffer, out int actor);
            var move = ProtocolV4.ReadUdpPlayerMovement(frame);
            Assert.Equal(sender.Value.Slot, actor);
            Assert.Equal(sender.Value.Session, move.Token);
            Assert.Equal((room, round, 1L, x, y), (move.RoomId, move.RoundId, move.MoveSeq, move.X, move.Y));
            using var position = ProtocolV4.CreateUdpPlayerPosition(observer.Value.Session,
                room, round, actor, move.MoveSeq, move.X, move.Y);
            await udp.SendAsync(position.ToArray(), observer.Key, token);
            while (ReadObserved(trace) == 0) await Task.Delay(5, token);
            Assert.Equal(1, ReadObserved(trace));
        }
        finally
        {
            await Task.WhenAll(clients.Select(client => client.DisposeAsync().AsTask()));
            foreach (var tcp in sockets) tcp.Dispose();
        }
    }

    [Fact]
    [Trait("Category", "LoadClientIntegration")]
    public async Task RecoverableUdpReceiveFailureRearmsOnceAndTerminalFailureLeavesTcpReady()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        CancellationToken token = timeout.Token;
        var address = IPAddress.Parse("127.0.0.3");
        using var udp = new UdpClient(new IPEndPoint(address, Constants.UDP_PORT));
        using var listener = new TcpListener(address, Constants.PORT);
        listener.Start();
        int activeReads = 0;
        int readStarts = 0;
        int injectError = 0;
        async ValueTask<UdpReceiveResult> Receive(UdpClient socket, CancellationToken stop)
        {
            if (Interlocked.Increment(ref activeReads) != 1)
                throw new InvalidOperationException("Overlapping UDP receive.");
            Interlocked.Increment(ref readStarts);
            try
            {
                var datagram = await socket.ReceiveAsync(stop);
                int error = Interlocked.Exchange(ref injectError, 0);
                if (error != 0) throw new SocketException(error);
                return datagram;
            }
            finally { Interlocked.Decrement(ref activeReads); }
        }

        await using var client = NewLoadClient(LoadClientAssembly(), token, Receive);
        var connecting = (Task)Invoke(client, "ConnectAsync", address)!;
        using TcpClient peer = await listener.AcceptTcpClientAsync(token);
        NetworkStream stream = peer.GetStream();
        byte[] session = ProtocolV2.NewSessionToken();
        byte[] nonce = ProtocolV2.NewProbeNonce();
        IPEndPoint endpoint = await GrantLoadClientAsync(stream, udp, 1, session, token);
        using (var probe = ProtocolV2.CreateUdpProbe(session, nonce))
            await udp.SendAsync(probe.ToArray(), endpoint, token);
        await ConfirmLoadClientAsync(stream, session, nonce, token);
        await connecting.WaitAsync(token);

        var pending = (Task<bool>)Invoke(client, "ProbeUdpHealthAsync", token)!;
        var health = await ReadHealthProbeAsync(udp, token);
        using var echo = ProtocolV3.CreateUdpHealthEcho(session, health.Sequence);
        while (Volatile.Read(ref activeReads) == 0) await Task.Delay(5, token);
        int beforeError = Volatile.Read(ref readStarts);
        long sentAt = Stopwatch.GetTimestamp();
        Interlocked.Exchange(ref injectError, (int)SocketError.ConnectionReset);
        await udp.SendAsync(echo.ToArray(), endpoint, token);
        while (Volatile.Read(ref readStarts) == beforeError) await Task.Delay(5, token);
        Assert.Equal(beforeError + 1, Volatile.Read(ref readStarts));
        Assert.True(Stopwatch.GetElapsedTime(sentAt) >= TimeSpan.FromSeconds(1));
        Assert.False(pending.IsCompleted);
        await udp.SendAsync(echo.ToArray(), endpoint, token);
        Assert.True(await pending.WaitAsync(token));
        Assert.Equal(1L, ReadHealth<long>(client, "ReceiveErrors"));

        var next = (Task<bool>)Invoke(client, "ProbeUdpHealthAsync", token)!;
        var nextHealth = await ReadHealthProbeAsync(udp, token);
        var udpHandle = ReadField<UdpClient>(client, "udp").Client.SafeHandle;
        using var nextEcho = ProtocolV3.CreateUdpHealthEcho(session, nextHealth.Sequence);
        Interlocked.Exchange(ref injectError, (int)SocketError.NotSocket);
        await udp.SendAsync(nextEcho.ToArray(), endpoint, token);
        Assert.False(await next.WaitAsync(token));
        await ReadField<Task>(client, "receiveUdp").WaitAsync(token);
        Assert.True(udpHandle.IsClosed);
        Assert.Equal(0, Volatile.Read(ref activeReads));
        Assert.Equal(2L, ReadHealth<long>(client, "ReceiveErrors"));
        Assert.True(ReadHealth<bool>(client, "SocketUnavailable"));
        Assert.True(ReadHealth<bool>(client, "IsDegraded"));
        Assert.False(await (Task<bool>)Invoke(client, "ProbeUdpHealthAsync", token)!);
        Assert.True(ReadReady(client));
        await ExchangeHeartbeatAsync(stream, 1, token);
        Assert.Equal(0, ReadProperty(ReadField<object>(client, "errors"), "Total"));
    }

    private static T ReadHealth<T>(IAsyncDisposable client, string field) =>
        (T)ReadProperty(Invoke(client, "ReadUdpHealth")!, field);

    private static async Task<(IPEndPoint Endpoint, int Slot, byte[] Token, long Sequence)> ReadHealthProbeAsync(
        UdpClient udp, CancellationToken token)
    {
        var datagram = await udp.ReceiveAsync(token);
        using Packet packet = ProtocolV2.OpenClientDatagram(datagram.Buffer, out int slot);
        var health = ProtocolV3.ReadUdpHealthProbe(packet);
        return (datagram.RemoteEndPoint, slot, health.Token, health.Sequence);
    }

    private static async Task ExchangeHeartbeatAsync(NetworkStream stream, long sequence, CancellationToken token)
    {
        using var ping = ProtocolV3.CreateHeartbeatPing(sequence);
        await stream.WriteAsync(ping.ToArray(), token);
        using var pong = await ReadFrameAsync(stream, token);
        Assert.Equal(sequence, ProtocolV3.ReadHeartbeatPong(pong));
    }

    private static Assembly LoadClientAssembly()
    {
        // Load the executable produced by a solution build without changing the test project reference graph.
        var testOutput = new DirectoryInfo(AppContext.BaseDirectory);
        string clientDll = Path.GetFullPath(Path.Combine(testOutput.FullName, "..", "..", "..", "..",
            "GameServer.LoadClient", "bin", testOutput.Parent!.Name, testOutput.Name, "GameServer.LoadClient.dll"));
        Assert.True(File.Exists(clientDll), "Build TinyBomber.sln before running the LoadClient integration test.");
        return Assembly.LoadFrom(clientDll);
    }

    private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static IAsyncDisposable NewLoadClient(Assembly assembly, CancellationToken token,
        Func<UdpClient, CancellationToken, ValueTask<UdpReceiveResult>>? receiveDatagram = null)
    {
        Type errorType = assembly.GetType("GameServer.LoadClient.RunErrors", throwOnError: true)!;
        object errors = Activator.CreateInstance(errorType, nonPublic: true)!;
        Type clientType = assembly.GetType("GameServer.LoadClient.SimulatedClient", throwOnError: true)!;
        return (IAsyncDisposable)Activator.CreateInstance(clientType, InstanceMembers, null,
            new[] { (object?)1, errors, token, receiveDatagram }, null)!;
    }

    private static object? Invoke(object instance, string method, params object[] args) =>
        instance.GetType().GetMethod(method, InstanceMembers)!.Invoke(instance, args);

    private static object ReadProperty(object instance, string name) =>
        instance.GetType().GetProperty(name, InstanceMembers)!.GetValue(instance)!;

    private static T ReadField<T>(object instance, string name) =>
        (T)instance.GetType().GetField(name, InstanceMembers)!.GetValue(instance)!;

    private static int ReadObserved(object trace) => (int)ReadProperty(Invoke(trace, "Read")!, "Observed");

    private static bool ReadReady(IAsyncDisposable client) => (bool)ReadProperty(Invoke(client, "ReadState")!, "Ready");

    private static async Task<IPEndPoint> GrantLoadClientAsync(NetworkStream stream, UdpClient udp,
        int slot, byte[] sessionToken, CancellationToken token)
    {
        using var hello = ProtocolV4.CreateServerHello(slot);
        await stream.WriteAsync(hello.ToArray(), token);
        using Packet clientHello = await ReadFrameAsync(stream, token);
        var advertised = ProtocolV2.ReadClientHello(clientHello);
        Assert.Equal((ProtocolV4.Version, slot), (advertised.Version, advertised.SlotId));
        using var grant = ProtocolV2.CreateSessionGranted(sessionToken);
        await stream.WriteAsync(grant.ToArray(), token);
        return await ReadRegistrationAsync(udp, slot, sessionToken, token);
    }

    private static async Task<IPEndPoint> ReadRegistrationAsync(UdpClient udp, int slot,
        byte[] sessionToken, CancellationToken token)
    {
        while (true)
        {
            var datagram = await udp.ReceiveAsync(token);
            using Packet registration = ProtocolV2.OpenClientDatagram(datagram.Buffer, out int slotId);
            Assert.Equal(slot, slotId);
            if (ProtocolV2.ReadUdpRegistration(registration).AsSpan().SequenceEqual(sessionToken))
                return datagram.RemoteEndPoint;
        }
    }

    private static async Task ConfirmLoadClientAsync(NetworkStream stream, byte[] sessionToken,
        byte[] nonce, CancellationToken token)
    {
        using Packet confirm = await ReadFrameAsync(stream, token);
        var echo = ProtocolV2.ReadUdpConfirm(confirm);
        Assert.True(echo.Token.AsSpan().SequenceEqual(sessionToken));
        Assert.True(echo.Nonce.AsSpan().SequenceEqual(nonce));
        using var ready = ProtocolV2.CreateLobbyReady();
        await stream.WriteAsync(ready.ToArray(), token);
    }

    private static async Task SendServerPacketAsync(NetworkStream stream, ServerPackets type,
        int[] fields, CancellationToken token)
    {
        using var packet = new Packet((int)type);
        foreach (int field in fields) packet.Write(field);
        packet.WriteLength();
        packet.EncryptPayload();
        await stream.WriteAsync(packet.ToArray(), token);
    }

    private static Task SendSpawnAsync(NetworkStream stream, int room, int round, int player, float x, float y,
        CancellationToken token) => SendServerPacketAsync(stream, ServerPackets.spawnPlayer,
            [room, round, player, BitConverter.SingleToInt32Bits(x), BitConverter.SingleToInt32Bits(y)], token);

    private static byte[] Different(byte[] bytes)
    {
        byte[] copy = (byte[])bytes.Clone();
        copy[0] ^= 1;
        return copy;
    }

    private static async Task<Packet> ReadFrameAsync(NetworkStream stream, CancellationToken token)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, token);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        Assert.InRange(length, 64, 16_384);
        var frame = new byte[header.Length + length];
        header.CopyTo(frame, 0);
        await stream.ReadExactlyAsync(frame.AsMemory(header.Length), token);
        var packet = new Packet(frame);
        try
        {
            packet.DecryptPayload();
            packet.ReadInt();
            return packet;
        }
        catch
        {
            packet.Dispose();
            throw;
        }
    }
}
