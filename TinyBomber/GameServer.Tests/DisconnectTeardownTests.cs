using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using GameShared;
using Xunit.Abstractions;

namespace GameServer.Tests;

[Collection("Network metrics server state")]
public sealed class DisconnectTeardownTests(ITestOutputHelper output)
{
    [Fact]
    public async Task OnePeerDisconnectsAndRemainingPlayersContinueAfterSlotReuse()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        CancellationToken token = timeout.Token;
        using var fixture = new SocketFixture();
        var (room, peers) = await fixture.StartRoomAsync(4, token);
        var departed = peers[0];
        var survivors = peers[1..];
        var players = survivors.Select(peer => peer.Slot.player).ToArray();
        long errorsBefore = Server.Network.ReadTotals().TcpSendErrors;

        departed.Close();
        await DrainAsync(1, token);
        departed.AssertReleased();
        Assert.Equal(survivors.Select(peer => peer.Slot), room.Members);
        Assert.Equal(players, survivors.Select(peer => peer.Slot.player));
        Assert.All(survivors, peer => Assert.True(room.AcceptsInput(peer.Slot, room.Id, room.RoundId)));
        foreach (var peer in survivors)
        {
            await peer.ExpectAsync(ServerPackets.playerDisconnected, token, room.Id, room.RoundId, departed.Slot.id);
            await peer.ExpectMatchStateAsync(room, token);
        }

        // Queue delayed cleanup for the departed wrapper, then let a new socket occupy the slot.
        ThreadManager.EnqueueAction(() => Server.Disconnect(departed.Slot.id, departed.Connection));
        var replacement = await fixture.OpenAsync(departed.Slot, token);
        await DrainAsync(1, token);
        Assert.Same(replacement.Connection, replacement.Slot.tcp);
        Assert.Same(replacement.Udp, replacement.Slot.udp);
        Assert.True(replacement.Slot.IsReady);
        Assert.Null(replacement.Slot.Room);
        Assert.Null(replacement.Slot.player);

        var mover = survivors[0];
        await mover.PlaceBombAsync(room, token);
        await DrainAsync(1, token);
        var (col, row) = GameLogic.WorldToGrid(mover.Slot.player!.x, mover.Slot.player.y);
        foreach (var peer in survivors)
            await peer.ExpectAsync(ServerPackets.bombPlaced, token, room.Id, room.RoundId, col, row);
        Assert.Equal(errorsBefore, Server.Network.ReadTotals().TcpSendErrors);

        await replacement.SendAsync(ClientPackets.createRoom, token);
        await DrainAsync(1, token);
        var newRoom = Assert.IsType<GameLogic>(replacement.Slot.Room);
        Assert.NotEqual(room.Id, newRoom.Id);
        await replacement.ExpectAsync(ServerPackets.roomChanged, token, newRoom.Id);
        await replacement.ExpectMatchStateAsync(newRoom, token);
    }

    [Fact]
    public async Task SequentialDisconnectsPreserveResultAndRemoveRoomAfterLastPeer()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        CancellationToken token = timeout.Token;
        using var fixture = new SocketFixture();
        var (room, peers) = await fixture.StartRoomAsync(3, token);
        long errorsBefore = Server.Network.ReadTotals().TcpSendErrors;

        for (int i = 0; i < peers.Length; i++)
        {
            var departed = peers[i];
            departed.Close();
            await DrainAsync(1, token);
            departed.AssertReleased();
            var survivors = peers[(i + 1)..];
            Assert.Equal(survivors.Select(peer => peer.Slot), room.Members);
            foreach (var peer in survivors)
            {
                await peer.ExpectAsync(ServerPackets.playerDisconnected, token, room.Id, room.RoundId, departed.Slot.id);
                await peer.ExpectMatchStateAsync(room, token);
            }

            if (survivors.Length > 0)
            {
                Assert.Same(room, Server.FindRoom(room.Id));
                room.UpdateRound(Stopwatch.GetTimestamp());
                Assert.Equal(survivors.Length == 1 ? MatchPhase.Finished : MatchPhase.Playing, room.Phase);
                if (survivors.Length == 1) await survivors[0].ExpectMatchStateAsync(room, token);
            }
        }

        Assert.Equal(MatchPhase.Finished, room.Phase);
        Assert.Equal(peers[^1].Slot.id, room.WinnerId);
        Assert.Equal(1, room.RoundId);
        Assert.Null(Server.FindRoom(room.Id));
        Assert.Equal(errorsBefore, Server.Network.ReadTotals().TcpSendErrors);
    }

    [Fact]
    public async Task ConcurrentDisconnectsReleaseBothRoomsAndTheirSlots()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        CancellationToken token = timeout.Token;
        using var fixture = new SocketFixture();
        var (firstRoom, firstPeers) = await fixture.StartRoomAsync(4, token);
        var (secondRoom, secondPeers) = await fixture.StartRoomAsync(4, token);
        Peer[] peers = [.. firstPeers, .. secondPeers];
        long errorsBefore = Server.Network.ReadTotals().TcpSendErrors;

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task[] closes = peers.Select(async peer =>
        {
            await start.Task;
            peer.Close();
        }).ToArray();
        start.SetResult();
        await Task.WhenAll(closes).WaitAsync(token);
        // Apply the real EOF/error callbacks only after every client has closed its socket.
        await DrainAsync(peers.Length, token);

        Assert.All(peers, peer => peer.AssertReleased());
        Assert.Empty(firstRoom.Members);
        Assert.Empty(secondRoom.Members);
        Assert.Null(Server.FindRoom(firstRoom.Id));
        Assert.Null(Server.FindRoom(secondRoom.Id));

        var replacement = await fixture.OpenAsync(firstPeers[0].Slot, token);
        // Send failures from the closing batch can queue additional disconnects for the old TCP wrappers.
        ThreadManager.UpdateMain();
        Assert.Equal(0, ThreadManager.LastBatchMetrics.Failed);
        Assert.Same(replacement.Connection, replacement.Slot.tcp);
        await replacement.SendAsync(ClientPackets.createRoom, token);
        await DrainAsync(1, token);
        var newRoom = Assert.IsType<GameLogic>(replacement.Slot.Room);
        await replacement.ExpectAsync(ServerPackets.roomChanged, token, newRoom.Id);
        await replacement.ExpectMatchStateAsync(newRoom, token);
        output.WriteLine($"TCP send errors observed during the closing batch: {Server.Network.ReadTotals().TcpSendErrors - errorsBefore}");
    }

    private static async Task DrainAsync(int minimumActions, CancellationToken token)
    {
        // The queue count, not elapsed time, controls when the update thread may apply the batch.
        while (ThreadManager.GetWaitingActionCount() < minimumActions)
            await Task.Delay(5, token);
        ThreadManager.UpdateMain();
        Assert.Equal(0, ThreadManager.LastBatchMetrics.Failed);
    }

    private sealed class SocketFixture : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly List<Client> slots = [];
        private readonly List<Peer> peers = [];

        public SocketFixture()
        {
            ThreadManager.Initialize();
            ThreadManager.UpdateMain();
            listener.Start();
        }

        public async Task<(GameLogic Room, Peer[] Peers)> StartRoomAsync(int count, CancellationToken token)
        {
            var group = new Peer[count];
            for (int i = 0; i < count; i++)
            {
                var slot = new Client(100 + slots.Count);
                Server.clients.Add(slot.id, slot);
                slots.Add(slot);
                group[i] = await OpenAsync(slot, token);
            }

            var room = Server.CreateRoom();
            foreach (var peer in group) Assert.True(room.TryAddMember(peer.Slot));
            room.StartRound(group[0].Slot, room.Id, 0);
            Assert.Equal(MatchPhase.Playing, room.Phase);
            foreach (var peer in group)
            {
                await peer.ExpectAsync(ServerPackets.roundBegin, token,
                    [room.Id, 1, count, .. group.Select(member => member.Slot.id)]);
                using (var map = await peer.ReadAsync(token))
                    Assert.Equal((ServerPackets.mapData, room.Id, 1), ((ServerPackets)map.ReadInt(), map.ReadInt(), map.ReadInt()));
                foreach (var member in group)
                {
                    using var spawn = await peer.ReadAsync(token);
                    Assert.Equal((ServerPackets.spawnPlayer, room.Id, 1, member.Slot.id),
                        ((ServerPackets)spawn.ReadInt(), spawn.ReadInt(), spawn.ReadInt(), spawn.ReadInt()));
                }
                await peer.ExpectMatchStateAsync(room, token);
            }
            return (room, group);
        }

        public async Task<Peer> OpenAsync(Client slot, CancellationToken token)
        {
            var peer = new Peer(slot);
            peers.Add(peer);
            await peer.Tcp.ConnectAsync((IPEndPoint)listener.LocalEndpoint, token);
            peer.Accept(await listener.AcceptTcpClientAsync(token));
            using (var hello = await peer.ReadAsync(token))
                Assert.Equal((ProtocolV2.Version, slot.id), ProtocolV2.ReadServerHello(hello));

            // Drive the v2 state transitions in the fixture. The M3 integration test covers the UDP socket handshake.
            ServerHandle.ClientHello(slot, peer.Connection, ProtocolV2.Version, slot.id, $"Peer{slot.id}");
            using var grant = await peer.ReadAsync(token);
            byte[] session = ProtocolV2.ReadSessionGranted(grant);
            Assert.True(ServerHandle.RegisterUdp(slot, peer.Connection, peer.Udp,
                (IPEndPoint)peer.Tcp.Client.LocalEndPoint!, session));
            ServerHandle.ConfirmUdp(slot, peer.Connection, session, peer.Udp.ProbeNonce!);
            using var ready = await peer.ReadAsync(token);
            ProtocolV2.ReadLobbyReady(ready);
            Assert.True(slot.IsReady);
            return peer;
        }

        public void Dispose()
        {
            foreach (var slot in slots) Server.Disconnect(slot.id, slot.tcp);
            foreach (var peer in peers) peer.Close();
            ThreadManager.UpdateMain();
            foreach (var slot in slots) Server.clients.Remove(slot.id);
            listener.Stop();
        }
    }

    private sealed class Peer(Client slot)
    {
        public Client Slot { get; } = slot;
        public Client.TCP Connection { get; } = slot.tcp;
        public Client.UDP Udp { get; } = slot.udp;
        public TcpClient Tcp { get; } = new();
        private SafeSocketHandle serverHandle = null!;

        public void Accept(TcpClient accepted)
        {
            serverHandle = accepted.Client.SafeHandle;
            Connection.Connect(accepted);
        }

        public void Close() => Tcp.Dispose();

        public void AssertReleased()
        {
            Assert.True(serverHandle.IsClosed);
            Assert.NotSame(Connection, Slot.tcp);
            Assert.NotSame(Udp, Slot.udp);
            Assert.Null(Slot.tcp.socket);
            Assert.Null(Slot.tcp.SessionToken);
            Assert.Null(Slot.udp.endPoint);
            Assert.Null(Slot.Username);
            Assert.False(Slot.IsReady);
            Assert.Null(Slot.Room);
            Assert.Null(Slot.player);
        }

        public async Task SendAsync(ClientPackets type, CancellationToken token, params int[] fields)
        {
            using var packet = new Packet((int)type);
            foreach (var field in fields) packet.Write(field);
            packet.WriteLength();
            packet.EncryptPayload();
            await Tcp.GetStream().WriteAsync(packet.ToArray(), token);
        }

        public Task PlaceBombAsync(GameLogic room, CancellationToken token) =>
            SendAsync(ClientPackets.placeBomb, token, room.Id, room.RoundId);

        public Task ExpectMatchStateAsync(GameLogic room, CancellationToken token) =>
            ExpectAsync(ServerPackets.matchState, token, room.Id, room.RoundId,
                (int)room.Phase, room.Members.Count, room.WinnerId);

        public async Task ExpectAsync(ServerPackets type, CancellationToken token, params int[] fields)
        {
            using var packet = await ReadAsync(token);
            Assert.Equal(type, (ServerPackets)packet.ReadInt());
            foreach (var field in fields) Assert.Equal(field, packet.ReadInt());
            Assert.Equal(0, packet.UnreadLength());
        }

        public async Task<Packet> ReadAsync(CancellationToken token)
        {
            NetworkStream stream = Tcp.GetStream();
            byte[] header = new byte[sizeof(int)];
            await stream.ReadExactlyAsync(header, token);
            int length = BinaryPrimitives.ReadInt32LittleEndian(header);
            Assert.InRange(length, 64, 16_384);
            byte[] frame = new byte[header.Length + length];
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
}
