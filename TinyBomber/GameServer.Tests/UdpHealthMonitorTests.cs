using System.Diagnostics;
using GameShared;

namespace GameServer.Tests;

public sealed class UdpHealthMonitorTests
{
    private static long At(int seconds) => 100 + seconds * Stopwatch.Frequency;

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void FirstScheduledProbeRequiresReadyAndFiveSeconds(int offset)
    {
        var monitor = new UdpHealthMonitor();
        Assert.Null(monitor.BeginProbe(At(0), scheduled: false));
        monitor.Start(At(0));
        Assert.Equal(At(5), monitor.NextCheckAt);
        Assert.Equal(offset < 0 ? null : (long?)1, monitor.BeginProbe(At(5) + offset, scheduled: true));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void EchoMustArriveBeforeThePendingProbeDeadline(int offset)
    {
        var monitor = Start();
        Assert.Equal(1, monitor.BeginProbe(At(5), scheduled: true));
        Assert.Equal(At(10), monitor.ProbeDeadline);
        Assert.Equal(offset < 0, monitor.AcceptEcho(1, At(10) + offset));
        monitor.Advance(At(10) + offset);
        Assert.Null(monitor.PendingSequence);
        Assert.Equal(offset < 0 ? 1 : 0, monitor.EchoesAccepted);
        Assert.Equal(offset < 0 ? 0 : 1, monitor.ProbesExpired);
    }

    [Fact]
    public void ExpirationPrecedesNextProbeAndOnlyCurrentEchoCanRecover()
    {
        var monitor = Start();
        Assert.Equal(1, monitor.BeginProbe(At(5), scheduled: true));
        monitor.RecordProbeSent();
        Assert.Null(monitor.BeginProbe(At(10) - 1, scheduled: false));
        Assert.Equal(2, monitor.BeginProbe(At(10), scheduled: true));
        monitor.RecordProbeSent();
        Assert.Equal(1, monitor.ProbesExpired);
        Assert.False(monitor.AcceptEcho(1, At(10) + 1));
        Assert.False(monitor.AcceptEcho(9, At(14)));
        Assert.Equal(2, monitor.ProbesSent);
        monitor.Advance(At(15) - 1);
        Assert.False(monitor.IsDegraded);

        Assert.Equal(3, monitor.BeginProbe(At(15), scheduled: true));
        Assert.True(monitor.IsDegraded);
        Assert.Equal(2, monitor.ProbesExpired);
        monitor.Advance(At(15) + 1);
        Assert.Equal(1, monitor.Degradations);
        Assert.True(monitor.AcceptEcho(3, At(16)));
        Assert.False(monitor.AcceptEcho(3, At(17)));
        Assert.False(monitor.IsDegraded);
        Assert.Equal(1, monitor.Recoveries);
        monitor.Advance(At(31) - 1);
        Assert.False(monitor.IsDegraded);
        monitor.Advance(At(31));
        Assert.True(monitor.IsDegraded);
    }

    [Fact]
    public void StallSendsOnlyOneProbeAndManualProbeSharesTheSlot()
    {
        var monitor = Start();
        Assert.Equal(1, monitor.BeginProbe(At(17), scheduled: true));
        Assert.Null(monitor.BeginProbe(At(17), scheduled: true));
        Assert.Null(monitor.BeginProbe(At(17), scheduled: false));
        Assert.True(monitor.AcceptEcho(1, At(18)));
        Assert.Equal(At(22), monitor.NextCheckAt);
        Assert.Null(monitor.BeginProbe(At(22) - 1, scheduled: true));
        Assert.Equal(2, monitor.BeginProbe(At(22), scheduled: true));
    }

    [Fact]
    public void SocketUnavailableStopsProbesAndFreshSessionStartsClean()
    {
        var first = Start();
        Assert.Equal(1, first.BeginProbe(At(5), scheduled: true));
        first.MarkSocketUnavailable();
        Assert.True(first.IsDegraded);
        Assert.True(first.SocketUnavailable);
        Assert.Null(first.NextCheckAt);
        Assert.Null(first.PendingSequence);
        Assert.False(first.AcceptEcho(1, At(6)));
        Assert.Null(first.BeginProbe(At(30), scheduled: false));

        var replacement = new UdpHealthMonitor();
        replacement.Start(At(30));
        Assert.False(replacement.IsDegraded);
        Assert.Null(replacement.BeginProbe(At(35) - 1, scheduled: true));
        Assert.Equal(1, replacement.BeginProbe(At(35), scheduled: true));
    }

    private static UdpHealthMonitor Start()
    {
        var monitor = new UdpHealthMonitor();
        monitor.Start(At(0));
        return monitor;
    }
}
