using System.Buffers.Binary;
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
    [InlineData(2, 43)]
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
        ServerHandle.ClientHello(slot, slot.tcp, ProtocolV2.Version, slot.id, "Player", 100);
        byte[] token = Assert.IsType<byte[]>(slot.tcp.SessionToken);
        ServerHandle.ClientHello(slot, slot.tcp, ProtocolV2.Version, slot.id, "Repeated", 101);
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
        ServerHandle.ClientHello(slot, slot.tcp, ProtocolV2.Version, slot.id, "Player", 100);
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
            ServerHandle.ClientHello(slot, next, ProtocolV2.Version, slot.id, "Replacement", 100);
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
    public async Task BroadcastFallbackKeepsLegacyTcpPositionLayout()
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
            recipient.tcp.Connect(accepted);
            try
            {
                using Packet hello = await ReadFrameAsync(peer.GetStream(), token);
                Assert.Equal(recipient.id, ProtocolV2.ReadServerHello(hello).SlotId);

                ServerSend.BroadcastPlayerPosition(room, player);

                using Packet position = await ReadFrameAsync(peer.GetStream(), token);
                Assert.Equal((int)ServerPackets.playerPosition, position.ReadInt());
                Assert.Equal((room.Id, room.RoundId, mover.id, player.x, player.y),
                    (position.ReadInt(), position.ReadInt(), position.ReadInt(), position.ReadFloat(), position.ReadFloat()));
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
        // Load the executable produced by a solution build without changing the test project reference graph.
        var testOutput = new DirectoryInfo(AppContext.BaseDirectory);
        string clientDll = Path.GetFullPath(Path.Combine(testOutput.FullName, "..", "..", "..", "..",
            "GameServer.LoadClient", "bin", testOutput.Parent!.Name, testOutput.Name, "GameServer.LoadClient.dll"));
        Assert.True(File.Exists(clientDll), "Build TinyBomber.sln before running the LoadClient integration test.");
        Assembly clientAssembly = Assembly.LoadFrom(clientDll);

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
            using var stalePosition = ProtocolV2.CreateUdpPlayerPosition(firstToken, room, round, actor, oldX, y);

            await using (IAsyncDisposable first = NewLoadClient(clientAssembly, token))
            {
                var connect = (Task)Invoke(first, "ConnectAsync", address)!;
                using TcpClient tcp = await listener.AcceptTcpClientAsync(token);
                IPEndPoint endpoint = await GrantLoadClientAsync(tcp.GetStream(), udp, slot, firstToken, token);
                await udp.SendAsync(staleProbe.ToArray(), endpoint, token);
                await ConfirmLoadClientAsync(tcp.GetStream(), firstToken, firstNonce, token);
                await connect.WaitAsync(token);
                Assert.True(ReadReady(first));
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

            await SendServerPacketAsync(stream, ServerPackets.roomChanged, new[] { room }, token);
            await SendServerPacketAsync(stream, ServerPackets.roundBegin, new[] { room, round, 2, slot, actor }, token);
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
            using var currentPosition = ProtocolV2.CreateUdpPlayerPosition(nextToken, room, round, actor, newX, y);
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

    private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static IAsyncDisposable NewLoadClient(Assembly assembly, CancellationToken token)
    {
        Type errorType = assembly.GetType("GameServer.LoadClient.RunErrors", throwOnError: true)!;
        object errors = Activator.CreateInstance(errorType, nonPublic: true)!;
        Type clientType = assembly.GetType("GameServer.LoadClient.SimulatedClient", throwOnError: true)!;
        return (IAsyncDisposable)Activator.CreateInstance(clientType, InstanceMembers, null,
            new[] { (object)1, errors, token }, null)!;
    }

    private static object? Invoke(object instance, string method, params object[] args) =>
        instance.GetType().GetMethod(method, InstanceMembers)!.Invoke(instance, args);

    private static object ReadProperty(object instance, string name) =>
        instance.GetType().GetProperty(name, InstanceMembers)!.GetValue(instance)!;

    private static int ReadObserved(object trace) => (int)ReadProperty(Invoke(trace, "Read")!, "Observed");

    private static bool ReadReady(IAsyncDisposable client) => (bool)ReadProperty(Invoke(client, "ReadState")!, "Ready");

    private static async Task<IPEndPoint> GrantLoadClientAsync(NetworkStream stream, UdpClient udp,
        int slot, byte[] sessionToken, CancellationToken token)
    {
        using var hello = ProtocolV2.CreateServerHello(slot);
        await stream.WriteAsync(hello.ToArray(), token);
        using Packet clientHello = await ReadFrameAsync(stream, token);
        var advertised = ProtocolV2.ReadClientHello(clientHello);
        Assert.Equal((ProtocolV2.Version, slot), (advertised.Version, advertised.SlotId));
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
