namespace GameServer.Tests;

public sealed class ProgramArgumentsTests
{
    [Fact]
    public void NoArgumentsUsesEightConnections()
    {
        Assert.True(Program.TryParseMaxConnections(Array.Empty<string>(), out int limit));
        Assert.Equal(8, limit);
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("2", 2)]
    [InlineData("16", 16)]
    [InlineData("2048", 2048)]
    public void AcceptsLimitsWithinResourceBound(string value, int expected)
    {
        Assert.True(Program.TryParseMaxConnections(new[] { "--max-connections", value }, out int limit));
        Assert.Equal(expected, limit);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("2.5")]
    [InlineData("0")]
    [InlineData("-2")]
    [InlineData("2147483648")]
    [InlineData("2049")]
    public void RejectsInvalidLimits(string value)
    {
        Assert.False(Program.TryParseMaxConnections(new[] { "--max-connections", value }, out _));
    }

    [Fact]
    public void RejectsMissingUnknownOrDuplicateArguments()
    {
        Assert.False(Program.TryParseMaxConnections(new[] { "--max-connections" }, out _));
        Assert.False(Program.TryParseMaxConnections(new[] { "--unknown", "2" }, out _));
        Assert.False(Program.TryParseMaxConnections(
            new[] { "--max-connections", "2", "--max-connections", "4" }, out _));
    }
}
