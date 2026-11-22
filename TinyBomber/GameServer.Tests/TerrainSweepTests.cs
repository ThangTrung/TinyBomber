using GameShared;

namespace GameServer.Tests;

public sealed class TerrainSweepTests
{
    private const float Radius = BombCollision.PlayerRadius;

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void ClearEndpointsDoNotPermitSweepingThroughABlock(int block)
    {
        var map = NewMap();
        map[9, 8] = block;

        Assert.True(TerrainCollision.CanStandAt(map, -0.5f, 0.5f));
        Assert.True(TerrainCollision.CanStandAt(map, 1.5f, 0.5f));
        Assert.False(TerrainCollision.CanSweep(map, -0.5f, 0.5f, 1.5f, 0.5f));
        Assert.False(TerrainCollision.CanSweep(map, 1.5f, 0.5f, -0.5f, 0.5f));
    }

    [Fact]
    public void RemovingASoftBlockClearsTheSameSweep()
    {
        var map = NewMap();
        map[9, 8] = 2;
        Assert.False(TerrainCollision.CanSweep(map, -0.5f, 0.5f, 1.5f, 0.5f));

        map[9, 8] = 0;
        Assert.True(TerrainCollision.CanSweep(map, -0.5f, 0.5f, 1.5f, 0.5f));
    }

    [Fact]
    public void FootprintCornerHitsABlockThatTheCenterPassesBelow()
    {
        var map = NewMap();
        map[9, 8] = 1;

        Assert.True(TerrainCollision.CanStandAt(map, -0.5f, -0.2f));
        Assert.True(TerrainCollision.CanStandAt(map, 1.5f, -0.2f));
        Assert.False(TerrainCollision.CanSweep(map, -0.5f, -0.2f, 1.5f, -0.2f));
        Assert.True(TerrainCollision.CanSweep(map, -0.5f, -0.4f, 1.5f, -0.4f));
    }

    [Theory]
    [InlineData(9, -Radius, false)]
    [InlineData(8, Radius, true)]
    public void SweepAlongAnExactTileEdgeUsesTheSameSideAsCanStandAt(int col, float x, bool clear)
    {
        var map = NewMap();
        map[col, 8] = 1;

        Assert.Equal(clear, TerrainCollision.CanStandAt(map, x, 0.5f));
        Assert.Equal(clear, TerrainCollision.CanSweep(map, x, -0.5f, x, 1.5f));
        Assert.Equal(clear, TerrainCollision.CanSweep(map, x, 1.5f, x, -0.5f));
    }

    [Theory]
    [InlineData(9, 8, -Radius, false)]
    [InlineData(8, 7, Radius, true)]
    public void SweepTouchingOnlyOneCornerRespectsTheTileBoundary(int col, int row, float touch, bool clear)
    {
        var map = NewMap();
        map[col, row] = 1;
        const float offset = 0.125f;
        float low = touch - offset, high = touch + offset;

        Assert.True(TerrainCollision.CanStandAt(map, low, high));
        Assert.True(TerrainCollision.CanStandAt(map, high, low));
        Assert.Equal(clear, TerrainCollision.CanStandAt(map, touch, touch));
        Assert.Equal(clear, TerrainCollision.CanSweep(map, low, high, high, low));
        Assert.Equal(clear, TerrainCollision.CanSweep(map, high, low, low, high));
    }

    [Fact]
    public void SweepRejectsEitherEndpointInsideTerrain()
    {
        var map = NewMap();
        map[9, 8] = 1;

        Assert.False(TerrainCollision.CanSweep(map, 0.5f, 0.5f, 1.5f, 0.5f));
        Assert.False(TerrainCollision.CanSweep(map, 1.5f, 0.5f, 0.5f, 0.5f));
    }

    [Theory]
    [InlineData(float.NaN, 0f)]
    [InlineData(0f, float.NaN)]
    [InlineData(float.PositiveInfinity, 0f)]
    [InlineData(0f, float.NegativeInfinity)]
    [InlineData(float.MaxValue, 0f)]
    [InlineData(0f, float.MinValue)]
    [InlineData(-9.1f, 0f)]
    [InlineData(0f, 7.1f)]
    [InlineData(7.9f, 0f)]
    [InlineData(0f, -7.9f)]
    public void SweepRejectsInvalidCoordinatesAtEitherEnd(float x, float y)
    {
        var map = NewMap();

        Assert.False(TerrainCollision.CanSweep(map, x, y, 0f, 0f));
        Assert.False(TerrainCollision.CanSweep(map, 0f, 0f, x, y));
    }

    [Fact]
    public void SkippingTheMiddlePositionCutsTheBottomLeftSpawnCorner()
    {
        // Bottom-left of Tiny Bomber: (1,1), (1,2) and (2,1) are clear, (2,2) is hard terrain.
        var map = NewMap();
        map[2, 2] = 1;
        var a = (X: -7.32f, Y: -6.22f);
        var b = (X: -7.32f, Y: -6.32f);
        var c = (X: -7.22f, Y: -6.32f);

        Assert.True(TerrainCollision.CanStandAt(map, c.X, c.Y));
        Assert.True(TerrainCollision.CanSweep(map, a.X, a.Y, b.X, b.Y));
        Assert.True(TerrainCollision.CanSweep(map, b.X, b.Y, c.X, c.Y));
        Assert.False(TerrainCollision.CanSweep(map, a.X, a.Y, c.X, c.Y));
    }

    [Theory]
    [InlineData(-0.5f, 0f, true)]
    [InlineData(0.5f, 0.5f, false)]
    [InlineData(-Radius, 0.5f, false)]
    [InlineData(0.5f, -Radius, false)]
    [InlineData(-Radius, -Radius, false)]
    [InlineData(1f + Radius, 0.5f, false)]
    [InlineData(1.31f, 0.5f, true)]
    [InlineData(-8.75f, 0f, false)]
    [InlineData(7.75f, 0f, false)]
    public void ZeroLengthSweepMatchesCanStandAt(float x, float y, bool canStand)
    {
        var map = NewMap();
        map[9, 8] = 1;

        Assert.Equal(canStand, TerrainCollision.CanStandAt(map, x, y));
        Assert.Equal(canStand, TerrainCollision.CanSweep(map, x, y, x, y));
    }

    private static int[,] NewMap() => new int[Constants.MAP_WIDTH, Constants.MAP_HEIGHT];
}
