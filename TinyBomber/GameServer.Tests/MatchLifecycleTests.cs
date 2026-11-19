using GameShared;
using Xunit;

namespace GameServer.Tests;

public sealed class MatchLifecycleTests
{
    private readonly GameLogic room = new();
    private readonly Client first = new(1) { Username = "First", UdpConfirmed = true };
    private readonly Client second = new(2) { Username = "Second", UdpConfirmed = true };

    public MatchLifecycleTests()
    {
        Assert.True(room.TryAddMember(first));
        Assert.True(room.TryAddMember(second));
    }

    [Fact]
    public void TwoBombsDueInOneTick_EndInDraw()
    {
        StartRound();
        var a = first.player!;
        var b = second.player!;
        b.blastRange = 3;
        Put(a, 2, 1);
        Put(b, 4, 1);
        room.mapData[3, 1] = 0;
        room.PlaceBomb(first, room.RoundId);
        room.PlaceBomb(second, room.RoundId);

        room.UpdateRound(long.MaxValue);

        Assert.Equal(0, a.hp);
        Assert.Equal(0, b.hp);
        Assert.False(a.isAlive);
        Assert.False(b.isAlive);
        Assert.Equal(MatchPhase.Finished, room.Phase);
        Assert.Equal(0, room.WinnerId);
        room.UpdateRound(long.MaxValue);
        Assert.Equal(0, room.WinnerId);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void BlockProtectsPlayerBehindIt(int block)
    {
        StartRound();
        var a = first.player!;
        var b = second.player!;
        a.blastRange = 3;
        Put(a, 2, 1);
        Put(b, 4, 1);
        room.mapData[3, 1] = block;
        room.PlaceBomb(first, room.RoundId);

        room.UpdateRound(long.MaxValue);

        Assert.False(a.isAlive);
        Assert.True(b.isAlive);
        Assert.False(room.AcceptsInput(first, room.RoundId));
        Assert.Equal(block == 1 ? 1 : 0, room.mapData[3, 1]);
        Assert.Equal(MatchPhase.Finished, room.Phase);
        Assert.Equal(b.id, room.WinnerId);
    }

    [Fact]
    public void OwnerDisconnectAndDueBombInSameTick_EndInDraw()
    {
        StartRound();
        Put(first.player!, 2, 1);
        Put(second.player!, 3, 1);
        room.PlaceBomb(first, room.RoundId);

        room.RemoveMember(first);
        Assert.Equal(MatchPhase.Playing, room.Phase);
        room.UpdateRound(long.MaxValue);

        Assert.False(second.player!.isAlive);
        Assert.Equal(MatchPhase.Finished, room.Phase);
        Assert.Equal(0, room.WinnerId);
    }

    [Fact]
    public void ReplayUsesNewPlayersAndRejectsOldRound()
    {
        StartRound();
        int previousRound = room.RoundId;
        Player previousPlayer = first.player!;
        room.PlaceBomb(first, previousRound);
        room.UpdateRound(long.MaxValue);
        Assert.False(previousPlayer.isAlive);

        room.StartRound(second, previousRound);
        room.StartRound(first, previousRound);

        Assert.Equal(previousRound + 1, room.RoundId);
        Assert.NotSame(previousPlayer, first.player);
        Assert.Equal(1, first.player!.hp);
        Assert.False(room.AcceptsInput(first, previousRound));
        Assert.True(room.AcceptsInput(first, room.RoundId));
        room.UpdateRound(long.MaxValue);
        Assert.Equal(MatchPhase.Playing, room.Phase);
    }

    [Fact]
    public void TcpOnlyClientIsNotCountedAndLateClientWaitsForReplay()
    {
        int previousRound = room.RoundId;
        second.UdpConfirmed = false;
        room.StartRound(first, previousRound);
        Assert.Equal(previousRound, room.RoundId);
        Assert.Null(second.player);

        second.UdpConfirmed = true;
        StartRound();
        var late = new Client(3) { Username = "Late", UdpConfirmed = true };
        Assert.True(room.TryAddMember(late));
        Assert.Null(late.player);

        room.PlaceBomb(first, room.RoundId);
        room.UpdateRound(long.MaxValue);
        room.StartRound(first, room.RoundId);
        Assert.NotNull(late.player);
    }

    [Fact]
    public void StartingAndReplayingOneRoomKeepsTheOtherMatchIntact()
    {
        var otherRoom = new GameLogic();
        var third = new Client(3) { Username = "Third", UdpConfirmed = true };
        var fourth = new Client(4) { Username = "Fourth", UdpConfirmed = true };
        Assert.True(otherRoom.TryAddMember(third));
        Assert.True(otherRoom.TryAddMember(fourth));
        otherRoom.StartRound(third, 0);

        Player otherPlayer = third.player!;
        Player otherBombOwner = fourth.player!;
        int otherRoundId = otherRoom.RoundId;
        var otherMap = (int[,])otherRoom.mapData.Clone();
        otherRoom.PlaceBomb(fourth, otherRoundId);

        StartRound();
        Assert.Equal(otherRoundId, room.RoundId);
        Assert.False(room.AcceptsInput(third, room.RoundId));
        Assert.False(otherRoom.AcceptsInput(first, otherRoundId));
        room.PlaceBomb(first, room.RoundId);
        room.UpdateRound(long.MaxValue);
        Assert.Equal(second.id, room.WinnerId);

        room.StartRound(second, room.RoundId);
        Assert.Equal(MatchPhase.Playing, room.Phase);
        Assert.Equal(MatchPhase.Playing, otherRoom.Phase);
        Assert.Equal(otherRoundId, otherRoom.RoundId);
        Assert.Equal(0, otherRoom.WinnerId);
        Assert.Same(otherPlayer, third.player);
        Assert.Same(otherBombOwner, fourth.player);
        Assert.Equal(otherMap.Cast<int>(), otherRoom.mapData.Cast<int>());

        var (x, y) = GameLogic.GridToWorld(2, 13);
        ServerHandle.PlayerMovement(third, otherRoundId, x, y);
        Assert.Equal((x, y), (otherPlayer.x, otherPlayer.y));

        otherRoom.UpdateRound(long.MaxValue);
        Assert.Equal(MatchPhase.Finished, otherRoom.Phase);
        Assert.Equal(third.id, otherRoom.WinnerId);
        Assert.Equal(MatchPhase.Playing, room.Phase);
    }

    [Fact]
    public void MembershipIsExclusiveAndCapacityIsPerRoom()
    {
        var otherRoom = new GameLogic();
        Assert.False(otherRoom.TryAddMember(first));

        for (int id = 3; id <= Constants.MAX_PLAYERS; id++)
            Assert.True(room.TryAddMember(new Client(id)));

        var next = new Client(Constants.MAX_PLAYERS + 1);
        Assert.False(room.TryAddMember(next));
        Assert.True(otherRoom.TryAddMember(next));
    }

    private void StartRound() => room.StartRound(first, room.RoundId);

    private void Put(Player player, int col, int row)
    {
        room.mapData[col, row] = 0;
        (player.x, player.y) = GameLogic.GridToWorld(col, row);
    }
}
