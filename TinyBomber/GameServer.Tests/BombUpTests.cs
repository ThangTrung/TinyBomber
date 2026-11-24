using System.Diagnostics;
using GameShared;

namespace GameServer.Tests;

public sealed class BombUpTests
{
    internal sealed class Roll(int value) : Random
    {
        internal int Calls { get; private set; }
        public override int Next(int maxValue)
        {
            Assert.Equal(100, maxValue);
            Calls++;
            return value;
        }
    }

    private readonly GameLogic room;
    private readonly Client low = new(1) { Username = "Low", UdpConfirmed = true };
    private readonly Client high = new(2) { Username = "High", UdpConfirmed = true };
    private readonly Client third = new(3) { Username = "Third", UdpConfirmed = true };
    private readonly Roll roll = new(24);

    public BombUpTests()
    {
        room = new GameLogic(1, itemRandom: roll);
        foreach (var slot in new[] { high, low, third }) room.TryAddMember(slot);
        room.StartRound(low, 1, 0);
        Array.Clear(room.mapData);
    }

    private static void Put(Client slot, int col, int row) =>
        (slot.player!.x, slot.player.y) = GameLogic.GridToWorld(col, row);

    [Theory]
    [InlineData("eligible")]
    [InlineData("full")]
    [InlineData("dead")]
    [InlineData("left")]
    public void StationaryContestUsesLowestEligibleSlot(string status)
    {
        Put(high, 5, 5);
        Put(low, 5, 5);
        var a = low.player!;
        if (status == "full") a.maxActiveBombs = 3;
        if (status == "dead") a.isAlive = false;
        if (status == "left") room.RemoveMember(low);
        room.BombUps.Add((5, 5));
        room.UpdateRound(0);
        room.UpdateRound(1);
        Assert.Empty(room.BombUps);
        Assert.Equal(status is "eligible" or "full" ? 1 : 2, high.player!.maxActiveBombs);
        Assert.Equal(status == "eligible" ? 2 : status == "full" ? 3 : 1, a.maxActiveBombs);
    }

    [Fact]
    public void FullPlayerConsumesSuccessiveItemsWithoutIncreasingCapacity()
    {
        var player = low.player!;
        player.maxActiveBombs = ProtocolV5.MaximumCapacity;
        for (int col = 5; col <= 7; col++)
        {
            Put(low, col, 5);
            room.BombUps.Add((col, 5));
            room.UpdateRound(0);
            Assert.Empty(room.BombUps);
            Assert.Equal(ProtocolV5.MaximumCapacity, player.maxActiveBombs);
        }
    }

    [Fact]
    public void SlotsFreedByExplosionAndCapacityResetOnReplay()
    {
        var player = low.player!;
        Assert.Equal(1, player.maxActiveBombs);
        for (int cap = 1; cap <= 3; cap++)
        {
            Put(low, cap, 3);
            room.PlaceBomb(low, 1, 1, cap);
            room.PlaceBomb(low, 1, 1, cap);
            Assert.Equal(cap, room.ActiveBombCount(player));
            Put(low, 10, 3);
            room.PlaceBomb(low, 1, 1, cap);
            Assert.Equal(cap, room.ActiveBombCount(player));
            room.BombUps.Add((10, 3));
            room.UpdateRound(0);
            Assert.Equal(Math.Min(3, cap + 1), player.maxActiveBombs);
            Assert.Empty(room.BombUps);
        }
        room.UpdateRound(2 * Stopwatch.Frequency + 1);
        Assert.Equal(2, room.ActiveBombCount(player));
        room.PlaceBomb(low, 1, 1);
        Assert.Equal(3, room.ActiveBombCount(player));
        room.RemoveMember(third);
        room.RemoveMember(high);
        room.UpdateRound(0);
        Assert.Empty(room.BombUps);
        room.TryAddMember(high);
        room.StartRound(low, 1, 1);
        Assert.Equal(1, low.player!.maxActiveBombs);
    }

    [Fact]
    public void SameCellAndRoundInTwoRoomsStayIndependentThroughSlotReuse()
    {
        var other = new GameLogic(2);
        var a = new Client(4) { Username = "A", UdpConfirmed = true };
        var b = new Client(5) { Username = "B", UdpConfirmed = true };
        other.TryAddMember(a);
        other.TryAddMember(b);
        other.StartRound(a, 2, 0);
        room.BombUps.Add((5, 5));
        other.BombUps.Add((5, 5));
        Put(low, 5, 5);
        room.UpdateRound(0);
        Assert.Equal(2, low.player!.maxActiveBombs);
        room.RemoveMember(low);
        var replacement = new Client(low.id) { Username = "Replacement", UdpConfirmed = true };
        Assert.True(other.TryAddMember(replacement));
        Assert.Null(replacement.player);
        Assert.Contains((5, 5), other.BombUps);
        Assert.Equal(1, a.player!.maxActiveBombs);
        other.RemoveMember(b);
        other.UpdateRound(0);
        other.StartRound(a, 2, 1);
        Assert.Empty(other.BombUps);
        Assert.Equal(1, replacement.player!.maxActiveBombs);
        Assert.False(other.AcceptsInput(low, 2, 2));
    }

    [Fact]
    public void RetryOfAcceptedMoveDoesNotAwardAnotherCapacity()
    {
        var player = low.player!;
        var target = GameLogic.GridToWorld(5, 5);
        player.x = target.x - 0.51f;
        player.y = target.y;
        low.tcp.AcceptedProtocolVersion = ProtocolV5.Version;
        low.tcp.SessionToken = ProtocolV2.NewSessionToken();
        room.BombUps.Add((5, 5));
        float x = player.x + 0.05f;
        for (int i = 0; i < 2; i++)
        {
            ServerHandle.PlayerMovement(low, 1, 1, x, target.y, now: 0, moveSeq: 1);
            room.UpdateRound(0);
        }
        Assert.Equal(1, player.LastAcceptedMoveSeq);
        Assert.Equal(2, player.maxActiveBombs);
        Assert.Empty(room.BombUps);
    }

    [Fact]
    public void FinishedSkipsRollAndPickup()
    {
        Put(low, 4, 4);
        Put(high, 4, 5);
        Put(third, 9, 9);
        room.mapData[5, 4] = 2;
        room.PlaceBomb(low, 1, 1);
        room.BombUps.Add((9, 9));
        room.UpdateRound(long.MaxValue);
        Assert.Equal(MatchPhase.Finished, room.Phase);
        Assert.Equal(0, roll.Calls);
        Assert.Empty(room.BombUps);
        Assert.Equal(1, third.player!.maxActiveBombs);
    }

    [Fact]
    public void DuplicatePlacementOnOccupiedCellUsesOnlyOneSlot()
    {
        low.player!.maxActiveBombs = 3;
        room.PlaceBomb(low, 1, 1);
        room.PlaceBomb(low, 1, 1);
        Assert.Equal(1, room.ActiveBombCount(low.player));
    }

    [Fact]
    public void RejectedMovementAndRetryDoNotClaimTargetCell()
    {
        Put(low, 4, 4);
        room.BombUps.Add((5, 4));
        low.tcp.AcceptedProtocolVersion = ProtocolV5.Version;
        low.tcp.SessionToken = ProtocolV2.NewSessionToken();
        var (x, y) = GameLogic.GridToWorld(5, 4);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            ServerHandle.PlayerMovement(low, 1, 1, x, y, now: 0, moveSeq: 1);
            room.UpdateRound(0);
        }
        Assert.Contains((5, 4), room.BombUps);
        Assert.Equal(1, low.player!.maxActiveBombs);
    }

    [Theory]
    [InlineData(24, true)]
    [InlineData(25, false)]
    public void TwoBlastsRollOnceOnlyForDestroyedSoft(int value, bool drop)
    {
        var random = new Roll(value);
        var other = new GameLogic(2, itemRandom: random);
        room.RemoveMember(low);
        room.RemoveMember(high);
        other.TryAddMember(low);
        other.TryAddMember(high);
        other.StartRound(low, 2, 0);
        Array.Clear(other.mapData);
        Put(low, 4, 5);
        Put(high, 6, 5);
        other.mapData[5, 5] = 2;
        other.mapData[4, 6] = 1;
        other.BombUps.Add((4, 4));
        other.PlaceBomb(low, 2, 1);
        other.PlaceBomb(high, 2, 1);
        Put(low, 2, 2);
        Put(high, 9, 9);
        other.UpdateRound(long.MaxValue);
        Assert.Equal(1, random.Calls);
        Assert.Equal(drop, other.BombUps.Contains((5, 5)));
        Assert.Contains((4, 4), other.BombUps);
        Assert.Empty(room.BombUps);
    }
}
