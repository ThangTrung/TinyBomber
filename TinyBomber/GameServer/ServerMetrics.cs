using System.Collections.ObjectModel;
using System.Diagnostics;
using GameShared;

namespace GameServer;

internal readonly record struct ActionBatchMetrics(
    double ProcessingMs, double SleepMs, int BatchSize, int Succeeded, int Failed);

internal sealed record MetricSummary(int Samples, double? Average, double? Maximum);

internal sealed record RoomMetrics(
    int Id, MatchPhase Phase, int Members, int ReadyClients,
    int RoundParticipants, int ActivePlayers, int AlivePlayers)
{
    // Called by the gameplay owner after room updates.
    internal static RoomMetrics Capture(GameLogic room)
    {
        int participants = room.Members.Count(slot => slot.player != null);
        bool playing = room.Phase == MatchPhase.Playing;
        return new RoomMetrics(room.Id, room.Phase, room.Members.Count,
            room.Members.Count(slot => slot.IsReady), participants,
            playing ? participants : 0,
            playing ? room.Members.Count(slot => slot.player is { isAlive: true }) : 0);
    }
}

internal sealed record ServerMetricsSnapshot(
    DateTimeOffset TimestampUtc, double WindowSeconds, int ConnectedClients, int ReadyClients,
    ReadOnlyCollection<RoomMetrics> Rooms, int WaitingActions, int LargestActionBatch,
    long SuccessfulActions, long FailedActions, MetricSummary ActionProcessingMs,
    MetricSummary RoomUpdateMs, MetricSummary GameplayWorkMs, MetricSummary SleepElapsedMs,
    MetricSummary TickIntervalMs, MetricSummary TickNonSleepWallMs, double? TickRate,
    double SnapshotCaptureMs, ProcessMetricsSample? Process, double? ProcessSampleAgeMs,
    NetworkMetricsSnapshot? Network = null)
{
    public int RoomCount => Rooms.Count;
    public int WaitingRooms => Rooms.Count(room => room.Phase == MatchPhase.Waiting);
    public int PlayingRooms => Rooms.Count(room => room.Phase == MatchPhase.Playing);
    public int FinishedRooms => Rooms.Count(room => room.Phase == MatchPhase.Finished);
    public int RoomMembers => Rooms.Sum(room => room.Members);
    public int RoundParticipants => Rooms.Sum(room => room.RoundParticipants);
    public int ActivePlayers => Rooms.Sum(room => room.ActivePlayers);
    public int AlivePlayers => Rooms.Sum(room => room.AlivePlayers);
}

internal sealed class ServerMetrics
{
    private sealed class Accumulator
    {
        private int count;
        private double sum;
        private double maximum;

        internal void Add(double value)
        {
            count++;
            sum += value;
            maximum = Math.Max(maximum, value);
        }

        internal MetricSummary Take()
        {
            var result = new MetricSummary(count, count == 0 ? null : sum / count,
                count == 0 ? null : maximum);
            count = 0;
            sum = maximum = 0;
            return result;
        }
    }

    private readonly Accumulator actions = new();
    private readonly Accumulator rooms = new();
    private readonly Accumulator gameplay = new();
    private readonly Accumulator sleep = new();
    private readonly Accumulator intervals = new();
    private readonly Accumulator nonSleep = new();
    private long windowStart;
    private long? previousTickStart;
    private ActionBatchMetrics? pendingBatch;
    private double pendingRoomMs;
    private int largestBatch;
    private long successful;
    private long failed;
    private ServerMetricsSnapshot? latest;
    private string? captureError;

    internal ServerMetrics(long startTimestamp) => windowStart = startTimestamp;
    internal ServerMetricsSnapshot? Latest => Volatile.Read(ref latest);
    internal string? CaptureError => Volatile.Read(ref captureError);
    internal void RecordCaptureFailure(Exception error) =>
        Volatile.Write(ref captureError, $"{error.GetType().Name}: {error.Message}");

    internal void BeginTick(long timestamp)
    {
        // Closing the previous interval here includes its monitoring work and scheduler delays.
        // Gameplay counts in a snapshot are current; timing aggregates contain completed intervals.
        if (previousTickStart is long start && pendingBatch is ActionBatchMetrics batch)
        {
            double intervalMs = Stopwatch.GetElapsedTime(start, timestamp).TotalMilliseconds;
            actions.Add(batch.ProcessingMs);
            rooms.Add(pendingRoomMs);
            gameplay.Add(batch.ProcessingMs + pendingRoomMs);
            sleep.Add(batch.SleepMs);
            intervals.Add(intervalMs);
            nonSleep.Add(intervalMs - batch.SleepMs);
            largestBatch = Math.Max(largestBatch, batch.BatchSize);
            successful += batch.Succeeded;
            failed += batch.Failed;
        }
        previousTickStart = timestamp;
        pendingBatch = null;
    }

    internal void EndGameplay(ActionBatchMetrics batch, double roomUpdateMs)
    {
        pendingBatch = batch;
        pendingRoomMs = roomUpdateMs;
    }

    internal bool IsSnapshotDue(long timestamp) =>
        Stopwatch.GetElapsedTime(windowStart, timestamp) >= TimeSpan.FromSeconds(1);

    internal ServerMetricsSnapshot Publish(long timestamp, DateTimeOffset utc,
        int connectedClients, int readyClients, IEnumerable<RoomMetrics> roomMetrics,
        int waitingActions, long captureStarted, ProcessMetricsSample? process,
        NetworkMetricsSnapshot? network = null)
    {
        double windowSeconds = Stopwatch.GetElapsedTime(windowStart, timestamp).TotalSeconds;
        var copiedRooms = Array.AsReadOnly(roomMetrics.ToArray());
        double captureMs = Stopwatch.GetElapsedTime(captureStarted).TotalMilliseconds;
        MetricSummary tickIntervals = intervals.Take();
        var snapshot = new ServerMetricsSnapshot(utc, windowSeconds, connectedClients, readyClients,
            copiedRooms, waitingActions, largestBatch, successful, failed,
            actions.Take(), rooms.Take(), gameplay.Take(), sleep.Take(), tickIntervals, nonSleep.Take(),
            tickIntervals.Samples == 0 || tickIntervals.Average <= 0
                ? null : 1000.0 / tickIntervals.Average,
            captureMs, process, process == null ? null :
                Stopwatch.GetElapsedTime(process.Timestamp, timestamp).TotalMilliseconds, network);
        largestBatch = 0;
        successful = failed = 0;
        windowStart = timestamp;
        Volatile.Write(ref latest, snapshot);
        Volatile.Write(ref captureError, null);
        return snapshot;
    }
}
