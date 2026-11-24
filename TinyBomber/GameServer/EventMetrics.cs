using System.Collections.ObjectModel;

namespace GameServer;

// Totals are process-scoped. TCP frame counts use complete frames, UDP counts use datagrams.
internal enum ServerEvent
{
    TcpFrameReceived, TcpFrameWritten, UdpDatagramReceived, UdpDatagramSent, TcpMalformed, UdpMalformed,
    MoveTcp, MoveUdp, MoveScopeRejected, MoveStale, MoveCollisionRejected, MoveCreditRejected,
    MoveAccepted, OwnerAck, Correction, FinalRefresh, BombRequest, BombPlaced, Explosion,
    SoftDestroyed, ItemSpawn, ItemCollect, RoundStart, RoundFinish
}

internal static class EventMetrics
{
    internal static readonly IReadOnlyList<ServerEvent> Names = Array.AsReadOnly(Enum.GetValues<ServerEvent>());
    private static readonly long[] totals = new long[Names.Count];
    internal static void Record(ServerEvent name, int count = 1) => Interlocked.Add(ref totals[(int)name], count);
    internal static IReadOnlyDictionary<string, long> Capture() =>
        new ReadOnlyDictionary<string, long>(Names.ToDictionary(name => name.ToString(), name => Interlocked.Read(ref totals[(int)name])));
}

// Queue counts are current gauges. Peak is the maximum enqueue depth of a currently attached wrapper.
// RTT samples include server FIFO and application processing. Retried challenges are excluded.
internal sealed record TransportSample(int Pending, int HighWater, int InFlight, long HeartbeatSamples, double HeartbeatTotalMs)
{
    public double? HeartbeatMeanMs => HeartbeatSamples == 0 ? null : HeartbeatTotalMs / HeartbeatSamples;
}

internal sealed record TickWindow(IReadOnlyList<double> IntervalsMs, double RequestedSleepAvgMs,
    double SleepOvershootAvgMs, double DeadlineLatenessAvgMs, int WorkOverruns, int IntervalOverruns)
{
    public double? P50Ms => Quantile(0.50);
    public double? P95Ms => Quantile(0.95);
    public double? P99Ms => Quantile(0.99);
    private double? Quantile(double p) => IntervalsMs.Count == 0 ? null :
        IntervalsMs[(int)Math.Ceiling(IntervalsMs.Count * p) - 1];
}

