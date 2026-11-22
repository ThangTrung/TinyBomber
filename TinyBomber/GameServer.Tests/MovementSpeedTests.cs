using System.Diagnostics;
using System.Net;
using GameShared;

namespace GameServer.Tests;

[Collection("Network metrics server state")]
public sealed class MovementSpeedTests
{
    private readonly GameLogic room = new(1);
    private readonly Client mover = new(1) { Username = "Mover", UdpConfirmed = true };
    private readonly Client peer = new(2) { Username = "Peer", UdpConfirmed = true };

    public MovementSpeedTests()
    {
        Assert.True(room.TryAddMember(mover));
        Assert.True(room.TryAddMember(peer));
        room.StartRound(mover, room.Id, 0, now: 0);
        Assert.Equal(MatchPhase.Playing, room.Phase);
        Array.Clear(room.mapData);
        (mover.player!.x, mover.player.y) = (0f, 0f);
    }

    [Theory]
    [InlineData(0.08f, 0f)]
    [InlineData(0.05656854f, 0.05656854f)]
    [InlineData(0f, 0.06f)]
    public void SustainedUnityStepsAreAcceptedAtTwentyMillisecondIntervals(float dx, float dy)
    {
        (float x, float y) position = (-7.5f, 5.5f);
        (mover.player!.x, mover.player.y) = position;
        for (int i = 0; i < 1000; i++)
        {
            int sign = (i / 8) % 2 == 0 ? 1 : -1;
            position = (position.x + sign * dx, position.y + sign * dy);
            Assert.Equal(position, Move(position.x, position.y, 20 * i));
        }
    }

    [Theory]
    [InlineData(40, true)]
    [InlineData(80, true)]
    [InlineData(100, false)]
    [InlineData(200, false)]
    public void ContinuousMovementAfterMissingPacketsIsLimitedByCreditCap(int gapMs, bool accepted)
    {
        const float initialX = 0.08f;
        Assert.Equal((initialX, 0f), Move(initialX, 0f, 0));
        float targetX = (float)(initialX + 4d * gapMs / 1000d);
        Assert.True(room.CanMove(mover.player!, targetX, 0f));

        Assert.Equal((accepted ? targetX : initialX, 0f), Move(targetX, 0f, gapMs));
    }

    [Fact]
    public void SchedulerJitterCanDeliverSeveralStepsTogether()
    {
        Assert.Equal((0.08f, 0f), Move(0.08f, 0f, 0));
        Assert.Equal((0.16f, 0f), Move(0.16f, 0f, 47));
        Assert.Equal((0.24f, 0f), Move(0.24f, 0f, 47));
        Assert.Equal((0.32f, 0f), Move(0.32f, 0f, 103));
        Assert.Equal((0.40f, 0f), Move(0.40f, 0f, 103));
        Assert.Equal((0.48f, 0f), Move(0.48f, 0f, 103));
        Assert.Equal((0.56f, 0f), Move(0.56f, 0f, 121));
    }

    [Theory]
    [InlineData(0.320005f, true)]
    [InlineData(0.32002f, false)]
    public void LongIdleRespectsTheEffectiveCreditCap(float targetX, bool accepted)
    {
        Assert.Equal((accepted ? targetX : 0f, 0f), Move(targetX, 0f, 60_000));
    }

    [Fact]
    [Trait("Category", "ActionQueueIntegration")]
    public void QueuedUdpAndTcpMovementSpendOneBudgetInTheSameBatch()
    {
        var endpoint = new IPEndPoint(IPAddress.Loopback, 30001);
        byte[] token = ProtocolV2.NewSessionToken();
        mover.udp.endPoint = endpoint;
        mover.tcp.SessionToken = token;
        long appliedAt = At(100);
        var observed = new List<float>();

        ThreadManager.Initialize();
        ThreadManager.ResetStats();
        try
        {
            EnqueueMovement(0.08f, udp: true);
            EnqueueMovement(0f, udp: false);
            EnqueueMovement(0.08f, udp: true);
            EnqueueMovement(0f, udp: false);
            EnqueueMovement(0.08f, udp: true);

            ThreadManager.UpdateMain();

            Assert.Equal(new[] { 0.08f, 0f, 0.08f, 0f, 0f }, observed);
        }
        finally
        {
            ThreadManager.ResetStats();
        }

        void EnqueueMovement(float x, bool udp)
        {
            ThreadManager.EnqueueAction(() =>
            {
                if (udp)
                    ServerHandle.UdpMovement(mover, mover.tcp, mover.udp, endpoint,
                        token, room.Id, room.RoundId, x, 0f, appliedAt);
                else
                    ServerHandle.PlayerMovement(mover, room.Id, room.RoundId, x, 0f, appliedAt);
                observed.Add(mover.player!.x);
            });
        }
    }

    [Fact]
    public void RejectionsAndAnOlderTimestampCannotRefillTheSameIntervalTwice()
    {
        for (int i = 0; i < 5; i++)
            Assert.Equal((0f, 0f), Move(0.20f, 0f, 20));

        Assert.Equal((0.16f, 0f), Move(0.16f, 0f, 20));
        Assert.Equal((0.16f, 0f), Move(0.15f, 0f, 0));
        Assert.Equal((0.16f, 0f), Move(0.15f, 0f, 20));
        Assert.Equal((0.15f, 0f), Move(0.15f, 0f, 23));
    }

    [Fact]
    public void RepeatedTinyStepsCannotReuseTheNumericalMargin()
    {
        Assert.Equal((0.08f, 0f), Move(0.08f, 0f, 0));
        float x = 0.08f;
        for (int i = 0; i < 100; i++)
            x = Move(x + 0.000001f, 0f, 0).X;

        Assert.InRange(x, 0.080008f, 0.08001f);
    }

    [Fact]
    public void CollidingProposalDoesNotSpendCreditAndOtherPlayersHaveTheirOwnBudget()
    {
        room.mapData[9, 8] = 1;
        (mover.player!.x, mover.player.y) = (-0.32f, 0.5f);
        (peer.player!.x, peer.player.y) = (-0.32f, 0.5f);
        Assert.Equal((-0.32f, 0.5f), Move(-0.24f, 0.5f, 0));
        Assert.Equal((-0.40f, 0.5f), Move(-0.40f, 0.5f, 0));

        ServerHandle.PlayerMovement(peer, room.Id, room.RoundId, -0.40f, 0.5f, now: 0);
        Assert.Equal((-0.40f, 0.5f), (peer.player.x, peer.player.y));
    }

    [Fact]
    public void SessionRoundAndAliveGuardsRunBeforeSpendingCredit()
    {
        var endpoint = new IPEndPoint(IPAddress.Loopback, 30001);
        mover.udp.endPoint = endpoint;
        mover.tcp.SessionToken = ProtocolV2.NewSessionToken();
        ServerHandle.UdpMovement(mover, mover.tcp, mover.udp, endpoint, ProtocolV2.NewSessionToken(),
            room.Id, room.RoundId, 0.08f, 0f, now: 0);
        ServerHandle.PlayerMovement(mover, room.Id + 1, room.RoundId, 0.08f, 0f, now: 0);
        ServerHandle.PlayerMovement(mover, room.Id, room.RoundId + 1, 0.08f, 0f, now: 0);
        mover.player!.isAlive = false;
        ServerHandle.PlayerMovement(mover, room.Id, room.RoundId, 0.08f, 0f, now: 0);
        Assert.Equal((0f, 0f), (mover.player.x, mover.player.y));

        mover.player.isAlive = true;
        Assert.Equal((0.08f, 0f), Move(0.08f, 0f, 0));
    }

    [Fact]
    public void ReplayReinitializesCreditAtTheNewSpawnTimestamp()
    {
        Player original = mover.player!;
        Assert.Equal((0f, 0f), Move(0f, 0f, 1000));
        peer.player!.isAlive = false;
        room.UpdateRound(long.MaxValue);
        Assert.Equal(MatchPhase.Finished, room.Phase);
        room.StartRound(mover, room.Id, room.RoundId, At(1000));
        Assert.NotSame(original, mover.player);

        AssertHasOnlyInitialCredit(room, mover);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReusingASlotAfterLeavingOrDisconnectingDoesNotCarryCredit(bool disconnect)
    {
        Player original = mover.player!;
        Assert.Equal((0f, 0f), Move(0f, 0f, 1000));
        if (disconnect)
        {
            var originalTcp = mover.tcp;
            bool replaced = Server.clients.TryGetValue(mover.id, out Client? saved);
            Server.clients[mover.id] = mover;
            try
            {
                Server.Disconnect(mover.id, originalTcp);
                Assert.NotSame(originalTcp, mover.tcp);
            }
            finally
            {
                if (replaced) Server.clients[mover.id] = saved!;
                else Server.clients.Remove(mover.id);
            }
            mover.Username = "Reconnected";
            mover.UdpConfirmed = true;
        }
        else
        {
            room.RemoveMember(mover);
        }

        Assert.Null(mover.player);
        var nextRoom = new GameLogic(2);
        Assert.True(nextRoom.TryAddMember(mover));
        Assert.True(nextRoom.TryAddMember(new Client(3) { Username = "Another", UdpConfirmed = true }));
        nextRoom.StartRound(mover, nextRoom.Id, 0, At(1000));
        Assert.NotSame(original, mover.player);

        AssertHasOnlyInitialCredit(nextRoom, mover);
    }

    private (float X, float Y) Move(float x, float y, int milliseconds)
    {
        ServerHandle.PlayerMovement(mover, room.Id, room.RoundId, x, y, At(milliseconds));
        return (mover.player!.x, mover.player.y);
    }

    private static long At(int milliseconds) => Stopwatch.Frequency * milliseconds / 1000;

    private static void AssertHasOnlyInitialCredit(GameLogic currentRoom, Client current)
    {
        Player player = Assert.IsType<Player>(current.player);
        var spawn = (player.x, player.y);
        ServerHandle.PlayerMovement(current, currentRoom.Id, currentRoom.RoundId, spawn.x + 0.125f, spawn.y, At(1000));
        Assert.Equal(spawn, (player.x, player.y));
        ServerHandle.PlayerMovement(current, currentRoom.Id, currentRoom.RoundId, spawn.x + 0.08f, spawn.y, At(1000));
        Assert.Equal((spawn.x + 0.08f, spawn.y), (player.x, player.y));
    }
}
