using System.Diagnostics;
using GameShared;

namespace GameServer.Tests;

public sealed class ServerMetricsTests
{
    [Fact]
    public void TickDistributionComesFromIndividualCompletedIntervalsAndResets()
    {
        var metrics = new ServerMetrics(0);
        long t = 0;
        metrics.BeginTick(t);
        foreach (int ms in new[] { 20, 25, 35, 41, 80 })
        {
            metrics.EndGameplay(new ActionBatchMetrics(1, 10, 1, 1, 0, 8, 2), 2);
            metrics.BeginTick(t += At(ms));
        }
        var first = Publish(metrics, t);
        Assert.Equal(35d, first.Timing!.P50Ms!.Value, 4);
        Assert.Equal(80d, first.Timing.P99Ms!.Value, 4);
        Assert.Equal(3, first.Timing.IntervalOverruns);
        Assert.Equal(1, first.Timing.WorkOverruns);
        Assert.Equal(2, first.Timing.SleepOvershootAvgMs);
        var next = Publish(metrics, t + 1);
        Assert.Null(next.Timing!.P95Ms);
        Assert.Equal(5, first.Timing.IntervalsMs.Count);
    }

    private static long At(double milliseconds) => (long)(milliseconds * Stopwatch.Frequency / 1000);

    private static ServerMetricsSnapshot Publish(ServerMetrics collector, long timestamp,
        IEnumerable<RoomMetrics>? rooms = null) =>
        collector.Publish(timestamp, DateTimeOffset.UtcNow, 8, 6,
            rooms ?? Array.Empty<RoomMetrics>(), 2, Stopwatch.GetTimestamp(), null);

    [Fact]
    public void FirstTickHasNoIntervalAndSnapshotDoesNotConsumePendingTick()
    {
        var collector = new ServerMetrics(0);
        collector.BeginTick(0);
        collector.EndGameplay(new ActionBatchMetrics(2, 30, 3, 2, 1), 4);
        var first = Publish(collector, At(10));
        Assert.Null(first.TickIntervalMs.Average);
        Assert.Null(first.TickNonSleepWallMs.Maximum);
        Assert.Null(first.TickRate);
        Assert.Null(first.Process);
        Assert.Null(first.ProcessSampleAgeMs);
        collector.BeginTick(At(40));
        var second = Publish(collector, At(50));
        Assert.Equal(40, second.TickIntervalMs.Average!.Value, 6);
        Assert.Equal(10, second.TickNonSleepWallMs.Average!.Value, 6);
        Assert.Equal(2, second.SuccessfulActions);
        Assert.Equal(1, second.FailedActions);
    }

    [Fact]
    public void CompletedTicksUseMatchingSleepAndSeparateMeasuredAndDerivedWork()
    {
        var collector = new ServerMetrics(0);
        collector.BeginTick(0);
        collector.EndGameplay(new ActionBatchMetrics(2, 30, 5, 4, 1), 4);
        collector.BeginTick(At(40));
        collector.EndGameplay(new ActionBatchMetrics(6, 20, 3, 3, 0), 8);
        collector.BeginTick(At(90));
        Assert.False(collector.IsSnapshotDue(At(999)));
        Assert.True(collector.IsSnapshotDue(At(1000)));
        var snapshot = Publish(collector, At(1000));
        Assert.Equal(4, snapshot.ActionProcessingMs.Average);
        Assert.Equal(6, snapshot.RoomUpdateMs.Average);
        Assert.Equal(10, snapshot.GameplayWorkMs.Average);
        Assert.Equal(14, snapshot.GameplayWorkMs.Maximum);
        Assert.Equal(25, snapshot.SleepElapsedMs.Average);
        Assert.Equal(45, snapshot.TickIntervalMs.Average);
        Assert.Equal(20, snapshot.TickNonSleepWallMs.Average);
        Assert.Equal(30, snapshot.TickNonSleepWallMs.Maximum);
        Assert.Equal(1000.0 / 45, snapshot.TickRate);
        Assert.Equal(5, snapshot.LargestActionBatch);
        Assert.Equal(7, snapshot.SuccessfulActions);
        Assert.Equal(1, snapshot.FailedActions);
        Assert.Equal(2, snapshot.WaitingActions);
        Assert.Null(Publish(collector, At(2000)).ActionProcessingMs.Average);
        Assert.Equal(2, snapshot.ActionProcessingMs.Samples);
    }

    [Fact]
    public void RoomSnapshotSurvivesEliminationLeaveFinishAndReplay()
    {
        var playing = new GameLogic(1);
        var waiting = new GameLogic(2);
        var first = new Client(1) { Username = "A", UdpConfirmed = true };
        var second = new Client(2) { Username = "B", UdpConfirmed = true };
        var late = new Client(3) { Username = "C", UdpConfirmed = true };
        playing.TryAddMember(first);
        playing.TryAddMember(second);
        playing.StartRound(first, playing.Id, 0);
        playing.TryAddMember(late);
        waiting.TryAddMember(new Client(4) { Username = "D", UdpConfirmed = true });
        var source = new List<RoomMetrics> { RoomMetrics.Capture(playing), RoomMetrics.Capture(waiting) };
        var collector = new ServerMetrics(0);
        var snapshot = Publish(collector, At(1000), source);
        Assert.Equal(8, snapshot.ConnectedClients);
        Assert.Equal(6, snapshot.ReadyClients);
        Assert.Equal(4, snapshot.RoomMembers);
        Assert.Equal(2, snapshot.RoundParticipants);
        Assert.Equal(2, snapshot.ActivePlayers);
        Assert.Equal(2, snapshot.AlivePlayers);
        Assert.Equal(1, snapshot.PlayingRooms);
        Assert.Equal(1, snapshot.WaitingRooms);
        Assert.Throws<NotSupportedException>(() => ((IList<RoomMetrics>)snapshot.Rooms).Clear());
        source.Clear();
        first.player!.isAlive = false;
        Assert.Equal(1, RoomMetrics.Capture(playing).AlivePlayers);
        playing.RemoveMember(late);
        playing.UpdateRound(Stopwatch.GetTimestamp());
        Assert.Equal(0, RoomMetrics.Capture(playing).ActivePlayers);
        Assert.Equal(2, RoomMetrics.Capture(playing).RoundParticipants);
        playing.StartRound(first, playing.Id, playing.RoundId);
        Assert.Equal(2, RoomMetrics.Capture(playing).AlivePlayers);
        Assert.Equal(2, snapshot.RoomCount);
        Assert.Equal(3, snapshot.Rooms[0].Members);
        Assert.Equal(2, snapshot.AlivePlayers);
        Assert.Equal(MatchPhase.Playing, snapshot.Rooms[0].Phase);
    }

    [Fact]
    public void CaptureFailurePreservesLatestSnapshotAndSuccessClearsError()
    {
        var collector = new ServerMetrics(0);
        var previous = Publish(collector, At(1000));
        IEnumerable<RoomMetrics> BrokenCapture()
        {
            yield return new RoomMetrics(1, MatchPhase.Waiting, 0, 0, 0, 0, 0);
            throw new InvalidOperationException("capture failed");
        }
        var error = Assert.Throws<InvalidOperationException>(() =>
            Publish(collector, At(2000), BrokenCapture()));
        collector.RecordCaptureFailure(error);
        Assert.Same(previous, collector.Latest);
        Assert.Contains("capture failed", collector.CaptureError);
        Publish(collector, At(2000));
        Assert.Null(collector.CaptureError);
    }
}
