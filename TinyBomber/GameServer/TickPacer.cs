using System.Diagnostics;
using GameShared;

namespace GameServer;

internal static class TickPacer
{
    internal static readonly long Period = Stopwatch.Frequency / Constants.TICKS_PER_SEC;

    // Absolute deadlines avoid accumulating sleep overshoot. Yield to the OS without spinning.
    internal static ActionBatchMetrics WaitUntil(long deadline, ActionBatchMetrics batch)
    {
        long start = Stopwatch.GetTimestamp();
        int requested = Math.Max(0, (int)Math.Ceiling(Stopwatch.GetElapsedTime(start, deadline).TotalMilliseconds));
        if (requested > 0) Thread.Sleep(requested);
        long finished = Stopwatch.GetTimestamp();
        return batch with { SleepMs = requested == 0 ? 0 : Stopwatch.GetElapsedTime(start, finished).TotalMilliseconds,
            RequestedSleepMs = requested, DeadlineLatenessMs = Math.Max(0, Stopwatch.GetElapsedTime(deadline, finished).TotalMilliseconds) };
    }

    internal static long Next(long deadline, long now) =>
        deadline + (1 + Math.Max(0L, now - deadline) / Period) * Period;
}

