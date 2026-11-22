using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using GameShared;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

public sealed class MovementOrderingTests
{
    [Test]
    public void OnlyASubmittedMovementConsumesTheNextSequence()
    {
        var ordering = new MovementOrdering();
        Assert.That(ordering.TrySend(seq => seq == 1), Is.True);
        Assert.That(ordering.TrySend(seq => { Assert.That(seq, Is.EqualTo(2)); return false; }), Is.False);
        Assert.That(ordering.TrySend(seq => seq == 2), Is.True);
        Assert.That(ordering.TrySend(seq => seq == 3), Is.True);
    }

    [Test]
    public void ExhaustionStopsBeforeProducingAnotherFrame()
    {
        var ordering = new MovementOrdering();
        typeof(MovementOrdering).GetField("lastSent", BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(ordering, long.MaxValue - 1);
        Assert.That(ordering.TrySend(seq => seq == long.MaxValue), Is.True);
        Assert.That(ordering.TrySend(_ => throw new AssertionException("An exhausted stream cannot send")), Is.False);
        ordering.ResetLocal();
        Assert.That(ordering.TrySend(seq => seq == 1), Is.True);
    }

    [TestCase(11, 12, true)]
    [TestCase(12, 11, false)]
    [TestCase(11, 11, false)]
    public void PeerTargetOnlyChangesForANewerSequence(int first, int second, bool acceptsSecond)
    {
        var ordering = new MovementOrdering();
        Assert.That(ordering.TryRecordPeer(2, first, Vector2.left), Is.True);
        Assert.That(ordering.TryRecordPeer(2, second, Vector2.right), Is.EqualTo(acceptsSecond));
        Assert.That(ordering.TryGetPeerTarget(2, out var target), Is.True);
        Assert.That(target, Is.EqualTo(acceptsSecond ? Vector2.right : Vector2.left));
        Assert.That(ordering.TryRecordPeer(3, 1, Vector2.up), Is.True);
        ordering.RemovePeer(2);
        Assert.That(ordering.TryGetPeerTarget(2, out _), Is.False);
        Assert.That(ordering.TryRecordPeer(2, 1, Vector2.down), Is.True);
        Assert.That(ordering.TryGetPeerTarget(3, out target) && target == Vector2.up, Is.True);
    }

    [Test]
    public void NewCorrectionStillAppliesAfterNewerPredictionWasSent()
    {
        var ordering = SentThrough(14);
        Assert.That(ordering.QueueOwnerState(12, 11, At(0.08f)), Is.True);
        Assert.That(ordering.QueueOwnerState(12, 11, At(0.08f)), Is.False);
        Assert.That(ordering.QueueOwnerState(11, 10, At(0.04f)), Is.False);
        Assert.That(ordering.QueueOwnerState(13, 14, At(0.24f)), Is.False);
        Assert.That(ordering.TryTakeOwnerPosition(out var target) && target == At(0.08f), Is.True);
        Assert.That(ordering.TryTakeOwnerPosition(out _), Is.False);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void AckSupersedesCorrectionBeforeTheFrameAppliesAnyMovement(bool ackFirst)
    {
        var ordering = SentThrough(13);
        Assert.That(ordering.QueueOwnerState(11, 11, At(0.08f)), Is.True);
        Assert.That(ordering.TryTakeOwnerPosition(out _), Is.False);
        if (ackFirst)
        {
            Assert.That(ordering.QueueOwnerState(13, 13, At(0.24f)), Is.True);
            Assert.That(ordering.QueueOwnerState(12, 11, At(0.08f)), Is.False);
        }
        else
        {
            Assert.That(ordering.QueueOwnerState(12, 11, At(0.08f)), Is.True);
            Assert.That(ordering.QueueOwnerState(13, 13, At(0.24f)), Is.True);
        }
        Assert.That(ordering.QueueOwnerState(13, 13, At(0.24f)), Is.False);
        Assert.That(ordering.TryTakeOwnerPosition(out _), Is.False);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void OnlyFinalAckCanRestorePositionAfterCorrectionWasApplied(bool hasNewerPrediction)
    {
        var ordering = SentThrough(hasNewerPrediction ? 14 : 13);
        Assert.That(ordering.QueueOwnerState(12, 11, At(0.08f)), Is.True);
        Assert.That(ordering.TryTakeOwnerPosition(out var snap) && snap == At(0.08f), Is.True);
        Assert.That(ordering.QueueOwnerState(13, 13, At(0.24f)), Is.True);
        if (hasNewerPrediction)
        {
            // The last move (14) has not been seen by the server. ACK 13 cannot acknowledge it.
            Assert.That(ordering.TryTakeOwnerPosition(out _), Is.False);
            Assert.That(ordering.QueueOwnerState(15, 15, At(0.40f)), Is.False);
            Assert.That(ordering.QueueOwnerState(14, 14, At(0.32f)), Is.True);
        }
        Assert.That(ordering.TryTakeOwnerPosition(out var restored), Is.True);
        Assert.That(restored, Is.EqualTo(At(hasNewerPrediction ? 0.32f : 0.24f)));
        Assert.That(ordering.TryTakeOwnerPosition(out _), Is.False);
    }

    [Test]
    public void RoundResetClearsOwnerWatermarkPendingAndRecovery()
    {
        var ordering = SentThrough(13);
        ordering.QueueOwnerState(12, 11, At(0.08f));
        Assert.That(ordering.TryTakeOwnerPosition(out _), Is.True);
        ordering.QueueOwnerState(13, 13, At(0.24f));
        ordering.Reset();
        Assert.That(ordering.TryTakeOwnerPosition(out _), Is.False);
        Assert.That(ordering.TrySend(sequence => sequence == 1), Is.True);
        Assert.That(ordering.QueueOwnerState(1, 1, Vector2.right), Is.True);
        Assert.That(ordering.TryTakeOwnerPosition(out _), Is.False);
    }

    private static Vector2 At(float x) => new Vector2(x, 0);

    private static MovementOrdering SentThrough(int last)
    {
        var ordering = new MovementOrdering();
        for (long sequence = 1; sequence <= last; sequence++)
            Assert.That(ordering.TrySend(next => next == sequence), Is.True);
        return ordering;
    }
}

[Category("UnityRuntime")]
public sealed class MovementOrderingUnityTests
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private readonly List<GameObject> objects = new List<GameObject>();
    private Type gameType, clientType;
    private FieldInfo playerField, gameInstance, clientInstance;
    private object previousPlayers, previousGame, previousClient;
    private Component game;
    private IDictionary players;

    [SetUp]
    public void SetUp()
    {
        gameType = FindType("GameManager");
        clientType = FindType("Client");
        playerField = gameType.GetField("players");
        gameInstance = gameType.GetField("instance");
        clientInstance = clientType.GetField("instance");
        previousPlayers = playerField.GetValue(null);
        previousGame = gameInstance.GetValue(null);
        previousClient = clientInstance.GetValue(null);
        players = (IDictionary)Activator.CreateInstance(playerField.FieldType);
        playerField.SetValue(null, players);
        gameInstance.SetValue(null, null);
        clientInstance.SetValue(null, null);
        game = NewObject().AddComponent(gameType);
        var client = NewObject().AddComponent(clientType);
        gameInstance.SetValue(null, game);
        clientInstance.SetValue(null, client);
        clientType.GetField("myId").SetValue(client, 1);
        Invoke("ChangeRoom", 7);
        Invoke("BeginRound", 1, new[] { 1, 2 });
    }

    [TearDown]
    public void TearDown()
    {
        // Detach the temporary scene objects before GameManager teardown.
        players.Clear();
        gameInstance.SetValue(null, previousGame);
        clientInstance.SetValue(null, previousClient);
        playerField.SetValue(null, previousPlayers);
        for (int i = objects.Count - 1; i >= 0; i--)
            if (objects[i]) Object.DestroyImmediate(objects[i]);
        objects.Clear();
    }

    [Test]
    public void PreSpawnSnapshotAppliesWhenPlayingAndStaleDoesNotReachSmoothing()
    {
        Peer(7, 1, 12, Vector2.right);
        Peer(7, 1, 11, Vector2.left);
        var prefab = NewObject();
        prefab.AddComponent(FindType("RemotePlayerController"));
        prefab.AddComponent<SpriteRenderer>();
        gameType.GetField("playerPrefab").SetValue(game, prefab);
        Invoke("SpawnPlayer", 2, Vector2.zero);
        var remoteObject = (GameObject)players[2];
        objects.Add(remoteObject);
        var remote = remoteObject.GetComponent(FindType("RemotePlayerController"));
        var sprite = remoteObject.GetComponent<SpriteRenderer>();
        var target = remote.GetType().GetField("targetPosition", Instance);
        Assert.That(target.GetValue(remote), Is.Null);
        Invoke("ApplyMatchState", 1, MatchPhase.Playing, 2, 0);
        Assert.That(target.GetValue(remote), Is.EqualTo(Vector2.right));

        var hold = remote.GetType().GetField("lastTargetChangeTime", Instance);
        hold.SetValue(remote, -123d);
        Peer(7, 1, 11, Vector2.left);
        Peer(7, 1, 12, Vector2.left);
        Assert.That(target.GetValue(remote), Is.EqualTo(Vector2.right));
        Assert.That(hold.GetValue(remote), Is.EqualTo(-123d));
        Assert.That(sprite.flipX, Is.False);
        Peer(7, 1, 13, Vector2.left);
        Assert.That(target.GetValue(remote), Is.EqualTo(Vector2.left));
        Assert.That(hold.GetValue(remote), Is.Not.EqualTo(-123d));
    }

    [Test]
    public void ReplayRoomChangeAndSessionResetAllowReusedPlayerIdButDropOldScope()
    {
        var ordering = (MovementOrdering)gameType.GetField("movementOrdering", Instance).GetValue(game);
        Assert.That(ordering.TrySend(seq => seq == 1), Is.True);
        Peer(7, 1, 12, Vector2.right);
        Invoke("ApplyMatchState", 1, MatchPhase.Finished, 2, 0);
        Peer(7, 1, 13, Vector2.left);
        Assert.That(ordering.TryGetPeerTarget(2, out _), Is.False);
        Invoke("BeginRound", 2, new[] { 1, 2 });
        Peer(7, 1, 13, Vector2.left);
        Peer(7, 2, 1, Vector2.up);
        Assert.That(ordering.TryGetPeerTarget(2, out var target) && target == Vector2.up, Is.True);
        Assert.That(ordering.TrySend(seq => seq == 1), Is.True);

        Invoke("ChangeRoom", 8);
        Invoke("BeginRound", 1, new[] { 1, 2 });
        Peer(7, 2, 14, Vector2.right);
        Assert.That(ordering.TryGetPeerTarget(2, out _), Is.False);
        Assert.That(ordering.TrySend(seq => seq == 1), Is.True);
        gameType.GetMethod("RemovePlayer").Invoke(null, new object[] { 2 });
        Peer(8, 1, 1, Vector2.up);
        Assert.That(ordering.TryGetPeerTarget(2, out _), Is.False);
        gameType.GetMethod("ClearSession").Invoke(null, new object[] { "Disconnected" });
        Assert.That(ordering.TrySend(seq => seq == 1), Is.True);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void TcpOwnerMessagesApplyOnlyOnceAtFrameEnd(bool separateFrames)
    {
        var local = NewObject();
        players.Add(1, local);
        Invoke("ApplyMatchState", 1, MatchPhase.Playing, 2, 0);
        var ordering = (MovementOrdering)gameType.GetField("movementOrdering", Instance).GetValue(game);
        for (int i = 0; i < 13; i++) ordering.TrySend(_ => true);
        var predicted = new Vector3(0.24f, 0, 0);
        var corrected = new Vector3(0.08f, 0, 0);
        local.transform.position = predicted;
        var connection = InstallConnection();
        using var ackBefore = ProtocolV4.CreateTcpPlayerPosition(7, 1, 1, 11, 0.08f, 0);
        using var correction = ProtocolV4.CreateTcpPlayerCorrection(7, 1, 1, 12, 11, 0.08f, 0);
        using var ackAfter = ProtocolV4.CreateTcpPlayerPosition(7, 1, 1, 13, 0.24f, 0);
        Receive(connection, ackBefore);
        Receive(connection, correction);
        DrainNetwork();
        Assert.That(local.transform.position, Is.EqualTo(predicted));
        if (separateFrames)
        {
            Invoke("LateUpdate");
            Assert.That(local.transform.position, Is.EqualTo(corrected));
        }
        Receive(connection, ackAfter);
        DrainNetwork();
        Invoke("LateUpdate");
        Assert.That(local.transform.position, Is.EqualTo(predicted));
        local.transform.position = Vector3.up;
        Receive(connection, correction);
        Receive(connection, ackAfter);
        DrainNetwork();
        Invoke("LateUpdate");
        Assert.That(local.transform.position, Is.EqualTo(Vector3.up));
    }

    [Test]
    public void RetiredTcpCallbackAndWrongOwnerScopeCannotQueueASnap()
    {
        var local = NewObject();
        players.Add(1, local);
        local.transform.position = Vector3.right;
        Invoke("ApplyMatchState", 1, MatchPhase.Playing, 2, 0);
        var ordering = (MovementOrdering)gameType.GetField("movementOrdering", Instance).GetValue(game);
        ordering.TrySend(_ => true);
        var oldConnection = InstallConnection();
        using var correction = ProtocolV4.CreateTcpPlayerCorrection(7, 1, 1, 1, 0, 0, 0);
        Receive(oldConnection, correction);
        var connection = InstallConnection();
        using var wrongRoom = ProtocolV4.CreateTcpPlayerCorrection(8, 1, 1, 1, 0, 0, 0);
        using var wrongRound = ProtocolV4.CreateTcpPlayerCorrection(7, 2, 1, 1, 0, 0, 0);
        using var wrongPlayer = ProtocolV4.CreateTcpPlayerCorrection(7, 1, 2, 1, 0, 0, 0);
        Receive(connection, wrongRoom);
        Receive(connection, wrongRound);
        Receive(connection, wrongPlayer);
        DrainNetwork();
        Invoke("LateUpdate");
        Assert.That(local.transform.position, Is.EqualTo(Vector3.right));

        Receive(connection, correction);
        DrainNetwork();
        Invoke("LateUpdate");
        Assert.That(local.transform.position, Is.EqualTo(Vector3.zero));
    }

    private object InstallConnection()
    {
        var type = clientType.GetNestedType("TCP");
        var connection = Activator.CreateInstance(type);
        type.GetProperty("AcceptedProtocolVersion", Instance).SetValue(connection, ProtocolV4.Version);
        clientType.GetField("tcp").SetValue(clientInstance.GetValue(null), connection);
        return connection;
    }

    private static void Receive(object connection, Packet packet) =>
        connection.GetType().GetMethod("HandleFrame", Instance).Invoke(connection, new object[] { packet.ToArray() });

    private static void DrainNetwork() => FindType("ThreadManager").GetMethod("UpdateMain").Invoke(null, null);

    private void Peer(int room, int round, long seq, Vector2 position) =>
        Invoke("ApplyOrderedPeerPosition", room, round, 2, seq, position);

    private void Invoke(string method, params object[] args) => gameType.GetMethod(method, Instance).Invoke(game, args);

    private GameObject NewObject()
    {
        var item = new GameObject("Movement ordering test");
        objects.Add(item);
        return item;
    }

    private static Type FindType(string name) => Type.GetType(name + ", Assembly-CSharp", throwOnError: true);
}
