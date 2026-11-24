using System.Diagnostics;

namespace GameServer;

internal readonly record struct ProcessReading(TimeSpan CpuTime, long WorkingSetBytes, ProcessResources? Resources = null);

internal sealed record ProcessResources(long PrivateBytes, long ManagedBytes, int Gen0, int Gen1, int Gen2, int Threads, int? Handles);

internal sealed record ProcessMetricsSample(
    long Timestamp, DateTimeOffset TimestampUtc, double? CpuPercent,
    long? WorkingSetBytes, int LogicalProcessors, double QueryMs, string? Error, ProcessResources? Resources = null);

internal sealed class ProcessMetricsSampler : IDisposable
{
    private readonly Func<ProcessReading> read;
    private readonly CancellationTokenSource cancellation = new();
    private readonly int logicalProcessors;
    private ProcessMetricsSample? latest;
    private ProcessReading? previous;
    private long previousTimestamp;
    private Task? worker;
    private int stopped;

    internal ProcessMetricsSampler(Func<ProcessReading>? read = null)
    {
        this.read = read ?? ReadProcess;
        logicalProcessors = Environment.ProcessorCount;
    }

    internal ProcessMetricsSample? Latest => Volatile.Read(ref latest);
    internal Task Completion => worker ?? Task.CompletedTask;

    // Start/Dispose are called by the server lifecycle owner.
    internal void Start() => worker ??= Task.Run(RunAsync);

    private async Task RunAsync()
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                long started = Stopwatch.GetTimestamp();
                Capture();
                TimeSpan remaining = TimeSpan.FromSeconds(1) - Stopwatch.GetElapsedTime(started);
                if (remaining > TimeSpan.Zero)
                    await Task.Delay(remaining, cancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            cancellation.Dispose();
        }
    }

    // Only the worker calls Capture in production. Tests can sample without starting a worker.
    internal ProcessMetricsSample Capture()
    {
        long started = Stopwatch.GetTimestamp();
        ProcessMetricsSample sample;
        try
        {
            ProcessReading current = read();
            long now = Stopwatch.GetTimestamp();
            double? cpu = previous is ProcessReading baseline
                ? CalculateCpuPercent(current.CpuTime - baseline.CpuTime,
                    Stopwatch.GetElapsedTime(previousTimestamp, now), logicalProcessors)
                : null;
            sample = new ProcessMetricsSample(now, DateTimeOffset.UtcNow, cpu,
                current.WorkingSetBytes, logicalProcessors,
                Stopwatch.GetElapsedTime(started, now).TotalMilliseconds, null, current.Resources);
            previous = current;
            previousTimestamp = now;
        }
        catch (Exception ex)
        {
            long now = Stopwatch.GetTimestamp();
            sample = new ProcessMetricsSample(now, DateTimeOffset.UtcNow, null, null,
                logicalProcessors, Stopwatch.GetElapsedTime(started, now).TotalMilliseconds,
                $"{ex.GetType().Name}: {ex.Message}");
            previous = null;
        }
        Volatile.Write(ref latest, sample);
        return sample;
    }

    // Percentage of the logical CPU capacity visible to the runtime. Linux affinity,
    // container quotas and VPS CPU allocation can change that denominator and available capacity.
    internal static double? CalculateCpuPercent(TimeSpan cpu, TimeSpan elapsed, int processors) =>
        cpu < TimeSpan.Zero || elapsed <= TimeSpan.Zero || processors <= 0
            ? null : 100 * cpu.TotalSeconds / elapsed.TotalSeconds / processors;

    private static ProcessReading ReadProcess()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var resources = new ProcessResources(process.PrivateMemorySize64, GC.GetTotalMemory(false),
            GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), process.Threads.Count,
            OperatingSystem.IsWindows() ? process.HandleCount : null);
        return new ProcessReading(process.TotalProcessorTime, process.WorkingSet64, resources);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref stopped, 1) != 0) return;
        cancellation.Cancel();
        if (worker == null) cancellation.Dispose();
    }
}
