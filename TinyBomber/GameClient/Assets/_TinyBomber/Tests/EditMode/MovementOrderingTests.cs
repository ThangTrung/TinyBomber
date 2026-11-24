using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using GameShared;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

public sealed class MovementOrderingTests
{
    private const double Delay = 0.25;
    private static readonly (object, int, int) Scope = (new object(), 7, 1);

    internal static bool Submit(MovementOrdering ordering, Func<long, bool> send) =>
        ordering.TrySend(Scope, Vector2.zero, 0, Delay, (sequence, _) => send(sequence), (_, _) => false);

    [Test]
    public void OnlyASubmittedMovementConsumesTheNextSequence()
    {
        var ordering = new MovementOrdering();
        Assert.That(MovementOrderingTests.Submit(ordering, seq => seq == 1), Is.True);
        Assert.That(MovementOrderingTests.Submit(ordering, seq => { Assert.That(seq, Is.EqualTo(2)); return false; }), Is.False);
        Assert.That(MovementOrderingTests.Submit(ordering, seq => seq == 2), Is.True);
        Assert.That(MovementOrderingTests.Submit(ordering, seq => seq == 3), Is.True);
    }

    [Test]
    public void ExhaustionStopsBeforeProducingAnotherFrame()
    {
        var ordering = new MovementOrdering();
        typeof(MovementOrdering).GetField("lastSent", BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(ordering, long.MaxValue - 1);
        Assert.That(MovementOrderingTests.Submit(ordering, seq => seq == long.MaxValue), Is.True);
        Assert.That(MovementOrderingTests.Submit(ordering, _ => throw new AssertionException("An exhausted stream cannot send")), Is.False);
        ordering.ResetLocal();
        Assert.That(MovementOrderingTests.Submit(ordering, seq => seq == 1), Is.True);
    }

    [TestCase(11, 12, true)]
    [TestCase(12, 11, false)]
    [TestCase(11, 11, false)]
    public void PeerTargetOnlyChangesForANewerSequence(int first, int second, bool acceptsSecond)
    {
        var ordering = new MovementOrdering();
        Assert.That(ordering.TryRecordPeer(2, first, Vector2.left), Is.True);
        Assert.That(ordering.TryRecordPeer(2, second, Vector2.right), Is.EqualTo(acceptsSecond));
        Assert.That(ordering.TryGetPeerTarget(2, out var target, out _), Is.True);
        Assert.That(target, Is.EqualTo(acceptsSecond ? Vector2.right : Vector2.left));
        Assert.That(ordering.TryRecordPeer(3, 1, Vector2.up), Is.True);
        ordering.RemovePeer(2);
        Assert.That(ordering.TryGetPeerTarget(2, out _, out _), Is.False);
        Assert.That(ordering.TryRecordPeer(2, 1, Vector2.down), Is.True);
        Assert.That(ordering.TryGetPeerTarget(3, out target, out _) && target == Vector2.up, Is.True);
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
        Assert.That(MovementOrderingTests.Submit(ordering, sequence => sequence == 1), Is.True);
        Assert.That(ordering.QueueOwnerState(1, 1, Vector2.right), Is.True);
        Assert.That(ordering.TryTakeOwnerPosition(out _), Is.False);
    }

    [Test]
    public void TailRetryKeepsSequenceAndPositionAndIsAttemptedOnce()
    {
        var ordering = new MovementOrdering();
        int attempts = 0;
        var target = At(0.08f);
        Assert.That(ordering.TrySend(Scope, target, 1, Delay, (seq, position) =>
            seq == 1 && position.Equals(target), (_, _) => false), Is.True);
        Assert.That(ordering.TryRetry(Scope, 1.249, Delay, Retry), Is.False);
        Assert.That(ordering.TryRetry(Scope, 1.25, Delay, Retry), Is.True);
        Assert.That(ordering.TryRetry(Scope, 100, Delay, Retry), Is.False);
        Assert.That(attempts, Is.EqualTo(1));
        Assert.That(ordering.QueueOwnerState(1, 1, target), Is.True);
        Assert.That(Submit(ordering, seq => seq == 2), Is.True);

        bool Retry(long sequence, Vector2 position)
        {
            attempts++;
            Assert.That(sequence, Is.EqualTo(1));
            Assert.That(position, Is.EqualTo(target));
            return true;
        }
    }

    [TestCase(0L)]
    [TestCase(1L)]
    public void CorrectionOrAckBeforeTimeoutCancelsTailRetry(long accepted)
    {
        var ordering = SentThrough(1);
        Assert.That(ordering.QueueOwnerState(1, accepted, Vector2.zero), Is.True);
        Assert.That(ordering.TryRetry(Scope, 1, Delay, (_, _) => throw new AssertionException("Already processed")), Is.False);
    }

    [Test]
    public void NewMovementReplacesOldTailAndRestartsTimeout()
    {
        var ordering = SentThrough(1);
        Assert.That(ordering.TrySend(Scope, At(0.16f), 0.125, Delay, (seq, _) => seq == 2, (_, _) => false), Is.True);
        Assert.That(ordering.QueueOwnerState(1, 1, Vector2.zero), Is.True);
        Assert.That(ordering.TryRetry(Scope, 0.25, Delay, (_, _) => throw new AssertionException("Too early")), Is.False);
        Assert.That(ordering.TryRetry(Scope, 0.375, Delay, (seq, pos) => seq == 2 && pos.Equals(At(0.16f))), Is.True);
        Assert.That(ordering.QueueOwnerState(2, 1, At(0.08f)), Is.True);
        Assert.That(ordering.TryTakeOwnerPosition(out var corrected) && corrected.Equals(At(0.08f)), Is.True);
    }

    [Test]
    public void FailedUdpEnqueuesIdenticalCandidateViaTcpAndThrottlesFurtherFallback()
    {
        var ordering = new MovementOrdering();
        var pos = At(0.08f);
        int tcpCalls = 0;
        bool Tcp(long sequence, Vector2 position)
        {
            tcpCalls++;
            Assert.That(sequence, Is.EqualTo(1));
            Assert.That(position, Is.EqualTo(pos));
            return true;
        }
        Assert.That(ordering.TrySend(Scope, pos, 0, Delay, (_, _) => false, Tcp), Is.True);
        Assert.That(ordering.TrySend(Scope, At(0.16f), 0.125, Delay, (_, _) => false, Tcp), Is.False);
        Assert.That(ordering.TryRetry(Scope, 1, Delay, Tcp), Is.False);
        Assert.That(tcpCalls, Is.EqualTo(1));
        Assert.That(Submit(ordering, seq => seq == 2), Is.True);
    }

    [Test]
    public void BothSendFailuresLeaveSequenceUnspentAndLimitTcpAttempts()
    {
        var ordering = new MovementOrdering();
        int attempts = 0;
        bool FailTcp(long sequence, Vector2 _)
        {
            Assert.That(sequence, Is.EqualTo(1));
            attempts++;
            return false;
        }
        for (int frame = 0; frame < 10; frame++)
            Assert.That(ordering.TrySend(Scope, Vector2.right, frame * 0.02, Delay, (_, _) => false, FailTcp), Is.False);
        Assert.That(attempts, Is.EqualTo(1));
        Assert.That(Submit(ordering, seq => seq == 1), Is.True);
    }

    [Test]
    public void RetryIsScopedToItsSessionRoomAndRoundAndClearedOnReset()
    {
        var ordering = SentThrough(1);
        bool NoRetry(long _, Vector2 __) => throw new AssertionException("Previous scope cannot send");
        Assert.That(ordering.TryRetry((new object(), Scope.Item2, Scope.Item3), 1, Delay, NoRetry), Is.False);
        Assert.That(ordering.TryRetry((Scope.Item1, 8, Scope.Item3), 1, Delay, NoRetry), Is.False);
        Assert.That(ordering.TryRetry((Scope.Item1, Scope.Item2, 2), 1, Delay, NoRetry), Is.False);
        ordering.Reset();
        Assert.That(ordering.TryRetry(Scope, 1, Delay, NoRetry), Is.False);
        Assert.That(Submit(ordering, seq => seq == 1), Is.True);
    }

    [Test]
    public void ReliableRefreshCanConfirmEqualTargetOnceAndCannotReplaceNewerUdpTarget()
    {
        var ordering = new MovementOrdering();
        Assert.That(ordering.TryRecordPeer(2, 11, Vector2.left), Is.True);
        Assert.That(ordering.TryRecordPeer(2, 12, Vector2.right), Is.True);
        Assert.That(ordering.TryRecordPeer(2, 11, Vector2.left, reliable: true), Is.False);
        Assert.That(ordering.TryRecordPeer(2, 12, Vector2.left, reliable: true), Is.False);
        Assert.That(ordering.TryRecordPeer(2, 12, Vector2.right, reliable: true), Is.True);
        Assert.That(ordering.TryRecordPeer(2, 12, Vector2.right, reliable: true), Is.False);
        Assert.That(ordering.TryRecordPeer(2, 12, Vector2.right), Is.False);
        Assert.That(ordering.TryGetPeerTarget(2, out var target, out var reliable) &&
            reliable && target.Equals(Vector2.right), Is.True);
        Assert.That(ordering.TryRecordPeer(2, 13, Vector2.up, reliable: true), Is.True);
        Assert.That(ordering.TryGetPeerTarget(2, out target, out reliable) &&
            reliable && target.Equals(Vector2.up), Is.True);
    }

    private static Vector2 At(float x) => new Vector2(x, 0);

    private static MovementOrdering SentThrough(int last)
    {
        var ordering = new MovementOrdering();
        for (long sequence = 1; sequence <= last; sequence++)
            Assert.That(MovementOrderingTests.Submit(ordering, next => next == sequence), Is.True);
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

        hold.SetValue(remote, -125d);
        Invoke("ApplyOrderedPeerPosition", 7, 1, 2, 12L, Vector2.right, true);
        Invoke("ApplyOrderedPeerPosition", 7, 1, 2, 13L, Vector2.left, true);
        Assert.That(target.GetValue(remote), Is.EqualTo(Vector2.left));
        Assert.That(hold.GetValue(remote), Is.EqualTo(-125d));
        Assert.That(remote.GetType().GetField("finishAtTarget", Instance).GetValue(remote), Is.True);
    }

    [Test]
    public void ReplayRoomChangeAndSessionResetAllowReusedPlayerIdButDropOldScope()
    {
        var ordering = (MovementOrdering)gameType.GetField("movementOrdering", Instance).GetValue(game);
        var scope = (new object(), 7, 1);
        Assert.IsTrue(ordering.TrySend(scope, Vector2.zero, 0, 0.25, (seq, _) => seq == 1, (_, _) => false));
        Peer(7, 1, 12, Vector2.right);
        Invoke("ApplyMatchState", 1, MatchPhase.Finished, 2, 0);
        Peer(7, 1, 13, Vector2.left);
        Assert.That(ordering.TryGetPeerTarget(2, out _, out _), Is.False);
        Assert.IsFalse(ordering.TryRetry(scope, 1, 0.25, (_, _) => true));
        Invoke("BeginRound", 2, new[] { 1, 2 });
        Peer(7, 1, 13, Vector2.left);
        Peer(7, 2, 1, Vector2.up);
        Assert.That(ordering.TryGetPeerTarget(2, out var target, out _) && target == Vector2.up, Is.True);
        Assert.That(MovementOrderingTests.Submit(ordering, seq => seq == 1), Is.True);

        Invoke("ChangeRoom", 8);
        Invoke("BeginRound", 1, new[] { 1, 2 });
        Peer(7, 2, 14, Vector2.right);
        Assert.That(ordering.TryGetPeerTarget(2, out _, out _), Is.False);
        Assert.That(MovementOrderingTests.Submit(ordering, seq => seq == 1), Is.True);
        gameType.GetMethod("RemovePlayer").Invoke(null, new object[] { 2 });
        Peer(8, 1, 1, Vector2.up);
        Assert.That(ordering.TryGetPeerTarget(2, out _, out _), Is.False);
        gameType.GetMethod("ClearSession").Invoke(null, new object[] { "Disconnected" });
        Assert.That(MovementOrderingTests.Submit(ordering, seq => seq == 1), Is.True);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void TcpOwnerMessagesApplyOnlyOnceAtFrameEnd(bool separateFrames)
    {
        var local = NewObject();
        players.Add(1, local);
        Invoke("ApplyMatchState", 1, MatchPhase.Playing, 2, 0);
        var ordering = (MovementOrdering)gameType.GetField("movementOrdering", Instance).GetValue(game);
        for (int i = 0; i < 13; i++) MovementOrderingTests.Submit(ordering, _ => true);
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
        MovementOrderingTests.Submit(ordering, _ => true);
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

    [Test]
    public void RetiredTcpWrapperCannotSpawnItemInReusedRoomAndRound()
    {
        Invoke("ApplyMatchState", 1, MatchPhase.Playing, 2, 0);
        var state = (BombUpRoundState)gameType.GetField("bombUps", Instance).GetValue(game);
        using var spawn = new Packet((int)ServerPackets.bombUpSpawned);
        foreach (int field in new[] { 7, 1, 4, 5 }) spawn.Write(field);
        spawn.WriteLength();
        spawn.EncryptPayload();
        Receive(InstallConnection(), spawn);
        var current = InstallConnection();
        DrainNetwork();
        Assert.IsFalse(state.HasItem(4, 5));
        Receive(current, spawn);
        DrainNetwork();
        Assert.IsTrue(state.HasItem(4, 5));
        Invoke("ApplyMatchState", 1, MatchPhase.Finished, 2, 1);
        Receive(current, spawn);
        DrainNetwork();
        Assert.IsFalse(state.HasItem(4, 5));
    }

    private object InstallConnection()
    {
        var type = clientType.GetNestedType("TCP");
        var connection = Activator.CreateInstance(type);
        type.GetProperty("AcceptedProtocolVersion", Instance).SetValue(connection, ProtocolV5.Version);
        clientType.GetField("tcp").SetValue(clientInstance.GetValue(null), connection);
        return connection;
    }

    private static void Receive(object connection, Packet packet) =>
        connection.GetType().GetMethod("HandleFrame", Instance).Invoke(connection, new object[] { packet.ToArray() });

    private static void DrainNetwork() => FindType("ThreadManager").GetMethod("UpdateMain").Invoke(null, null);

    private void Peer(int room, int round, long seq, Vector2 position) =>
        Invoke("ApplyOrderedPeerPosition", room, round, 2, seq, position, false);

    private void Invoke(string method, params object[] args) => gameType.GetMethod(method, Instance).Invoke(game, args);

    private GameObject NewObject()
    {
        var item = new GameObject("Movement ordering test");
        objects.Add(item);
        return item;
    }

    private static Type FindType(string name) => Type.GetType(name + ", Assembly-CSharp", throwOnError: true);
}


[Category("UnityRuntime")]
public sealed class RoundCleanupUnityTests
{
    private const double WaitTimeoutSeconds = 5;

    [UnityTearDown]
    public IEnumerator ReturnToEditMode()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            yield return new ExitPlayMode();
    }

    [UnityTest]
    public IEnumerator ReplayDestroysOldObjectsAndIgnoresLateBombEvents()
    {
        bool expectDomainReload = !EditorSettings.enterPlayModeOptionsEnabled ||
            (EditorSettings.enterPlayModeOptions & EnterPlayModeOptions.DisableDomainReload) == 0;
        yield return new EnterPlayMode(expectDomainReload);
        Assert.IsTrue(Application.isPlaying, "Runtime Destroy must be tested in Play Mode.");

        const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var gameType = FindType("GameManager");
        var clientType = FindType("Client");
        var handleType = FindType("ClientHandle");
        var remoteType = FindType("RemotePlayerController");
        var gameSingleton = gameType.GetField("instance");
        var clientSingleton = clientType.GetField("instance");
        var playerField = gameType.GetField("players");
        var softBlockField = gameType.GetField("softBlocks");
        object previousGame = gameSingleton.GetValue(null), previousClient = clientSingleton.GetValue(null);
        object previousPlayers = playerField.GetValue(null), previousSoftBlocks = softBlockField.GetValue(null);
        var players = new Dictionary<int, GameObject>();
        var softBlocks = new Dictionary<Vector2Int, GameObject>();
        var fixture = new GameObject("Round cleanup test");
        var managerObject = new GameObject("Test GameManager");
        var clientObject = new GameObject("Test Client");
        managerObject.transform.SetParent(fixture.transform);
        clientObject.transform.SetParent(fixture.transform);
        float oldTimeScale = Time.timeScale;
        try
        {
            Time.timeScale = 1;
            gameSingleton.SetValue(null, null);
            clientSingleton.SetValue(null, null);
            playerField.SetValue(null, players);
            softBlockField.SetValue(null, softBlocks);
            var manager = (Behaviour)managerObject.AddComponent(gameType);
            var client = (Behaviour)clientObject.AddComponent(clientType);
            manager.enabled = client.enabled = false;
            clientType.GetField("myId").SetValue(client, 1);

            var tilePrefab = new GameObject("Test tile prefab");
            tilePrefab.transform.SetParent(fixture.transform);
            var remotePrefab = new GameObject("Test remote prefab");
            remotePrefab.transform.SetParent(fixture.transform);
            remotePrefab.SetActive(false);
            remotePrefab.AddComponent(remoteType);
            gameType.GetField("playerPrefab").SetValue(manager, remotePrefab);
            foreach (string name in new[] { "bombPrefab", "bombUpPrefab", "softBlockPrefab" })
                gameType.GetField(name).SetValue(manager, tilePrefab);

            var bombs = (Dictionary<Vector2Int, GameObject>)gameType.GetField("bombs", Instance).GetValue(manager);
            var items = (Dictionary<Vector2Int, GameObject>)gameType.GetField("itemViews", Instance).GetValue(manager);
            var blasts = (List<GameObject>)gameType.GetField("blasts", Instance).GetValue(manager);
            var bombCell = new Vector2Int(3, 5);
            var itemCell = new Vector2Int(4, 5);
            var softCell = new Vector2Int(5, 5);
            var terrain = new int[17, 15];
            terrain[5, 5] = 2;
            Call("ChangeRoom", 7);
            StartRound(1, terrain);

            var oldPlayer = players[2];
            var oldRemote = oldPlayer.GetComponent(remoteType);
            var remoteTarget = remoteType.GetField("targetPosition", Instance);
            var animationHold = remoteType.GetField("lastTargetChangeTime", Instance);
            Call("ApplyOrderedPeerPosition", 7, 1, 2, 1L, Vector2.up, false);
            Assert.AreEqual(Vector2.up, remoteTarget.GetValue(oldRemote));
            animationHold.SetValue(oldRemote, 123d);
            BombEvent("BombPlaced", 1, 3, 5);
            BombEvent("BombExploded", 1, 3, 5, 1, 3, 5, 0);
            Assert.IsEmpty(bombs);
            Assert.AreEqual(1, blasts.Count);
            var oldFlash = blasts[0];
            double oldFlashWindowEnd = Time.timeAsDouble + 0.25;
            BombEvent("BombPlaced", 1, 3, 5);
            var oldBomb = bombs[bombCell];
            Call("ApplyBombUp", 7, 1, 4, 5, 0, 0);
            var oldItem = items[itemCell];
            var oldSoft = softBlocks[softCell];

            Call("ApplyMatchState", 1, MatchPhase.Finished, 2, 2);
            BombEvent("BombPlaced", 1, 3, 5);
            BombEvent("BombExploded", 1, 3, 5, 1, 3, 5, 1, 5, 5);
            Assert.IsEmpty(bombs);
            Assert.IsEmpty(items);
            Assert.AreEqual(1, blasts.Count);
            Assert.AreEqual(2, terrain[5, 5]);
            Assert.IsTrue(oldFlash != null);

            // Replay in the same frame while the old flash is still awaiting destruction.
            var replayTerrain = new int[17, 15];
            replayTerrain[5, 5] = 2;
            StartRound(2, replayTerrain);
            BombEvent("BombPlaced", 2, 3, 5);
            Call("ApplyBombUp", 7, 2, 4, 5, 0, 0);
            BombEvent("BombPlaced", 1, 6, 5);
            BombEvent("BombExploded", 1, 3, 5, 1, 3, 5, 1, 5, 5);
            Call("ApplyBombUp", 7, 1, 6, 5, 0, 0);
            Assert.AreEqual(1, bombs.Count);
            Assert.AreEqual(1, items.Count);
            Assert.AreEqual(2, replayTerrain[5, 5]);
            Assert.IsEmpty(blasts);

            var newPlayer = players[2];
            var newRemote = newPlayer.GetComponent(remoteType);
            Assert.AreNotSame(oldPlayer, newPlayer);
            Assert.IsNull(remoteTarget.GetValue(newRemote));
            Assert.AreEqual(double.NegativeInfinity, animationHold.GetValue(newRemote));
            var newBomb = bombs[bombCell];
            var newItem = items[itemCell];
            var newSoft = softBlocks[softCell];
            yield return null;
            Assert.IsTrue(oldPlayer == null && oldBomb == null && oldItem == null && oldSoft == null && oldFlash == null);
            // Let the old flash's delayed Destroy window elapse before touching the new objects.
            double deadline = Time.realtimeSinceStartupAsDouble + WaitTimeoutSeconds;
            while (Time.timeAsDouble < oldFlashWindowEnd && Time.realtimeSinceStartupAsDouble < deadline)
                yield return null;
            Assert.GreaterOrEqual(Time.timeAsDouble, oldFlashWindowEnd, "Play Mode time did not advance.");
            Assert.IsTrue(newPlayer != null && newBomb != null && newItem != null && newSoft != null);

            BombEvent("BombExploded", 2, 3, 5, 1, 3, 5, 1, 5, 5);
            Assert.AreEqual(0, replayTerrain[5, 5]);
            var replayFlash = blasts[0];
            Call("ApplyMatchState", 2, MatchPhase.Finished, 2, 2);
            Assert.IsTrue(replayFlash != null);
            double replayFlashWindowEnd = Time.timeAsDouble + 0.25;
            deadline = Time.realtimeSinceStartupAsDouble + WaitTimeoutSeconds;
            while (Time.timeAsDouble < replayFlashWindowEnd && Time.realtimeSinceStartupAsDouble < deadline)
                yield return null;
            Assert.GreaterOrEqual(Time.timeAsDouble, replayFlashWindowEnd, "Play Mode time did not advance.");
            Assert.IsTrue(replayFlash == null);
            Assert.IsTrue(newItem == null && newBomb == null && newSoft == null);

            void Call(string method, params object[] args) => gameType.GetMethod(method, Instance).Invoke(manager, args);

            void StartRound(int round, int[,] map)
            {
                Call("BeginRound", round, new[] { 1, 2 });
                Call("BuildMap", map);
                Call("SpawnPlayer", 2, Vector2.zero);
                Call("SpawnPlayer", 2, Vector2.one);
                Assert.AreEqual(1, players.Count);
                Assert.AreEqual(Vector3.zero, players[2].transform.position);
                Call("ApplyMatchState", round, MatchPhase.Playing, 2, 0);
            }

            void BombEvent(string method, int round, params int[] fields)
            {
                using var writer = new Packet();
                writer.Write(7);
                writer.Write(round);
                foreach (int field in fields) writer.Write(field);
                using var packet = new Packet(writer.ToArray());
                handleType.GetMethod(method).Invoke(null, new object[] { packet });
            }
        }
        finally
        {
            try
            {
                if (gameSingleton.GetValue(null) is Component manager && manager && manager.gameObject == managerObject)
                    gameType.GetMethod("OnDestroy", Instance).Invoke(manager, null);
            }
            finally
            {
                gameSingleton.SetValue(null, previousGame);
                clientSingleton.SetValue(null, previousClient);
                playerField.SetValue(null, previousPlayers);
                softBlockField.SetValue(null, previousSoftBlocks);
                Time.timeScale = oldTimeScale;
                Object.Destroy(fixture);
            }
        }
    }

    private static Type FindType(string name) => Type.GetType(name + ", Assembly-CSharp", throwOnError: true);
}
