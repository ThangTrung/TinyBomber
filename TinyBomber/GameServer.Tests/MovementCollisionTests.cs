using System.Diagnostics;
using GameShared;

namespace GameServer.Tests;

public sealed class MovementCollisionTests
{
    private readonly GameLogic room = new(1);
    private readonly Client mover = new(1) { Username = "Mover", UdpConfirmed = true };
    private readonly Client peer = new(2) { Username = "Peer", UdpConfirmed = true };
    private long now;

    public MovementCollisionTests()
    {
        Assert.True(room.TryAddMember(mover));
        Assert.True(room.TryAddMember(peer));
        room.StartRound(mover, room.Id, 0, now);
        Assert.Equal(MatchPhase.Playing, room.Phase);
        Array.Clear(room.mapData);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void MovementAcrossATerrainBlockKeepsTheAuthoritativePosition(int block)
    {
        room.mapData[9, 8] = block;
        var start = (X: -0.5f, Y: 0.5f);
        var target = (X: 1.5f, Y: 0.5f);
        (mover.player!.x, mover.player.y) = start;

        Assert.True(TerrainCollision.CanStandAt(room.mapData, start.X, start.Y));
        Assert.True(TerrainCollision.CanStandAt(room.mapData, target.X, target.Y));
        Assert.False(room.CanMove(mover.player, target.X, target.Y));
        MoveAndExpect(mover, target, start);
    }

    [Fact]
    public void MovementCanCrossASoftBlockAfterTheExplosionDestroysIt()
    {
        room.mapData[9, 8] = 2;
        var start = (X: -0.5f, Y: 0.5f);
        var target = (X: 1.5f, Y: 0.5f);
        (mover.player!.x, mover.player.y) = start;
        Assert.False(room.CanMove(mover.player, target.X, target.Y));
        MoveAndExpect(mover, target, start);

        // Place the bomb above the soft block and move the owner beyond its blast.
        (peer.player!.x, peer.player.y) = (0.5f, 1.5f);
        room.PlaceBomb(peer, room.Id, room.RoundId);
        MoveAcrossOpenRow(peer, 2.5f);
        room.UpdateRound(long.MaxValue);

        Assert.Equal(0, room.mapData[9, 8]);
        Assert.Equal(MatchPhase.Playing, room.Phase);
        Assert.True(room.CanMove(mover.player, target.X, target.Y));
        MoveAcrossOpenRow(mover, target.X);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CornerMovementRequiresTheIntermediatePosition(bool receiveMiddle)
    {
        room.mapData[2, 2] = 1;
        var a = (X: -7.32f, Y: -6.22f);
        var b = (X: -7.32f, Y: -6.32f);
        var c = (X: -7.22f, Y: -6.32f);
        (mover.player!.x, mover.player.y) = a;

        if (receiveMiddle) MoveAndExpect(mover, b, b);
        MoveAndExpect(mover, c, receiveMiddle ? c : a);
    }

    [Fact]
    public void DiagonalStepCuttingTheCornerIsRejectedButTheSlideIsAccepted()
    {
        room.mapData[2, 2] = 1;
        const float diagonal = 0.1f / 1.41421356f;
        var start = (X: -7.32f, Y: -6.25f);
        var target = (X: start.X + diagonal, Y: start.Y - diagonal);
        var alongY = (X: start.X, target.Y);
        (mover.player!.x, mover.player.y) = start;

        Assert.True(TerrainCollision.CanStandAt(room.mapData, target.X, target.Y));
        MoveAndExpect(mover, target, start);
        MoveAndExpect(mover, (target.X, start.Y), start);
        MoveAndExpect(mover, alongY, alongY);
    }

    [Fact]
    public void BombAllowsEscapeAndBlocksEntryAndReentry()
    {
        var bomb = (X: 0.5f, Y: 0.5f);
        var left = (X: -0.5f, Y: 0.5f);
        var right = (X: 1.5f, Y: 0.5f);
        (mover.player!.x, mover.player.y) = bomb;
        (peer.player!.x, peer.player.y) = left;
        room.PlaceBomb(mover, room.Id, room.RoundId);

        Assert.False(room.CanMove(peer.player, bomb.X, bomb.Y));
        Assert.False(room.CanMove(peer.player, right.X, right.Y));
        MoveAndExpect(peer, (-0.25f, bomb.Y), left);
        Assert.True(room.CanMove(mover.player, right.X, right.Y));
        MoveAcrossOpenRow(mover, right.X);
        Assert.False(room.CanMove(mover.player, bomb.X, bomb.Y));
        MoveAndExpect(mover, (1.25f, bomb.Y), right);
    }

    [Theory]
    [InlineData(float.NaN, 0f)]
    [InlineData(0f, float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity, 0f)]
    [InlineData(float.MaxValue, 0f)]
    [InlineData(0f, -100f)]
    public void InvalidTargetDoesNotChangeTheAuthoritativePosition(float x, float y)
    {
        var start = (X: 0.5f, Y: 0.5f);
        (mover.player!.x, mover.player.y) = start;

        MoveAndExpect(mover, (x, y), start);
    }

    private void MoveAndExpect(Client slot, (float X, float Y) target, (float X, float Y) expected)
    {
        now += Stopwatch.Frequency / 10;
        ServerHandle.PlayerMovement(slot, room.Id, room.RoundId, target.X, target.Y, now);

        Assert.Equal(expected, (slot.player!.x, slot.player.y));
    }

    // Advance by an affordable step each time so the handler still checks the actual path.
    private void MoveAcrossOpenRow(Client slot, float destinationX)
    {
        var player = slot.player!;
        for (float x = player.x + 0.25f; x <= destinationX; x += 0.25f)
            MoveAndExpect(slot, (x, player.y), (x, player.y));

        Assert.Equal(destinationX, player.x);
    }
}
