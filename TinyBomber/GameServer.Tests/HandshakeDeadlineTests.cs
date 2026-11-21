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
    public void WelcomeOnlySetsUsernameBeforeDeadline(int offset, bool accepted)
    {
        var slot = new Client(1);
        slot.tcp.StartHandshake(AcceptedAt);

        ServerHandle.WelcomeReceived(slot, slot.id, "Player", Deadline + offset);

        Assert.Equal(accepted ? "Player" : null, slot.Username);
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    public void RegistrationOnlyBindsBeforeDeadline(int offset, bool accepted)
    {
        var slot = new Client(1) { Username = "Player" };
        slot.tcp.StartHandshake(AcceptedAt);
        var endpoint = new IPEndPoint(IPAddress.Loopback, 12345);

        Assert.Equal(accepted, ServerHandle.RegisterUdp(slot, slot.udp, endpoint, Deadline + offset));
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
        ServerHandle.WelcomeReceived(slot, slot.id, "Player", AcceptedAt);
        Assert.True(ServerHandle.RegisterUdp(slot, slot.udp, new IPEndPoint(IPAddress.Loopback, 12345), Deadline - 2));
        Assert.True(ServerHandle.RegisterUdp(slot, slot.udp, slot.udp.endPoint!, Deadline - 1));

        ServerHandle.ConfirmUdp(slot, slot.tcp.UdpChallenge, Deadline + offset);

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

            next.socket = nextSocket;
            next.StartHandshake(Deadline);
            Server.ExpireHandshake(slot.id, first, Deadline + 1);
            Server.Disconnect(slot.id, first);
            ServerHandle.WelcomeReceived(slot, slot.id, "Replacement", Deadline + 1);
            Assert.False(ServerHandle.RegisterUdp(slot, firstUdp,
                new IPEndPoint(IPAddress.Loopback, 12345), Deadline + 1));
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
        ServerHandle.WelcomeReceived(slot, slot.id, "Player", AcceptedAt);
        ServerHandle.RegisterUdp(slot, slot.udp, new IPEndPoint(IPAddress.Loopback, 12345), Deadline - 2);
        ServerHandle.ConfirmUdp(slot, connection.UdpChallenge, Deadline - 1);
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
        var (tcpPort, udpPort) = Server.Start(6, IPAddress.Loopback, 0, 0);
        try
        {
            using var ready = await Peer.OpenAsync(tcpPort, token);
            await ready.CompleteHandshakeAsync(udpPort, token);
            using var silent = await Peer.OpenAsync(tcpPort, token);
            using var partial = await Peer.OpenAsync(tcpPort, token);
            using var noRegistration = await Peer.OpenAsync(tcpPort, token);
            using var noConfirmation = await Peer.OpenAsync(tcpPort, token);
            using var retrying = await Peer.OpenAsync(tcpPort, token);

            byte[] partialFrame = ClientFrame(ClientPackets.welcomeReceived, packet =>
            {
                packet.Write(partial.SlotId);
                packet.Write("Partial");
            });
            await partial.Stream.WriteAsync(partialFrame.AsMemory(0, 10), token);
            await noRegistration.SendWelcomeReceivedAsync(token);
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
                    retrying.WaitForDeadlineAsync(token));
            }
            finally
            {
                stopRetries.Cancel();
                await retries;
            }

            // Admission reuses the first expired slot. The ready peer still occupies its original slot.
            using var replacement = await Peer.OpenAsync(tcpPort, token);
            Assert.Equal(silent.SlotId, replacement.SlotId);
            await replacement.CompleteHandshakeAsync(udpPort, token);
            await AssertRoomCanStartAsync(ready, replacement, token);

            ready.Dispose();
            replacement.Dispose();
            while (Server.LatestMetrics is not { ConnectedClients: 0, ReadyClients: 0, RoomCount: 0 })
                await Task.Delay(50, token);
        }
        finally
        {
            Server.Shutdown();
        }
    }

    private static async Task AssertRoomCanStartAsync(Peer ready, Peer replacement, CancellationToken token)
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
        Assert.Equal(1, started.ReadInt());
        Assert.Equal(2, started.ReadInt());
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
        private readonly UdpClient udp = new();
        private readonly long started = Stopwatch.GetTimestamp();
        internal NetworkStream Stream => tcp.GetStream();
        internal int SlotId { get; private set; }

        internal static async Task<Peer> OpenAsync(int port, CancellationToken token)
        {
            var peer = new Peer();
            try
            {
                await peer.tcp.ConnectAsync(IPAddress.Loopback, port, token);
                using Packet welcome = await peer.ReadAsync(ServerPackets.welcome, token);
                welcome.ReadString();
                peer.SlotId = welcome.ReadInt();
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

        internal Task SendAsync(ClientPackets type, CancellationToken token, Action<Packet>? write = null) =>
            Stream.WriteAsync(ClientFrame(type, write), token).AsTask();

        internal Task SendWelcomeReceivedAsync(CancellationToken token) =>
            SendAsync(ClientPackets.welcomeReceived, token, packet =>
            {
                packet.Write(SlotId);
                packet.Write($"Peer{SlotId}");
            });

        internal async Task<int> BeginUdpAsync(int port, CancellationToken token)
        {
            await SendWelcomeReceivedAsync(token);
            udp.Connect(IPAddress.Loopback, port);
            using var stopRetries = CancellationTokenSource.CreateLinkedTokenSource(token);
            Task retries = RepeatRegistrationAsync(stopRetries.Token);
            try
            {
                using var probe = new Packet((await udp.ReceiveAsync(token)).Buffer);
                probe.DecryptPayload();
                probe.ReadInt();
                Assert.Equal((int)ServerPackets.udpProbe, probe.ReadInt());
                return probe.ReadInt();
            }
            finally
            {
                stopRetries.Cancel();
                await retries;
            }
        }

        internal async Task RepeatRegistrationAsync(CancellationToken token)
        {
            byte[] registration = BitConverter.GetBytes(SlotId);
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await udp.SendAsync(registration, token);
                    await Task.Delay(100, token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }

        internal async Task CompleteHandshakeAsync(int udpPort, CancellationToken token)
        {
            int challenge = await BeginUdpAsync(udpPort, token);
            await SendAsync(ClientPackets.udpReady, token, packet => packet.Write(challenge));
            using Packet ready = await ReadAsync(ServerPackets.lobbyReady, token);
            Assert.Equal(0, ready.UnreadLength());
        }

        internal async Task<Packet> ReadAsync(ServerPackets expected, CancellationToken token, bool skipMatchState = false)
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
                    var type = (ServerPackets)packet.ReadInt();
                    if (!skipMatchState || type != ServerPackets.matchState)
                    {
                        Assert.Equal(expected, type);
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

        public void Dispose()
        {
            tcp.Dispose();
            udp.Dispose();
        }
    }
}
