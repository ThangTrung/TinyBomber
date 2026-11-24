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

    [Fact]
    public async Task BombUpEventsReachEveryParticipantIncludingEliminatedPeer()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        using var fixture = new SocketFixture();
        var (room, peers) = await fixture.StartRoomAsync(3, token, new GameLogic(987, itemRandom: new BombUpTests.Roll(0)));
        var owner = peers[0].Slot.player!;
        var (col, row) = GameLogic.WorldToGrid(owner.x, owner.y);
        room.mapData[col + 1, row] = 2;
        room.PlaceBomb(peers[0].Slot, room.Id, 1);
        room.UpdateRound(long.MaxValue);
        Assert.False(owner.isAlive);
        Assert.Contains((col + 1, row), room.BombUps);
        var winner = peers[1].Slot.player!;
        (winner.x, winner.y) = GameLogic.GridToWorld(col + 1, row);
        room.UpdateRound(long.MaxValue);
        Assert.Empty(room.BombUps);
        Assert.Equal(2, winner.maxActiveBombs);
        foreach (var peer in peers)
        {
            await peer.ExpectAsync(ServerPackets.bombPlaced, token, room.Id, 1, col, row);
            using var explosion = await peer.ReadAsync(token);
            Assert.Equal((int)ServerPackets.bombExploded, explosion.ReadInt());
            await peer.ExpectAsync(ServerPackets.playerEliminated, token, room.Id, 1, owner.id);
            await peer.ExpectAsync(ServerPackets.bombUpSpawned, token, room.Id, 1, col + 1, row);
            await peer.ExpectAsync(ServerPackets.bombUpCollected, token, room.Id, 1, col + 1, row, winner.id, 2);
        }
    }

    [Fact]
    public async Task BothPeersRemoveItemsCollectedAtMaximumCapacity()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        using var fixture = new SocketFixture();
        var (room, peers) = await fixture.StartRoomAsync(2, token);
        var replicas = peers.Select(_ => new BombUpRoundState()).ToArray();
        foreach (var replica in replicas)
            foreach (var peer in peers)
                replica.SpawnPlayer(peer.Slot.id);

        var winner = peers[0].Slot.player!;
        var other = peers[1].Slot.player!;
        const int row = 5;
        int expectedCapacity = ProtocolV5.InitialCapacity;
        for (int col = 5; col <= 8; col++)
        {
            (winner.x, winner.y) = (other.x, other.y) = GameLogic.GridToWorld(col, row);
            room.mapData[col, row] = 0;
            Assert.True(room.BombUps.Add((col, row)));
            ServerSend.BombUp(room, col, row);
            room.UpdateRound(0);

            expectedCapacity = Math.Min(expectedCapacity + 1, ProtocolV5.MaximumCapacity);
            Assert.Empty(room.BombUps);
            Assert.Equal(expectedCapacity, winner.maxActiveBombs);
            Assert.Equal(1, other.maxActiveBombs);
            for (int i = 0; i < peers.Length; i++)
            {
                using var spawn = await peers[i].ReadAsync(token);
                var spawnType = (ServerPackets)spawn.ReadInt();
                Assert.Equal(ServerPackets.bombUpSpawned, spawnType);
                var spawned = ProtocolV5.ReadBombUp(spawn, spawnType);
                Assert.Equal((room.Id, room.RoundId, col, row, 0, 0), spawned);
                Assert.True(replicas[i].Spawn(spawned.Col, spawned.Row));

                using var collect = await peers[i].ReadAsync(token);
                var collectType = (ServerPackets)collect.ReadInt();
                Assert.Equal(ServerPackets.bombUpCollected, collectType);
                var collected = ProtocolV5.ReadBombUp(collect, collectType);
                Assert.Equal((room.Id, room.RoundId, col, row, winner.id, expectedCapacity), collected);
                Assert.True(replicas[i].Collect(collected.Col, collected.Row, collected.Actor, collected.Capacity));
                Assert.False(replicas[i].HasItem(col, row));
                Assert.True(replicas[i].TryGetCapacity(winner.id, out int observed));
                Assert.Equal(expectedCapacity, observed);
            }
        }
    }

    [Fact]
    public async Task TwentyRoundsReuseTheSameRoomAndReadySessions()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = timeout.Token;
        int connectedBefore = Server.clients.Values.Count(slot => slot.tcp.socket != null);
        int readyBefore = Server.clients.Values.Count(slot => slot.IsReady);
        using var fixture = new SocketFixture();
        var (room, peers) = await fixture.StartRoomAsync(2, token);
        var first = peers[0].Slot;
        var second = peers[1].Slot;
        var sessions = peers.Select(peer => peer.Connection.SessionToken).ToArray();
        Player[]? previousPlayers = null;
        long fuse = 2 * Stopwatch.Frequency;
        const int roundCount = 20;

        for (int round = 1; round <= roundCount; round++)
        {
            Assert.Equal((round, MatchPhase.Playing, 0), (room.RoundId, room.Phase, room.WinnerId));
            Assert.Same(room, Server.FindRoom(room.Id));
            Assert.Equal(new[] { first, second }, room.Members);
            Assert.Equal(connectedBefore + 2, Server.clients.Values.Count(slot => slot.tcp.socket != null));
            Assert.Equal(readyBefore + 2, Server.clients.Values.Count(slot => slot.IsReady));
            Assert.Empty(room.BombUps);

            var players = peers.Select(peer => Assert.IsType<Player>(peer.Slot.player)).ToArray();
            for (int i = 0; i < players.Length; i++)
            {
                var player = players[i];
                Assert.Same(peers[i].Connection, peers[i].Slot.tcp);
                Assert.Same(peers[i].Udp, peers[i].Slot.udp);
                Assert.Same(sessions[i], peers[i].Slot.tcp.SessionToken);
                if (previousPlayers != null) Assert.NotSame(previousPlayers[i], player);
                Assert.True(player.isAlive);
                Assert.Equal((1, 1, 0L, 0L, 0), (player.hp, player.maxActiveBombs,
                    player.LastProcessedMoveSeq, player.LastAcceptedMoveSeq, room.ActiveBombCount(player)));
                Assert.Equal((i == 0 ? 1 : 15, 13), GameLogic.WorldToGrid(player.x, player.y));
                Assert.False(player.TryTakeFinalPositionRefresh(fuse, TimeSpan.Zero));

                // Before any refill, a new player can spend the opening credit only once.
                Assert.True(player.TrySpendMovementCredit(player.x + 0.075f, player.y, 0));
                Assert.False(player.TrySpendMovementCredit(player.x + 0.01f, player.y, 0));
            }
            for (int col = 0; col < Constants.MAP_WIDTH; col++)
            for (int row = 0; row < Constants.MAP_HEIGHT; row++)
                Assert.InRange(room.mapData[col, row], 0, 2);
            Assert.Equal(1, room.mapData[0, 0]);
            foreach (var (col, row) in new[] { (1, 1), (2, 1), (1, 2), (15, 1), (14, 1), (15, 2),
                         (1, 13), (2, 13), (1, 12), (15, 13), (14, 13), (15, 12) })
                Assert.Equal(0, room.mapData[col, row]);

            if (round > 1)
            {
                await peers[0].SendAsync(ClientPackets.placeBomb, token, room.Id, round - 1);
                await DrainAsync(1, token);
                ServerHandle.PlayerMovement(first, room.Id, round - 1, players[0].x, players[0].y, 0, 99);
                Assert.Equal(0, room.ActiveBombCount(players[0]));
                Assert.Equal(0, players[0].LastProcessedMoveSeq);
            }

            // Seed item cells and control fuse timestamps while applying the production pickup and finish logic.
            var firstPlayer = players[0];
            var firstSpawn = (firstPlayer.x, firstPlayer.y);
            foreach (int col in new[] { 3, 4 })
            {
                room.mapData[col, 13] = 0;
                (firstPlayer.x, firstPlayer.y) = GameLogic.GridToWorld(col, 13);
                room.BombUps.Add((col, 13));
                ServerSend.BombUp(room, col, 13);
                room.UpdateRound(0);
                foreach (var peer in peers)
                {
                    await peer.ExpectAsync(ServerPackets.bombUpSpawned, token, room.Id, round, col, 13);
                    await peer.ExpectAsync(ServerPackets.bombUpCollected, token, room.Id, round,
                        col, 13, first.id, col - 1);
                }
            }
            Assert.Equal(3, firstPlayer.maxActiveBombs);
            (firstPlayer.x, firstPlayer.y) = firstSpawn;
            foreach (var player in players)
            {
                Assert.True(player.TryMarkMovementProcessed(1));
                player.AcceptMovement(player.x, player.y, 1, 0);
            }
            room.mapData[2, 13] = 2;
            room.BombUps.Add((5, 13));
            ServerSend.BombUp(room, 5, 13);
            bool draw = round % 2 == 0;
            room.PlaceBomb(first, room.Id, round, placedAt: 0);
            if (draw) room.PlaceBomb(second, room.Id, round, placedAt: 0);
            (firstPlayer.x, firstPlayer.y) = GameLogic.GridToWorld(4, 13);
            room.PlaceBomb(first, room.Id, round, placedAt: 1);
            (firstPlayer.x, firstPlayer.y) = firstSpawn;
            Assert.Equal(2, room.ActiveBombCount(firstPlayer));

            room.UpdateRound(fuse);
            Assert.Equal(MatchPhase.Finished, room.Phase);
            Assert.Equal(draw ? 0 : second.id, room.WinnerId);
            Assert.Equal(0, room.mapData[2, 13]);
            Assert.Empty(room.BombUps);
            Assert.False(firstPlayer.isAlive);
            Assert.Equal(!draw, players[1].isAlive);
            Assert.All(players, player => Assert.Equal(0, room.ActiveBombCount(player)));
            room.UpdateRound(fuse + 1); // The cancelled fuse and pending refresh must emit nothing.
            foreach (var peer in peers)
            {
                await peer.ExpectAsync(ServerPackets.bombUpSpawned, token, room.Id, round, 5, 13);
                await peer.ExpectAsync(ServerPackets.bombPlaced, token, room.Id, round, 1, 13);
                if (draw) await peer.ExpectAsync(ServerPackets.bombPlaced, token, room.Id, round, 15, 13);
                await peer.ExpectAsync(ServerPackets.bombPlaced, token, room.Id, round, 4, 13);
                await peer.ExpectExplosionAsync(room, 1, 13, token);
                if (draw) await peer.ExpectExplosionAsync(room, 15, 13, token);
                await peer.ExpectAsync(ServerPackets.playerEliminated, token, room.Id, round, first.id);
                if (draw) await peer.ExpectAsync(ServerPackets.playerEliminated, token, room.Id, round, second.id);
                await peer.ExpectMatchStateAsync(room, token);
            }

            previousPlayers = players;
            if (round == roundCount) break;
            await peers[0].SendAsync(ClientPackets.requestStart, token, room.Id, round);
            await DrainAsync(1, token);
            await SocketFixture.ExpectRoundAsync(room, peers, token);
        }

        await peers[0].SendAsync(ClientPackets.leaveRoom, token, room.Id);
        await DrainAsync(1, token);
        await peers[0].ExpectAsync(ServerPackets.roomChanged, token, 0);
        await peers[1].ExpectAsync(ServerPackets.playerDisconnected, token, room.Id, roundCount, first.id);
        await peers[1].ExpectMatchStateAsync(room, token);
        Assert.Null(first.Room);
        Assert.Null(first.player);
        foreach (var peer in peers)
        {
            peer.Close();
            await DrainAsync(1, token);
            peer.AssertReleased();
        }
        Assert.Empty(room.Members);
        Assert.Null(Server.FindRoom(room.Id));
        Assert.Equal(connectedBefore, Server.clients.Values.Count(slot => slot.tcp.socket != null));
        Assert.Equal(readyBefore, Server.clients.Values.Count(slot => slot.IsReady));
        output.WriteLine($"Verified {roundCount} rounds on two sessions: 10 wins and 10 draws. Queue after teardown: {ThreadManager.GetWaitingActionCount()}.");
    }

    [Fact]
    public async Task SwitchingRoomsAndReusingSlotCannotCarryOldRoundState()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        using var fixture = new SocketFixture();
        var (roomA, a) = await fixture.StartRoomAsync(2, token);
        var (roomB, b) = await fixture.StartRoomAsync(2, token);
        Assert.Equal(roomA.RoundId, roomB.RoundId);
        foreach (var (room, owner) in new[] { (roomA, a[0]), (roomB, b[0]) })
        {
            room.mapData[5, 5] = 0;
            room.BombUps.Add((5, 5));
            var player = owner.Slot.player!;
            (player.x, player.y) = GameLogic.GridToWorld(5, 5);
            room.PlaceBomb(owner.Slot, room.Id, 1, placedAt: 0);
        }
        foreach (var peer in a) await peer.ExpectAsync(ServerPackets.bombPlaced, token, roomA.Id, 1, 5, 5);
        foreach (var peer in b) await peer.ExpectAsync(ServerPackets.bombPlaced, token, roomB.Id, 1, 5, 5);

        var mover = a[0];
        var ownerB = b[0].Slot.player!;
        roomA.mapData[6, 5] = 0;
        roomB.mapData[6, 5] = 2;
        await mover.SendAsync(ClientPackets.leaveRoom, token, roomA.Id);
        await DrainAsync(1, token);
        await mover.ExpectAsync(ServerPackets.roomChanged, token, 0);
        await a[1].ExpectAsync(ServerPackets.playerDisconnected, token, roomA.Id, 1, mover.Slot.id);
        await a[1].ExpectMatchStateAsync(roomA, token);
        await mover.SendAsync(ClientPackets.joinRoom, token, roomB.Id);
        await DrainAsync(1, token);
        await mover.ExpectAsync(ServerPackets.roomChanged, token, roomB.Id);
        foreach (var peer in b.Append(mover)) await peer.ExpectMatchStateAsync(roomB, token);
        Assert.Same(roomB, mover.Slot.Room);
        Assert.Null(mover.Slot.player);
        Assert.False(roomB.AcceptsInput(mover.Slot, roomB.Id, 1));

        var departing = a[1];
        var oldToken = departing.Connection.SessionToken;
        await departing.SendAsync(ClientPackets.createRoom, token);
        await WaitForActionsAsync(1, token);
        Server.Disconnect(departing.Slot.id, departing.Connection);
        Assert.Null(Server.FindRoom(roomA.Id));
        var replacement = await fixture.OpenAsync(departing.Slot, token);
        Assert.False(oldToken!.SequenceEqual(replacement.Connection.SessionToken!));
        await DrainAsync(1, token);
        Assert.Same(replacement.Connection, replacement.Slot.tcp);
        Assert.Null(replacement.Slot.Room);
        Assert.Null(replacement.Slot.player);

        Assert.Same(roomB, Server.FindRoom(roomB.Id));
        Assert.Same(ownerB, b[0].Slot.player);
        Assert.Equal(1, roomB.ActiveBombCount(ownerB));
        Assert.Contains((5, 5), roomB.BombUps);
        Assert.Equal(2, roomB.mapData[6, 5]);
        roomB.UpdateRound(2 * Stopwatch.Frequency);
        foreach (var peer in b)
        {
            await peer.ExpectExplosionAsync(roomB, 5, 5, token);
            await peer.ExpectAsync(ServerPackets.playerEliminated, token, roomB.Id, 1, ownerB.id);
        }
        foreach (var peer in b.Append(mover)) await peer.ExpectMatchStateAsync(roomB, token);
        Assert.Equal(0, roomB.mapData[6, 5]);

        await b[1].SendAsync(ClientPackets.requestStart, token, roomB.Id, 1);
        await DrainAsync(1, token);
        await SocketFixture.ExpectRoundAsync(roomB, [mover, .. b], token);
        Assert.Equal(2, roomB.RoundId);
        Assert.Equal(1, mover.Slot.player!.maxActiveBombs);
        Assert.Empty(roomB.BombUps);

        // A solo Ready client may disconnect in Waiting without leaving a room or player behind.
        await replacement.SendAsync(ClientPackets.createRoom, token);
        await DrainAsync(1, token);
        var waitingRoom = Assert.IsType<GameLogic>(replacement.Slot.Room);
        Assert.Equal(MatchPhase.Waiting, waitingRoom.Phase);
        replacement.Close();
        await DrainAsync(1, token);
        replacement.AssertReleased();
        Assert.Null(Server.FindRoom(waitingRoom.Id));
    }

    private static async Task WaitForActionsAsync(int minimumActions, CancellationToken token)
    {
        while (ThreadManager.GetWaitingActionCount() < minimumActions)
            await Task.Delay(5, token);
    }

    private static async Task DrainAsync(int minimumActions, CancellationToken token)
    {
        // The queue count, not elapsed time, controls when the update thread may apply the batch.
        await WaitForActionsAsync(minimumActions, token);
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

        public async Task<(GameLogic Room, Peer[] Peers)> StartRoomAsync(int count, CancellationToken token, GameLogic? controlledRoom = null)
        {
            var group = new Peer[count];
            for (int i = 0; i < count; i++)
            {
                var slot = new Client(100 + slots.Count);
                Server.clients.Add(slot.id, slot);
                slots.Add(slot);
                group[i] = await OpenAsync(slot, token);
            }

            var room = controlledRoom ?? Server.CreateRoom();
            foreach (var peer in group) Assert.True(room.TryAddMember(peer.Slot));
            room.StartRound(group[0].Slot, room.Id, 0);
            await ExpectRoundAsync(room, group, token);
            return (room, group);
        }

        public static async Task ExpectRoundAsync(GameLogic room, Peer[] group, CancellationToken token)
        {
            Assert.Equal(MatchPhase.Playing, room.Phase);
            foreach (var peer in group)
            {
                await peer.ExpectAsync(ServerPackets.roundBegin, token,
                    [room.Id, room.RoundId, group.Length, .. group.Select(member => member.Slot.id)]);
                using (var map = await peer.ReadAsync(token))
                {
                    Assert.Equal((ServerPackets.mapData, room.Id, room.RoundId),
                        ((ServerPackets)map.ReadInt(), map.ReadInt(), map.ReadInt()));
                    for (int col = 0; col < Constants.MAP_WIDTH; col++)
                    for (int row = 0; row < Constants.MAP_HEIGHT; row++)
                        Assert.Equal(room.mapData[col, row], map.ReadInt());
                    Assert.Equal(0, map.UnreadLength());
                }
                foreach (var member in group)
                {
                    using var spawn = await peer.ReadAsync(token);
                    var player = member.Slot.player!;
                    Assert.Equal((ServerPackets.spawnPlayer, room.Id, room.RoundId, member.Slot.id, player.x, player.y),
                        ((ServerPackets)spawn.ReadInt(), spawn.ReadInt(), spawn.ReadInt(), spawn.ReadInt(),
                            spawn.ReadFloat(), spawn.ReadFloat()));
                    Assert.Equal(0, spawn.UnreadLength());
                }
                await peer.ExpectMatchStateAsync(room, token);
            }
        }

        public async Task<Peer> OpenAsync(Client slot, CancellationToken token)
        {
            var peer = new Peer(slot);
            peers.Add(peer);
            await peer.Tcp.ConnectAsync((IPEndPoint)listener.LocalEndpoint, token);
            peer.Accept(await listener.AcceptTcpClientAsync(token));
            using (var hello = await peer.ReadAsync(token))
                Assert.Equal((ProtocolV5.Version, slot.id), ProtocolV2.ReadServerHello(hello));

            // Drive the v2 state transitions in the fixture. The M3 integration test covers the UDP socket handshake.
            ServerHandle.ClientHello(slot, peer.Connection, ProtocolV5.Version, slot.id, $"Peer{slot.id}");
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
            Connection.Attach(accepted);
            Connection.StartReceiving();
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

        public async Task ExpectExplosionAsync(GameLogic room, int col, int row, CancellationToken token)
        {
            using var packet = await ReadAsync(token);
            Assert.Equal((ServerPackets.bombExploded, room.Id, room.RoundId, col, row),
                ((ServerPackets)packet.ReadInt(), packet.ReadInt(), packet.ReadInt(), packet.ReadInt(), packet.ReadInt()));
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
