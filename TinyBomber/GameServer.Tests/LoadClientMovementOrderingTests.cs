using System.Reflection;
using GameShared;

namespace GameServer.Tests;

[Collection("Network metrics server state")]
public sealed class LoadClientMovementOrderingTests
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Static |
        BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Assembly LoadClient = LoadAssembly();
    private static readonly Type ClientType = LoadClient.GetType("GameServer.LoadClient.SimulatedClient", true)!;
    private static readonly Type PositionType = LoadClient.GetType("GameServer.LoadClient.Position", true)!;
    private readonly byte[] token = ProtocolV2.NewSessionToken();

    [Fact]
    public async Task TcpAndUdpEncodingUseTheSamePositiveSequence()
    {
        await using var client = ReadyClient();
        object round = Call(client, "ReadState")!;
        object target = Position(0.08f);
        using var tcp = (Packet)CallStatic("CreateMovementPacket", round, token, target, 11L, false)!;
        using var tcpPacket = new Packet(tcp.ToArray());
        tcpPacket.DecryptPayload();
        tcpPacket.ReadInt();
        Assert.Equal((7, 1, 11L, 0.08f, 0f), ProtocolV4.ReadTcpPlayerMovement(tcpPacket));
        using var udp = (Packet)CallStatic("CreateMovementPacket", round, token, target, 11L, true)!;
        using var udpPacket = ProtocolV2.OpenClientDatagram(udp.ToArray(), out int slot);
        var ordered = ProtocolV4.ReadUdpPlayerMovement(udpPacket);
        Assert.Equal(1, slot);
        Assert.Equal(token, ordered.Token);
        Assert.Equal((7, 1, 11L, 0.08f, 0f), (ordered.RoomId, ordered.RoundId, ordered.MoveSeq, ordered.X, ordered.Y));
        foreach (bool viaUdp in new[] { true, false })
        {
            var error = Assert.Throws<TargetInvocationException>(() =>
                CallStatic("CreateMovementPacket", round, token, target, 0L, viaUdp));
            Assert.IsType<ArgumentOutOfRangeException>(error.InnerException);
        }
    }

    [Theory]
    [InlineData(11L, 12L, 2)]
    [InlineData(12L, 11L, 1)]
    [InlineData(11L, 11L, 1)]
    public async Task PeerPositionsShareWatermarkAcrossTcpAndUdp(long first, long second, int expectedObserved)
    {
        await using var client = ReadyClient();
        object trace = ClientType.GetProperty("Trace", Members)!.GetValue(client)!;
        Call(client, "Watch", 2, trace);
        PrepareObservation(trace, 0.08f);
        PrepareObservation(trace, 0.16f);
        using var tcp = ProtocolV4.CreateTcpPlayerPosition(7, 1, 2, first, 0.08f, 0f);
        using var udp = ProtocolV4.CreateUdpPlayerPosition(token, 7, 1, 2, second, 0.16f, 0f);
        Call(client, "HandleTcpPacket", tcp.ToArray());
        Call(client, "HandleUdpPacket", udp.ToArray());
        var watermarks = Read<Dictionary<int, long>>(client, "peerMovementSequences");
        Assert.Equal(Math.Max(first, second), watermarks[2]);
        object counts = Call(trace, "Read")!;
        Assert.Equal(expectedObserved, counts.GetType().GetProperty("Observed")!.GetValue(counts));
    }

    [Fact]
    public async Task OwnerCorrectionStillMarksWorkloadCorrectedAfterANewerSend()
    {
        await using var client = ReadyClient();
        Set(client, "lastSentMoveSeq", 14L);
        using var correction = ProtocolV4.CreateTcpPlayerCorrection(7, 1, 1, 12, 11, 0.50f, 0f);
        Call(client, "HandleTcpPacket", correction.ToArray());
        Assert.True(Read<bool>(client, "corrected"));
        Assert.Equal(12L, Read<long>(client, "lastOwnerProcessedMoveSeq"));
        Set(client, "corrected", false);
        Call(client, "HandleTcpPacket", correction.ToArray());
        Assert.False(Read<bool>(client, "corrected"));
        Assert.Equal(14L, Read<long>(client, "lastSentMoveSeq"));
    }

    [Fact]
    public async Task OwnerAckSharesCorrectionWatermarkAndIsNeverCountedAsPeerObservation()
    {
        await using var client = ReadyClient();
        object trace = ClientType.GetProperty("Trace", Members)!.GetValue(client)!;
        Call(client, "Watch", 2, trace);
        PrepareObservation(trace, 0.24f);
        using var otherRound = ProtocolV4.CreateTcpPlayerPosition(7, 2, 1, 99, 0.24f, 0f);
        Call(client, "HandleTcpPacket", otherRound.ToArray());
        Assert.Equal(0L, Read<long>(client, "lastOwnerProcessedMoveSeq"));

        using var ack = ProtocolV4.CreateTcpPlayerPosition(7, 1, 1, 13, 0.24f, 0f);
        Call(client, "HandleTcpPacket", ack.ToArray());
        Call(client, "HandleTcpPacket", ack.ToArray());
        using var oldCorrection = ProtocolV4.CreateTcpPlayerCorrection(7, 1, 1, 12, 11, 0.08f, 0f);
        Call(client, "HandleTcpPacket", oldCorrection.ToArray());
        Assert.Equal(13L, Read<long>(client, "lastOwnerProcessedMoveSeq"));
        Assert.False(Read<bool>(client, "corrected"));
        object counts = Call(trace, "Read")!;
        Assert.Equal(0, counts.GetType().GetProperty("Observed")!.GetValue(counts));

        using var peerPosition = ProtocolV4.CreateTcpPlayerPosition(7, 1, 2, 1, 0.24f, 0f);
        Call(client, "HandleTcpPacket", peerPosition.ToArray());
        counts = Call(trace, "Read")!;
        Assert.Equal(1, counts.GetType().GetProperty("Observed")!.GetValue(counts));
    }

    [Fact]
    public async Task FinalTcpRefreshCountsOnlyWhenThePeerHadNotAlreadyObservedThePosition()
    {
        await using var client = ReadyClient();
        object trace = ClientType.GetProperty("Trace", Members)!.GetValue(client)!;
        Call(client, "Watch", 2, trace);
        PrepareObservation(trace, 0.08f);
        PrepareObservation(trace, 0.16f);
        using var udp = ProtocolV4.CreateUdpPlayerPosition(token, 7, 1, 2, 1, 0.08f, 0f);
        using var duplicate = ProtocolV4.CreateTcpPlayerPosition(7, 1, 2, 1, 0.08f, 0f);
        using var missing = ProtocolV4.CreateTcpPlayerPosition(7, 1, 2, 2, 0.16f, 0f);
        Call(client, "HandleUdpPacket", udp.ToArray());
        Call(client, "HandleTcpPacket", duplicate.ToArray());
        object counts = Call(trace, "Read")!;
        Assert.Equal(1, counts.GetType().GetProperty("Observed")!.GetValue(counts));
        Call(client, "HandleTcpPacket", missing.ToArray());
        counts = Call(trace, "Read")!;
        Assert.Equal(2, counts.GetType().GetProperty("Observed")!.GetValue(counts));
    }

    [Fact]
    public async Task NewRoundAndRoomResetWatermarksAndDropOldScope()
    {
        await using var client = ReadyClient();
        var watermarks = Read<Dictionary<int, long>>(client, "peerMovementSequences");
        using var initial = ProtocolV4.CreateUdpPlayerPosition(token, 7, 1, 2, 12, 0.08f, 0f);
        Call(client, "HandleUdpPacket", initial.ToArray());
        Set(client, "lastSentMoveSeq", 15L);
        Set(client, "lastOwnerProcessedMoveSeq", 12L);
        Assert.Equal(12L, watermarks[2]);

        ReceiveTcp(client, ServerPackets.roundBegin, 7, 2, 2, 1, 2);
        ReceiveTcp(client, ServerPackets.matchState, 7, 2, (int)MatchPhase.Playing, 2, 0);
        using var beforeSpawn = ProtocolV4.CreateUdpPlayerPosition(token, 7, 2, 2, 13, 0.16f, 0f);
        Call(client, "HandleUdpPacket", beforeSpawn.ToArray());
        Assert.Empty(watermarks);
        ReceiveTcp(client, ServerPackets.spawnPlayer, 7, 2, 2, 0, 0);
        Call(client, "HandleUdpPacket", initial.ToArray());
        byte[] previousToken = (byte[])token.Clone();
        previousToken[0] ^= 1;
        using var staleSession = ProtocolV4.CreateUdpPlayerPosition(previousToken, 7, 2, 2, 13, 0.16f, 0f);
        Call(client, "HandleUdpPacket", staleSession.ToArray());
        Assert.Empty(watermarks);
        using var freshRound = ProtocolV4.CreateUdpPlayerPosition(token, 7, 2, 2, 1, 0.08f, 0f);
        Call(client, "HandleUdpPacket", freshRound.ToArray());
        Assert.Equal(1L, watermarks[2]);
        Assert.Equal(0L, Read<long>(client, "lastSentMoveSeq"));
        Assert.Equal(0L, Read<long>(client, "lastOwnerProcessedMoveSeq"));
        ReceiveTcp(client, ServerPackets.playerEliminated, 7, 2, 2);
        Call(client, "HandleUdpPacket", freshRound.ToArray());
        Assert.Empty(watermarks);

        ReceiveTcp(client, ServerPackets.roomChanged, 8);
        Assert.Empty(watermarks);
        Assert.Equal(0, Read<int>(client, "roundId"));
    }

    [Fact]
    public async Task ItemReplicationIsScopedAndSurvivesLocalElimination()
    {
        await using var client = ReadyClient();
        Set(client, "map", new int[17, 15]);
        var state = Read<BombUpRoundState>(client, "bombUps");
        ReceiveTcp(client, ServerPackets.playerEliminated, 7, 1, 1);
        ReceiveTcp(client, ServerPackets.bombUpSpawned, 8, 1, 4, 5);
        ReceiveTcp(client, ServerPackets.bombUpSpawned, 7, 2, 4, 5);
        Assert.False(state.HasItem(4, 5));
        ReceiveTcp(client, ServerPackets.bombUpSpawned, 7, 1, 4, 5);
        Assert.True(state.HasItem(4, 5));
        Assert.Throws<TargetInvocationException>(() =>
            ReceiveTcp(client, ServerPackets.bombUpCollected, 7, 1, 4, 5, 2, 4));
        Assert.True(state.HasItem(4, 5));
        ReceiveTcp(client, ServerPackets.bombUpCollected, 7, 1, 4, 5, 2, 2);
        Assert.False(state.HasItem(4, 5));
        Assert.True(state.TryGetCapacity(2, out int capacity));
        Assert.Equal(2, capacity);

        ReceiveTcp(client, ServerPackets.bombUpSpawned, 7, 1, 5, 5);
        ReceiveTcp(client, ServerPackets.bombUpCollected, 7, 1, 5, 5, 2, 3);
        ReceiveTcp(client, ServerPackets.bombUpSpawned, 7, 1, 6, 5);
        Assert.True(state.HasItem(6, 5));
        ReceiveTcp(client, ServerPackets.bombUpCollected, 7, 1, 6, 5, 2, 3);
        ReceiveTcp(client, ServerPackets.bombUpCollected, 7, 1, 6, 5, 2, 3);
        ReceiveTcp(client, ServerPackets.bombUpSpawned, 7, 1, 6, 5);
        Assert.False(state.HasItem(6, 5));
        Assert.True(state.TryGetCapacity(2, out capacity));
        Assert.Equal(3, capacity);

        ReceiveTcp(client, ServerPackets.matchState, 7, 1, (int)MatchPhase.Finished, 2, 2);
        ReceiveTcp(client, ServerPackets.bombUpSpawned, 7, 1, 4, 5);
        Assert.False(state.HasItem(4, 5));
        Assert.False(state.TryGetCapacity(2, out _));
    }

    [Fact]
    public async Task FinishedClearsBombsBeforeReplay()
    {
        await using var client = ReadyClient();
        var bombs = Read<HashSet<(int, int)>>(client, "bombs");
        var map = new int[17, 15];
        map[4, 5] = 2;
        Set(client, "map", map);
        byte[] session = Read<byte[]>(client, "sessionToken");

        ReceiveTcp(client, ServerPackets.bombPlaced, 7, 1, 3, 5);
        Assert.Contains((3, 5), bombs);
        ReceiveTcp(client, ServerPackets.matchState, 7, 1, (int)MatchPhase.Finished, 2, 1);
        Assert.Empty(bombs);
        ReceiveTcp(client, ServerPackets.matchState, 7, 1, (int)MatchPhase.Finished, 2, 1);
        Assert.Empty(bombs);
        Assert.Same(map, Read<int[,]>(client, "map"));
        Assert.Same(session, Read<byte[]>(client, "sessionToken"));
        Assert.True(Read<bool>(client, "ready"));
    }

    [Fact]
    public async Task BombEventsRequirePlayingAndCurrentScopeEvenForSpectators()
    {
        await using var client = ReadyClient();
        var bombs = Read<HashSet<(int, int)>>(client, "bombs");
        var map = new int[17, 15];
        map[4, 5] = map[5, 5] = 2;
        Set(client, "map", map);
        ReceiveTcp(client, ServerPackets.playerEliminated, 7, 1, 1);

        ReceiveTcp(client, ServerPackets.bombPlaced, 7, 1, 3, 5);
        Assert.Contains((3, 5), bombs);
        ReceiveTcp(client, ServerPackets.bombExploded, 7, 1, 3, 5, 0, 1, 4, 5);
        Assert.Empty(bombs);
        Assert.Equal(0, map[4, 5]);

        ReceiveTcp(client, ServerPackets.matchState, 7, 1, (int)MatchPhase.Finished, 2, 2);
        ReceiveTcp(client, ServerPackets.bombPlaced, 7, 1, 3, 5);
        ReceiveTcp(client, ServerPackets.bombExploded, 7, 1, 3, 5, 0, 1, 5, 5);
        Assert.Empty(bombs);
        Assert.Equal(2, map[5, 5]);
        ReceiveTcp(client, ServerPackets.playerDisconnected, 7, 1, 2);
        Assert.DoesNotContain(2, Read<HashSet<int>>(client, "roster"));

        ReceiveTcp(client, ServerPackets.roundBegin, 7, 2, 2, 1, 3);
        int[] terrain = new int[17 * 15];
        terrain[5 * 15 + 5] = 2;
        ReceiveTcp(client, ServerPackets.mapData, [7, 2, .. terrain]);
        ReceiveTcp(client, ServerPackets.spawnPlayer, 7, 2, 3, 0, 0);
        Assert.NotNull(Read<int[,]>(client, "map"));
        Assert.True(Read<System.Collections.IDictionary>(client, "spawns").Contains(3));

        ReceiveTcp(client, ServerPackets.bombPlaced, 7, 2, 3, 5);
        Assert.Empty(bombs);
        ReceiveTcp(client, ServerPackets.matchState, 7, 2, (int)MatchPhase.Playing, 2, 0);
        ReceiveTcp(client, ServerPackets.bombPlaced, 7, 2, 3, 5);
        ReceiveTcp(client, ServerPackets.bombExploded, 7, 1, 3, 5, 0, 1, 5, 5);
        ReceiveTcp(client, ServerPackets.bombExploded, 8, 2, 3, 5, 0, 1, 5, 5);
        ReceiveTcp(client, ServerPackets.bombPlaced, 7, 1, 6, 5);
        Assert.Equal((3, 5), Assert.Single(bombs));
        Assert.Equal(2, Read<int[,]>(client, "map")[5, 5]);

        var items = Read<BombUpRoundState>(client, "bombUps");
        ReceiveTcp(client, ServerPackets.bombUpSpawned, 7, 2, 5, 5);
        ReceiveTcp(client, ServerPackets.bombUpCollected, 7, 1, 5, 5, 3, 2);
        ReceiveTcp(client, ServerPackets.bombUpSpawned, 7, 1, 6, 5);
        Assert.True(items.HasItem(5, 5));
        Assert.False(items.HasItem(6, 5));
        Assert.True(items.TryGetCapacity(3, out int capacity));
        Assert.Equal(1, capacity);
    }

    private IAsyncDisposable ReadyClient()
    {
        Type errorsType = LoadClient.GetType("GameServer.LoadClient.RunErrors", true)!;
        object errors = Activator.CreateInstance(errorsType, nonPublic: true)!;
        var client = (IAsyncDisposable)Activator.CreateInstance(ClientType, Members, null,
            new object?[] { 1, errors, CancellationToken.None, null }, null)!;
        Set(client, "acceptedProtocolVersion", ProtocolV5.Version);
        Set(client, "connected", true);
        Set(client, "ready", true);
        Set(client, "sessionToken", token);
        Set(client, "id", 1);
        ReceiveTcp(client, ServerPackets.roomChanged, 7);
        ReceiveTcp(client, ServerPackets.roundBegin, 7, 1, 2, 1, 2);
        ReceiveTcp(client, ServerPackets.spawnPlayer, 7, 1, 1, 0, 0);
        ReceiveTcp(client, ServerPackets.spawnPlayer, 7, 1, 2, 0, 0);
        ReceiveTcp(client, ServerPackets.matchState, 7, 1, (int)MatchPhase.Playing, 2, 0);
        return client;
    }

    private static void ReceiveTcp(IAsyncDisposable client, ServerPackets type, params int[] fields)
    {
        using var packet = new Packet((int)type);
        foreach (int field in fields) packet.Write(field);
        packet.WriteLength();
        packet.EncryptPayload();
        Call(client, "HandleTcpPacket", packet.ToArray());
    }

    private static void PrepareObservation(object trace, float x)
    {
        object target = Position(x);
        Call(trace, "Prepare", Position(0), target, true, 0L, 0L);
        Call(trace, "MarkSent", target, 0d);
    }

    private static Assembly LoadAssembly()
    {
        var testOutput = new DirectoryInfo(AppContext.BaseDirectory);
        string path = Path.GetFullPath(Path.Combine(testOutput.FullName, "..", "..", "..", "..",
            "GameServer.LoadClient", "bin", testOutput.Parent!.Name, testOutput.Name, "GameServer.LoadClient.dll"));
        return Assembly.LoadFrom(path);
    }

    private static object Position(float x) => Activator.CreateInstance(PositionType, x, 0f)!;
    private static object? CallStatic(string method, params object?[] arguments) =>
        ClientType.GetMethod(method, Members)!.Invoke(null, arguments);
    private static object? Call(object instance, string method, params object?[] arguments) =>
        instance.GetType().GetMethod(method, Members)!.Invoke(instance, arguments);
    private static T Read<T>(object instance, string field) =>
        (T)ClientType.GetField(field, Members)!.GetValue(instance)!;
    private static void Set(object instance, string field, object? value) =>
        ClientType.GetField(field, Members)!.SetValue(instance, value);
}
