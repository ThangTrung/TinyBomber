using System.Diagnostics;

namespace GameServer;

// Application socket byte totals. Completed sends describe successful local I/O completion.
internal sealed record NetworkTotals(
    long TcpReceivedBytes, long TcpCompletedWriteBytes, long TcpSendErrors,
    long UdpReceivedBytes, long UdpCompletedSendBytes, long UdpSendErrors);

internal sealed record NetworkByteRates(
    double? TcpReceivedBytesPerSecond, double? TcpCompletedWriteBytesPerSecond,
    double? UdpReceivedBytesPerSecond, double? UdpCompletedSendBytesPerSecond);

internal sealed record NetworkMetricsSnapshot(
    long Timestamp, double? IntervalSeconds, NetworkTotals Totals, NetworkByteRates Rates);

internal sealed class NetworkMetrics
{
    private long tcpReceivedBytes;
    private long tcpCompletedWriteBytes;
    private long tcpSendErrors;
    private long udpReceivedBytes;
    private long udpCompletedSendBytes;
    private long udpSendErrors;
    private NetworkMetricsSnapshot? previous;

    internal void RecordTcpReceive(int bytes) => Interlocked.Add(ref tcpReceivedBytes, bytes);
    internal void RecordTcpWrite(int bytes) => Interlocked.Add(ref tcpCompletedWriteBytes, bytes);
    internal void RecordTcpSendError() => Interlocked.Increment(ref tcpSendErrors);
    internal void RecordUdpReceive(int bytes) => Interlocked.Add(ref udpReceivedBytes, bytes);
    internal void RecordUdpSend(int bytes) => Interlocked.Add(ref udpCompletedSendBytes, bytes);
    internal void RecordUdpSendError() => Interlocked.Increment(ref udpSendErrors);

    // These independent reads may straddle concurrent completions. Reading totals does
    // not consume counters or advance the rate-sampling baseline.
    internal NetworkTotals ReadTotals() => new(
        Interlocked.Read(ref tcpReceivedBytes), Interlocked.Read(ref tcpCompletedWriteBytes),
        Interlocked.Read(ref tcpSendErrors), Interlocked.Read(ref udpReceivedBytes),
        Interlocked.Read(ref udpCompletedSendBytes), Interlocked.Read(ref udpSendErrors));

    // The update thread is the sole rate-sampling owner.
    internal NetworkMetricsSnapshot Capture(long timestamp)
    {
        NetworkTotals totals = ReadTotals();
        double? seconds = null;
        var rates = new NetworkByteRates(null, null, null, null);
        if (previous != null && timestamp > previous.Timestamp)
        {
            seconds = Stopwatch.GetElapsedTime(previous.Timestamp, timestamp).TotalSeconds;
            rates = new NetworkByteRates(
                (totals.TcpReceivedBytes - previous.Totals.TcpReceivedBytes) / seconds,
                (totals.TcpCompletedWriteBytes - previous.Totals.TcpCompletedWriteBytes) / seconds,
                (totals.UdpReceivedBytes - previous.Totals.UdpReceivedBytes) / seconds,
                (totals.UdpCompletedSendBytes - previous.Totals.UdpCompletedSendBytes) / seconds);
        }
        var snapshot = new NetworkMetricsSnapshot(timestamp, seconds, totals, rates);
        if (previous == null || timestamp > previous.Timestamp) previous = snapshot;
        return snapshot;
    }
}
