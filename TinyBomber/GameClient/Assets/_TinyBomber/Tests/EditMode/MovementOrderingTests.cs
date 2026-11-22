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
    public void CorrectionOrdersAgainstEarlierCorrectionsEvenAfterNewerPredictionWasSent()
    {
        var ordering = new MovementOrdering();
        for (long sequence = 1; sequence <= 14; sequence++)
            Assert.That(ordering.TrySend(next => next == sequence), Is.True);
        Assert.That(ordering.TryAcceptCorrection(12, 11), Is.True);
        Assert.That(ordering.TryAcceptCorrection(12, 11), Is.False);
        Assert.That(ordering.TryAcceptCorrection(11, 10), Is.False);
        Assert.That(ordering.TryAcceptCorrection(13, 13), Is.False);
        Assert.That(ordering.TryAcceptCorrection(13, 12), Is.True);
        ordering.Reset();
        Assert.That(ordering.TryAcceptCorrection(1, 0), Is.True);
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

    [Test]
    public void CorrectionSnapsEvenAfterNewerPredictionAndDuplicateDoesNotSnapAgain()
    {
        var local = NewObject();
        players.Add(1, local);
        Invoke("ApplyMatchState", 1, MatchPhase.Playing, 2, 0);
        var ordering = (MovementOrdering)gameType.GetField("movementOrdering", Instance).GetValue(game);
        for (int i = 0; i < 14; i++) ordering.TrySend(_ => true);
        local.transform.position = Vector3.right;
        Invoke("ApplyOrderedCorrection", 7, 1, 1, 12L, 11L, Vector2.zero);
        Assert.That(local.transform.position, Is.EqualTo(Vector3.zero));
        local.transform.position = Vector3.up;
        Invoke("ApplyOrderedCorrection", 7, 1, 1, 12L, 11L, Vector2.zero);
        Assert.That(local.transform.position, Is.EqualTo(Vector3.up));
    }

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
