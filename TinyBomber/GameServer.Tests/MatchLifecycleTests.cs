using GameShared;
using Xunit;

namespace GameServer.Tests;

public sealed class MatchLifecycleTests : IDisposable
{
    private readonly Client first = new(1) { Username = "First", UdpConfirmed = true };
    private readonly Client second = new(2) { Username = "Second", UdpConfirmed = true };

    public MatchLifecycleTests()
    {
        Server.clients.Add(first.id, first);
        Server.clients.Add(second.id, second);
    }

    public void Dispose()
    {
        Server.clients.Clear();
        GameLogic.UpdateRound(long.MaxValue);
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
        GameLogic.mapData[3, 1] = 0;
        GameLogic.PlaceBomb(a);
        GameLogic.PlaceBomb(b);

        GameLogic.UpdateRound(long.MaxValue);

        Assert.Equal(0, a.hp);
        Assert.Equal(0, b.hp);
        Assert.False(a.isAlive);
        Assert.False(b.isAlive);
        Assert.Equal(MatchPhase.Finished, GameLogic.Phase);
        Assert.Equal(0, GameLogic.WinnerId);
        GameLogic.UpdateRound(long.MaxValue);
        Assert.Equal(0, GameLogic.WinnerId);
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
        GameLogic.mapData[3, 1] = block;
        GameLogic.PlaceBomb(a);

        GameLogic.UpdateRound(long.MaxValue);

        Assert.False(a.isAlive);
        Assert.True(b.isAlive);
        Assert.False(GameLogic.AcceptsInput(a, GameLogic.RoundId));
        Assert.Equal(block == 1 ? 1 : 0, GameLogic.mapData[3, 1]);
        Assert.Equal(MatchPhase.Finished, GameLogic.Phase);
        Assert.Equal(b.id, GameLogic.WinnerId);
    }

    [Fact]
    public void OwnerDisconnectAndDueBombInSameTick_EndInDraw()
    {
        StartRound();
        Put(first.player!, 2, 1);
        Put(second.player!, 3, 1);
        GameLogic.PlaceBomb(first.player!);

        Server.Disconnect(first.id, first.tcp);
        Assert.Equal(MatchPhase.Playing, GameLogic.Phase);
        GameLogic.UpdateRound(long.MaxValue);

        Assert.False(second.player!.isAlive);
        Assert.Equal(MatchPhase.Finished, GameLogic.Phase);
        Assert.Equal(0, GameLogic.WinnerId);
    }

    [Fact]
    public void ReplayUsesNewPlayersAndRejectsOldRound()
    {
        StartRound();
        int previousRound = GameLogic.RoundId;
        Player previousPlayer = first.player!;
        GameLogic.PlaceBomb(previousPlayer);
        GameLogic.UpdateRound(long.MaxValue);
        Assert.False(previousPlayer.isAlive);

        GameLogic.StartRound(second, previousRound);
        GameLogic.StartRound(first, previousRound);

        Assert.Equal(previousRound + 1, GameLogic.RoundId);
        Assert.NotSame(previousPlayer, first.player);
        Assert.Equal(1, first.player!.hp);
        Assert.False(GameLogic.AcceptsInput(first.player, previousRound));
        Assert.True(GameLogic.AcceptsInput(first.player, GameLogic.RoundId));
        GameLogic.UpdateRound(long.MaxValue);
        Assert.Equal(MatchPhase.Playing, GameLogic.Phase);
    }

    [Fact]
    public void TcpOnlyClientIsNotCountedAndLateClientWaitsForReplay()
    {
        int previousRound = GameLogic.RoundId;
        second.UdpConfirmed = false;
        GameLogic.StartRound(first, previousRound);
        Assert.Equal(previousRound, GameLogic.RoundId);
        Assert.Null(second.player);

        second.UdpConfirmed = true;
        StartRound();
        var late = new Client(3) { Username = "Late", UdpConfirmed = true };
        Server.clients.Add(late.id, late);
        Assert.Null(late.player);

        GameLogic.PlaceBomb(first.player!);
        GameLogic.UpdateRound(long.MaxValue);
        GameLogic.StartRound(first, GameLogic.RoundId);
        Assert.NotNull(late.player);
    }

    private void StartRound() => GameLogic.StartRound(first, GameLogic.RoundId);

    private static void Put(Player player, int col, int row)
    {
        GameLogic.mapData[col, row] = 0;
        (player.x, player.y) = GameLogic.GridToWorld(col, row);
    }
}
