namespace GameServer.Tests;

public sealed class ProcessMetricsSamplerTests
{
    [Fact]
    public void CpuUsesVisibleLogicalProcessorCapacityAndRejectsMissingIntervals()
    {
        Assert.Equal(25, ProcessMetricsSampler.CalculateCpuPercent(
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 4));
        Assert.Null(ProcessMetricsSampler.CalculateCpuPercent(TimeSpan.Zero, TimeSpan.Zero, 4));
        Assert.Null(ProcessMetricsSampler.CalculateCpuPercent(TimeSpan.FromSeconds(-1), TimeSpan.FromSeconds(1), 4));
    }

    [Fact]
    public void FirstSampleAndFirstSampleAfterFailureHaveNoCpuValue()
    {
        int attempt = 0;
        using var sampler = new ProcessMetricsSampler(() =>
        {
            if (++attempt == 2) throw new IOException("query failed");
            return new ProcessReading(TimeSpan.FromSeconds(attempt), 1024);
        });
        var first = sampler.Capture();
        Assert.Null(first.CpuPercent);
        Assert.Equal(1024, first.WorkingSetBytes);
        var failed = sampler.Capture();
        Assert.Null(failed.CpuPercent);
        Assert.Null(failed.WorkingSetBytes);
        Assert.Contains("query failed", failed.Error);
        Assert.Null(sampler.Capture().CpuPercent);
        Assert.NotNull(sampler.Capture().CpuPercent);
        Assert.Null(failed.CpuPercent);
    }

    [Fact]
    public async Task SlowProcessQueryRunsOutsideCallerAndShutdownDoesNotWaitForQuery()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var sampler = new ProcessMetricsSampler(() =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            return new ProcessReading(TimeSpan.Zero, 1);
        });
        sampler.Start();
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.Null(sampler.Latest);
            sampler.Dispose();
            Assert.False(sampler.Completion.IsCompleted);
        }
        finally { release.Set(); }
        await sampler.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(sampler.Latest);
    }
}
