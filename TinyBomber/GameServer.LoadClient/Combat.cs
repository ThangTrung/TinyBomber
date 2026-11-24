using System.Diagnostics;
using GameShared;
using Cell = (int Col, int Row);

namespace GameServer.LoadClient;

internal sealed partial class SimulatedClient
{
    private Position authoritativePosition;
    private int placementsSeen, explosionsSeen, blocksSeen, spawnsSeen, pickupsSeen, requestsSent, roundsFinished;
    private double playingSeconds;
    private int? timingRound;
    private long playingSince;
    private static readonly Cell[] Directions = [(1, 0), (-1, 0), (0, 1), (0, -1)];

    internal int CompletedCombatRounds => roundsFinished;
    internal int ExplosionsSeen { get { lock (stateLock) return explosionsSeen; } }
    internal object CombatReport()
    {
        lock (stateLock) return new { ClientId = id, RequestsSent = requestsSent, PlacementDeliveries = placementsSeen,
            ExplosionDeliveries = explosionsSeen, SoftBlockDeliveries = blocksSeen, ItemSpawnDeliveries = spawnsSeen,
            PickupDeliveries = pickupsSeen, RoundsFinished = roundsFinished, MeasuredPlayingSeconds = playingSeconds };
    }

    // One actor per room. Other members observe the same reliable events and keep their sessions alive.
    internal static async Task RunCombatRoomAsync(SimulatedClient[] group, bool repeated, WorkloadWindow window, CancellationToken token)
    {
        var actor = group[0];
        try
        {
            while (!token.IsCancellationRequested)
            {
                var state = actor.ReadState();
                if (!state.Connected || !state.Ready) throw new IOException("Combat session closed.");
                if (state.Phase == MatchPhase.Finished)
                {
                    actor.EndPlaying(window);
                    actor.roundsFinished++;
                    if (repeated && actor.roundsFinished == 5) return;
                    await actor.SendRoundAction(ClientPackets.requestStart, state, token);
                    await WaitAsync(() => group.All(c => c.ReadState() is { Phase: MatchPhase.Playing, HasRoundData: true } next
                        && next.RoundId == state.RoundId + 1), token);
                    continue;
                }
                if (state.Phase != MatchPhase.Playing || !state.Alive)
                {
                    await Task.Delay(10, token);
                    continue;
                }
                if (actor.timingRound != state.RoundId)
                {
                    actor.timingRound = state.RoundId;
                    actor.playingSince = Stopwatch.GetTimestamp();
                }
                if (repeated)
                {
                    await actor.SendRoundAction(ClientPackets.placeBomb, state, token);
                    await WaitAsync(() => actor.ReadState().Phase == MatchPhase.Finished, token);
                }
                else await actor.CombatTurnAsync(state, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { actor.EndPlaying(window); }
    }

    private void EndPlaying(WorkloadWindow window)
    {
        if (!timingRound.HasValue) return;
        long start = Math.Max(playingSince, window.MeasurementStartsAt);
        long end = Math.Min(Stopwatch.GetTimestamp(), window.EndsAt);
        if (end > start) playingSeconds += Stopwatch.GetElapsedTime(start, end).TotalSeconds;
        timingRound = null;
    }

    private async Task SendRoundAction(ClientPackets type, ClientState state, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Finish an initiated frame across the measurement boundary.
        await SendTcpAsync(type, packet => { packet.Write(state.RoomId); packet.Write(state.RoundId); }, lifetime.Token);
        if (type == ClientPackets.placeBomb) requestsSent++;
    }

    private static async Task WaitAsync(Func<bool> ready, CancellationToken token)
    {
        long start = Stopwatch.GetTimestamp();
        while (!ready())
        {
            token.ThrowIfCancellationRequested();
            if (Stopwatch.GetElapsedTime(start) > TimeSpan.FromSeconds(5))
                throw new TimeoutException("Combat event or owner ACK exceeded 5 s.");
            await Task.Delay(4, token);
        }
    }

    private async Task CombatTurnAsync(ClientState state, CancellationToken token)
    {
        List<Cell> route = [];
        List<Cell>? escape = null;
        lock (stateLock)
        {
            var origin = TerrainCollision.WorldToGrid(authoritativePosition.X, authoritativePosition.Y);
            var paths = Routes(map!, origin);
            var item = paths.Keys.Where(c => bombUps.HasItem(c.Col, c.Row)).Select(c => (Cell?)c).FirstOrDefault();
            if (item is Cell target) route = PathTo(paths, origin, target);
            else
            {
                // BFS visits nearest reachable cells first. Pick a site with a soft neighbor and a two-step exit.
                foreach (var site in paths.Keys)
                {
                    if (!Neighbors(site).Any(c => map![c.Col, c.Row] == 2)) continue;
                    var exits = Routes(map!, site);
                    var safe = exits.Keys.Where(c => Math.Abs(c.Col-site.Col) + Math.Abs(c.Row-site.Row) == 2)
                        .Select(c => (Cell?)c).FirstOrDefault();
                    if (safe is not Cell refuge) continue;
                    escape = PathTo(exits, site, refuge);
                    route = PathTo(paths, origin, site);
                    break;
                }
            }
        }
        await WalkAsync(route, state, token);
        if (escape == null) { await Task.Delay(50, token); return; }
        int placed, exploded;
        lock (stateLock) { placed = placementsSeen; exploded = explosionsSeen; }
        await SendRoundAction(ClientPackets.placeBomb, state, token);
        await WaitAsync(() => { lock (stateLock) return placementsSeen > placed; }, token);
        await WalkAsync(escape, state, token);
        await WaitAsync(() => ExplosionsSeen > exploded, token);
    }

    private static IEnumerable<Cell> Neighbors(Cell c) =>
        Directions.Select(d => (c.Col + d.Col, c.Row + d.Row));

    private static Dictionary<Cell, Cell> Routes(int[,] terrain, Cell start)
    {
        var parents = new Dictionary<Cell, Cell> { [start] = start };
        var queue = new Queue<Cell>();
        queue.Enqueue(start);
        while (queue.TryDequeue(out var cell))
            foreach (var next in Neighbors(cell))
                if (next.Col >= 0 && next.Row >= 0 && next.Col < terrain.GetLength(0) && next.Row < terrain.GetLength(1)
                    && terrain[next.Col, next.Row] == 0 && parents.TryAdd(next, cell)) queue.Enqueue(next);
        return parents;
    }

    private static List<Cell> PathTo(Dictionary<Cell, Cell> parents, Cell start, Cell end)
    {
        var path = new List<Cell>();
        for (var cell = end; cell != start; cell = parents[cell]) path.Add(cell);
        path.Reverse();
        return path;
    }

    private async Task WalkAsync(List<Cell> route, ClientState state, CancellationToken token)
    {
        foreach (var cell in route)
        {
            var target = new Position(cell.Col - 8.5f, cell.Row - 7.5f);
            Position from;
            lock (stateLock) from = authoritativePosition;
            // At most 0.2 units per 60 ms, below the authoritative refill of 4 units/s.
            for (int step = 1; step <= 5; step++)
            {
                await Task.Delay(60, token);
                var next = new Position(from.X + (target.X-from.X)*step/5, from.Y + (target.Y-from.Y)*step/5);
                long seq;
                lock (stateLock) seq = ++lastSentMoveSeq;
                using var packet = ProtocolV4.CreateTcpPlayerMovement(state.RoomId, state.RoundId, seq, next.X, next.Y);
                await SendTcpFrameAsync(packet, ClientPackets.playerMovementV4, lifetime.Token);
                await WaitAsync(() => { lock (stateLock) return lastOwnerProcessedMoveSeq >= seq; }, token);
                lock (stateLock)
                    if (corrected || authoritativePosition != next)
                        throw new InvalidDataException("Combat move was not accepted at the requested position.");
            }
        }
    }
}

