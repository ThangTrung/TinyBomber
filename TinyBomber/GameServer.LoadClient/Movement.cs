using GameShared;

namespace GameServer.LoadClient;

internal readonly record struct Position(float X, float Y)
{
    internal bool IsFinite => float.IsFinite(X) && float.IsFinite(Y);
    internal float DistanceTo(Position other)
    {
        float dx = other.X - X;
        float dy = other.Y - Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}

internal sealed class MovementPath
{
    internal const float Speed = 5f; // PlayerController uses 5 units/s and Unity's 0.02 s fixed timestep.
    internal const float MaxStep = Speed * 0.02f;
    private const float SideStep = 0.07f;
    private const float LaneStep = 0.000004f;
    private const float MaxDrift = LaneStep * LoadOptions.MaxSeconds * LoadOptions.MaxMovementHz;
    private readonly Position spawn;
    private readonly int xDirection;
    private readonly int yDirection;

    internal MovementPath(Position spawn, int[,] map)
    {
        this.spawn = spawn;
        if (!spawn.IsFinite || !TerrainCollision.CanStandAt(map, spawn.X, spawn.Y))
            throw new InvalidDataException("Spawn không nằm ở vị trí có thể đứng.");

        foreach (int x in new[] { 1, -1 })
        foreach (int y in new[] { 1, -1 })
        {
            // The swept footprint stays in the same clear tile for the whole route.
            float farX = spawn.X + x * 2 * SideStep, farY = spawn.Y + y * MaxDrift;
            const float radius = BombCollision.PlayerRadius;
            if (MathF.Floor(MathF.Min(spawn.X, farX) - radius) != MathF.Floor(MathF.Max(spawn.X, farX) + radius) ||
                MathF.Floor(MathF.Min(spawn.Y, farY) - radius) != MathF.Floor(MathF.Max(spawn.Y, farY) + radius))
                continue;
            if (!TerrainCollision.CanStandAt(map, farX, farY)) continue;
            xDirection = x;
            yDirection = y;
            return;
        }
        throw new InvalidDataException("Không tìm được đường di chuyển hợp lệ quanh spawn.");
    }

    internal Position Target(int step)
    {
        int across = 2 - Math.Abs(2 - step % 4);
        return new Position(spawn.X + xDirection * (across * SideStep),
            spawn.Y + yDirection * (step * LaneStep));
    }
}

internal sealed record MovementEvidence(int ObserverId, Position SentFrom, Position Sent, Position Observed);

// The sender records its inputs and exactly one peer reports matching positions.
internal sealed class MovementTrace
{
    private const int RecentLimit = 512;
    private readonly object sync = new();
    private readonly Dictionary<Position, Entry> recent = new();
    private readonly Queue<Position> order = new();
    private readonly (long Sequence, long SentAt)[] ackTimes = new (long, long)[RecentLimit];
    private readonly LatencyHistogram ackLatency = new(), peerLatency = new();
    internal object Latencies()
    {
        lock (sync) return new { OwnerAck = ackLatency.Report(), PeerApply = peerLatency.Report() };
    }
    internal void Acknowledge(long sequence)
    {
        lock (sync)
        {
            var sample = ackTimes[(int)(sequence % RecentLimit)];
            if (sample.Sequence == sequence && sample.SentAt > 0)
                ackLatency.Record(sample.SentAt, System.Diagnostics.Stopwatch.GetTimestamp());
        }
    }
    private int warmupSent;
    private int warmupObserved;
    private int measuredSent;
    private int measuredObserved;
    private double measuredLagTotal;
    private double measuredLagMax;
    private float measuredMaxStep;
    private MovementEvidence? firstMeasuredMatch;

    private sealed class Entry(Position origin, Position target, bool measured)
    {
        internal Position Origin { get; } = origin;
        internal Position Target { get; } = target;
        internal bool Measured { get; } = measured;
        internal long SentAt { get; init; }
        internal bool Sent { get; set; }
        internal int ObserverId { get; set; }
        internal Position Observed { get; set; }
    }

    internal void Prepare(Position origin, Position target, bool measured, long sequence, long sentAt)
    {
        lock (sync)
        {
            recent.Add(target, new Entry(origin, target, measured) { SentAt = sentAt });
            ackTimes[(int)(sequence % RecentLimit)] = (sequence, measured ? sentAt : 0);
            order.Enqueue(target);
            if (order.Count > RecentLimit) recent.Remove(order.Dequeue());
        }
    }

    internal void MarkSent(Position target, double lagMs)
    {
        lock (sync)
        {
            Entry entry = recent[target];
            entry.Sent = true;
            if (entry.Measured)
            {
                measuredSent++;
                measuredLagTotal += lagMs;
                measuredLagMax = Math.Max(measuredLagMax, lagMs);
                measuredMaxStep = Math.Max(measuredMaxStep, entry.Origin.DistanceTo(target));
            }
            else
            {
                warmupSent++;
            }
            if (entry.ObserverId != 0) CountMatch(entry);
        }
    }

    internal void Observe(int observerId, Position position)
    {
        lock (sync)
        {
            if (!recent.TryGetValue(position, out Entry? entry) || entry.ObserverId != 0) return;
            if (entry.Measured && entry.SentAt > 0) peerLatency.Record(entry.SentAt, System.Diagnostics.Stopwatch.GetTimestamp());
            entry.ObserverId = observerId;
            entry.Observed = position;
            if (entry.Sent) CountMatch(entry);
        }
    }

    private void CountMatch(Entry entry)
    {
        // A late observation belongs to the input's send window.
        if (!entry.Measured)
        {
            warmupObserved++;
            return;
        }
        measuredObserved++;
        firstMeasuredMatch ??= new MovementEvidence(entry.ObserverId, entry.Origin, entry.Target, entry.Observed);
    }

    internal MovementCounts Read()
    {
        lock (sync)
            return new MovementCounts(measuredSent, measuredObserved, measuredLagTotal, measuredLagMax,
                measuredMaxStep, firstMeasuredMatch, warmupSent, warmupObserved);
    }
}

internal sealed record MovementCounts(
    int Sent, int Observed, double LagTotalMs, double LagMaxMs, float MaxStep, MovementEvidence? FirstMatch,
    int WarmupSent, int WarmupObserved);
