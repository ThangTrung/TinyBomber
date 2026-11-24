using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using GameShared;

namespace GameServer.LoadClient;

internal enum ErrorKind { Connection, Send, Receive, Timeout, Workload }

internal readonly record struct WorkloadWindow(
    long StartsAt, DateTimeOffset StartsUtc, int WarmupSeconds, int MeasurementSeconds)
{
    internal int TotalSeconds => WarmupSeconds + MeasurementSeconds;
    internal long MeasurementStartsAt => StartsAt + WarmupSeconds * Stopwatch.Frequency;
    internal long EndsAt => StartsAt + TotalSeconds * Stopwatch.Frequency;
    internal bool ContainsMeasurement(long timestamp) => timestamp >= MeasurementStartsAt && timestamp < EndsAt;
    internal DateTimeOffset UtcAt(long timestamp) => StartsUtc + Stopwatch.GetElapsedTime(StartsAt, timestamp);
}

internal sealed class RunErrors
{
    private const int DetailLimit = 12;
    private readonly ConcurrentDictionary<ErrorKind, int> counts = new();
    private readonly ConcurrentQueue<string> details = new();
    private int total;

    internal int Total => Volatile.Read(ref total);

    internal void Add(ErrorKind kind, string message)
    {
        counts.AddOrUpdate(kind, 1, static (_, count) => count + 1);
        if (Interlocked.Increment(ref total) <= DetailLimit)
            details.Enqueue($"{kind}: {message}");
    }

    internal Dictionary<string, int> Counts() => Enum.GetValues<ErrorKind>()
        .ToDictionary(kind => kind.ToString(), kind => counts.GetValueOrDefault(kind));

    internal string[] Details() => details.ToArray();
}

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args is ["--help"] or ["-h"])
        {
            Console.WriteLine(LoadOptions.Usage);
            return 0;
        }

        LoadOptions options;
        try { options = LoadOptions.Parse(args); }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(LoadOptions.Usage);
            return 2;
        }

        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += cancel;
        try { return await RunAsync(options, stop.Token); }
        finally { Console.CancelKeyPress -= cancel; }
    }

    private static async Task<int> RunAsync(LoadOptions options, CancellationToken stop)
    {
        var errors = new RunErrors();
        SimulatedClient[] clients = Enumerable.Range(1, options.Clients)
            .Select(index => new SimulatedClient(index, errors, stop)).ToArray();
        var rounds = new Dictionary<SimulatedClient, ClientState>();
        bool gameplay = options.Mode != WorkloadMode.LobbyIdle;
        bool active = options.Mode == WorkloadMode.GameplayActive;
        bool combat = options.Mode is WorkloadMode.CombatActive or WorkloadMode.RepeatedRound;
        bool completed = false;
        double elapsed = 0;
        DateTimeOffset? measurementStartUtc = null;
        DateTimeOffset? measurementEndUtc = null;
        double? cpuPercent = null;
        double? processSampleSeconds = null;
        long? workingSetBytes = null;
        using var process = Process.GetCurrentProcess();
        try
        {
            IPAddress address = await ResolveServerAsync(options.Server, stop);
            Console.Error.WriteLine($"LoadClient → {address}:{Constants.PORT} | {options.Mode} | {clients.Length} client");
            foreach (var batch in clients.Chunk(16))
            {
                stop.ThrowIfCancellationRequested();
                await Task.WhenAll(batch.Select(client => client.ConnectAsync(address)));
                if (errors.Total != 0) break;
            }
            RequireHealthy(clients, errors, rounds);

            if (gameplay)
                await SetUpRoomsAsync(clients, rounds, active, errors, stop);
            RequireHealthy(clients, errors, rounds);
            Console.Error.WriteLine($"Warm-up {options.WarmupSeconds}s, đo {options.Seconds}s. Phòng đang chơi: {rounds.Values.Select(state => state.RoomId).Distinct().Count()}.");

            var workload = new WorkloadWindow(Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow,
                options.WarmupSeconds, options.Seconds);
            TimeSpan? cpuAtMeasurementStart = null;
            long processSampleStart = 0;
            void SampleMeasurementStart()
            {
                measurementStartUtc = workload.UtcAt(workload.MeasurementStartsAt);
                process.Refresh();
                cpuAtMeasurementStart = process.TotalProcessorTime;
                processSampleStart = Stopwatch.GetTimestamp();
            }
            try
            {
                await RunWindowAsync(clients, rounds, active, options.Mode, options.MovementHz, workload,
                    SampleMeasurementStart, errors, stop);
            }
            finally
            {
                long finished = Stopwatch.GetTimestamp();
                if (finished >= workload.MeasurementStartsAt)
                {
                    long end = Math.Min(finished, workload.EndsAt);
                    elapsed = Stopwatch.GetElapsedTime(workload.MeasurementStartsAt, end).TotalSeconds;
                    measurementStartUtc ??= workload.UtcAt(workload.MeasurementStartsAt);
                    measurementEndUtc = workload.UtcAt(end);
                    process.Refresh();
                    workingSetBytes = process.WorkingSet64;
                    if (cpuAtMeasurementStart is TimeSpan cpuStart && finished > processSampleStart)
                    {
                        double sampleSeconds = Stopwatch.GetElapsedTime(processSampleStart, finished).TotalSeconds;
                        cpuPercent = 100 * (process.TotalProcessorTime - cpuStart).TotalSeconds /
                            (sampleSeconds * Environment.ProcessorCount);
                        processSampleSeconds = sampleSeconds;
                    }
                }
            }

            if (active)
            {
                // Allow the final datagrams to arrive without extending the send-rate window.
                await Task.Delay(TimeSpan.FromMilliseconds(400), stop);
                foreach (var client in clients)
                {
                    MovementCounts counts = client.Trace.Read();
                    if (counts.Sent == 0 || counts.Observed == 0 || client.WasCorrected)
                        errors.Add(ErrorKind.Workload, $"Slot {rounds[client].Id} thiếu movement/peer observation hoặc bị server chỉnh vị trí.");
                }
            }
            RequireHealthy(clients, errors, rounds, allowTransitions: combat);
            if (combat && clients.Any(c => c.ExplosionsSeen == 0))
                throw new InvalidDataException("A room member observed no combat explosion.");
            if (options.Mode == WorkloadMode.RepeatedRound && clients.All(c => c.CompletedCombatRounds != 5))
                throw new InvalidDataException("RepeatedRound did not finish five rounds.");
            completed = true;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (TimeoutException ex) { errors.Add(ErrorKind.Timeout, ex.Message); }
        catch (SocketException ex) { errors.Add(ErrorKind.Connection, ex.Message); }
        catch (Exception ex)
        {
            if (errors.Total == 0) errors.Add(ErrorKind.Workload, ex.Message);
        }
        finally
        {
            var states = clients.Select(client => client.ReadState()).ToArray();
            var udpHealth = clients.Select(client => client.ReadUdpHealth()).ToArray();
            var counts = clients.Select(client => client.Trace.Read()).ToArray();
            var reports = clients.Select((_, i) => new
                { ClientId = states[i].Id, states[i].RoomId, states[i].RoundId, counts[i].FirstMatch })
                .Where(report => report.FirstMatch != null).Take(8).ToArray();
            await Task.WhenAll(clients.Select(client => client.DisposeAsync().AsTask()));

            long sent = counts.Sum(count => (long)count.Sent);
            bool success = completed && errors.Total == 0 && !stop.IsCancellationRequested;
            var summary = new
            {
                Success = success,
                Cancelled = stop.IsCancellationRequested,
                Mode = options.Mode.ToString(),
                options.Server,
                RequestedClients = options.Clients,
                ConnectedClients = states.Count(state => state.Connected),
                ReadyClients = states.Count(state => state.Ready),
                PlayingClients = states.Count(IsPlaying),
                PlayingRooms = states.Where(IsPlaying).Select(state => state.RoomId).Distinct().Count(),
                PlayingRoomSizes = states.Where(IsPlaying).GroupBy(state => state.RoomId)
                    .ToDictionary(room => room.Key, room => room.Count()),
                ClientsSendingInput = counts.Count(count => count.Sent > 0),
                ClientsObservedByPeer = counts.Count(count => count.Observed > 0),
                MovementSent = sent,
                MatchedPeerPositionUpdates = counts.Sum(count => (long)count.Observed),
                WarmupMovementSent = counts.Sum(count => (long)count.WarmupSent),
                WarmupMatchedPeerPositionUpdates = counts.Sum(count => (long)count.WarmupObserved),
                RequestedMovementHzPerClient = active ? options.MovementHz : 0,
                RequestedMovementSlots = active ? (long)options.Clients * options.MovementHz * options.Seconds : 0,
                ActualMovementHzPerRequestedClient = elapsed > 0 ? sent / (elapsed * options.Clients) : (double?)null,
                ActualMovementPerSecond = elapsed > 0 ? sent / elapsed : (double?)null,
                SchedulingLagMeanMs = sent > 0 ? counts.Sum(count => count.LagTotalMs) / sent : (double?)null,
                SchedulingLagMaxMs = sent > 0 ? counts.Max(count => count.LagMaxMs) : (double?)null,
                MaxSentStep = sent > 0 ? counts.Max(count => count.MaxStep) : (float?)null,
                options.WarmupSeconds,
                MeasurementSeconds = elapsed,
                MeasurementStartUtc = measurementStartUtc,
                MeasurementEndUtc = measurementEndUtc,
                ProcessCpuPercentOfLogicalCapacity = cpuPercent,
                ProcessSampleSeconds = processSampleSeconds,
                Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                LogicalProcessors = Environment.ProcessorCount,
                ProcessWorkingSetBytes = workingSetBytes,
                Errors = errors.Counts(),
                ErrorDetails = errors.Details(),
                UdpHealth = new
                {
                    ProbesSent = udpHealth.Sum(health => health.ProbesSent),
                    EchoesAccepted = udpHealth.Sum(health => health.EchoesAccepted),
                    ProbesExpired = udpHealth.Sum(health => health.ProbesExpired),
                    Degradations = udpHealth.Sum(health => health.Degradations),
                    Recoveries = udpHealth.Sum(health => health.Recoveries),
                    SendErrors = udpHealth.Sum(health => health.SendErrors),
                    ReceiveErrors = udpHealth.Sum(health => health.ReceiveErrors),
                    DegradedClients = udpHealth.Count(health => health.IsDegraded),
                    UnavailableSockets = udpHealth.Count(health => health.SocketUnavailable)
                },
                Latency = clients.Select(c => new { Client = c.ReadState().Id, Samples = c.Trace.Latencies() }),
                Combat = clients.Select(c => c.CombatReport()),
                MovementExamples = reports
            };
            Console.WriteLine(JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
            completed = success;
        }
        return stop.IsCancellationRequested ? 130 : completed ? 0 : 1;
    }

    private static async Task<IPAddress> ResolveServerAsync(string host, CancellationToken stop)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, timeout.Token);
            return addresses.FirstOrDefault() ?? throw new SocketException((int)SocketError.HostNotFound);
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
        {
            throw new TimeoutException("Phân giải địa chỉ server quá hạn.");
        }
    }

    private static async Task SetUpRoomsAsync(SimulatedClient[] clients,
        Dictionary<SimulatedClient, ClientState> rounds, bool active, RunErrors errors, CancellationToken stop)
    {
        int roomCount = (clients.Length + Constants.MAX_PLAYERS - 1) / Constants.MAX_PLAYERS;
        int offset = 0;
        for (int i = 0; i < roomCount; i++)
        {
            int size = clients.Length / roomCount + (i < clients.Length % roomCount ? 1 : 0);
            var group = clients.AsSpan(offset, size).ToArray();
            offset += size;
            using var roomDeadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
            roomDeadline.CancelAfter(TimeSpan.FromSeconds(12));
            CancellationToken token = roomDeadline.Token;
            try
            {
                SimulatedClient host = group[0];
                await host.SendTcpAsync(ClientPackets.createRoom, null, token);
                await WaitForAsync(() => host.ReadState().RoomId > 0, errors, token);
                ClientState createdRoom = host.ReadState();

                await Task.WhenAll(group.Skip(1).Select(client =>
                    client.SendTcpAsync(ClientPackets.joinRoom, packet => packet.Write(createdRoom.RoomId), token)));
                await WaitForAsync(() => group.All(client =>
                {
                    ClientState state = client.ReadState();
                    return state.RoomId == createdRoom.RoomId && state.ReadyInRoom == size;
                }), errors, token);

                await host.SendTcpAsync(ClientPackets.requestStart, packet =>
                {
                    packet.Write(createdRoom.RoomId);
                    packet.Write(createdRoom.RoundId);
                }, token);
                int[] ids = group.Select(client => client.ReadState().Id).ToArray();
                await WaitForAsync(() => group.All(client =>
                {
                    ClientState state = client.ReadState();
                    return IsPlaying(state) && state.Alive && state.RoomId == createdRoom.RoomId &&
                        state.RoundId == createdRoom.RoundId + 1 && client.HasRoster(ids);
                }), errors, token);

                for (int member = 0; member < size; member++)
                {
                    SimulatedClient sender = group[member];
                    ClientState state = sender.ReadState();
                    rounds.Add(sender, state);
                    if (active) group[(member + 1) % size].Watch(state.Id, sender.Trace);
                }
            }
            catch (OperationCanceledException) when (!stop.IsCancellationRequested)
            {
                throw new TimeoutException($"Tạo/vào phòng hoặc bắt đầu ván {i + 1}/{roomCount} quá hạn.");
            }
        }
    }

    private static async Task WaitForAsync(Func<bool> condition, RunErrors errors, CancellationToken token)
    {
        while (!condition())
        {
            if (errors.Total != 0) throw new InvalidOperationException("Client báo lỗi khi thiết lập workload.");
            await Task.Delay(20, token);
        }
    }

    private static async Task RunWindowAsync(SimulatedClient[] clients, Dictionary<SimulatedClient, ClientState> rounds,
        bool active, WorkloadMode mode, int hz, WorkloadWindow workload, Action sampleMeasurementStart,
        RunErrors errors, CancellationToken stop)
    {
        using var window = CancellationTokenSource.CreateLinkedTokenSource(stop);
        bool measurementStarted = workload.WarmupSeconds == 0;
        if (measurementStarted) sampleMeasurementStart();
        window.CancelAfter(Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), workload.EndsAt));
        bool combat = mode is WorkloadMode.CombatActive or WorkloadMode.RepeatedRound;
        var movers = combat
            ? clients.GroupBy(c => rounds[c].RoomId).Select(group => SimulatedClient.RunCombatRoomAsync(
                group.ToArray(), mode == WorkloadMode.RepeatedRound, workload, window.Token)).ToArray()
            : active
            ? clients.Select(client => client.MoveAsync(rounds[client], workload, hz, window.Token)).ToArray()
            : Array.Empty<Task>();
        try
        {
            while (true)
            {
                long now = Stopwatch.GetTimestamp();
                if (now >= workload.EndsAt) break;
                RequireHealthy(clients, errors, rounds, allowTransitions: combat);
                if (!measurementStarted && now >= workload.MeasurementStartsAt)
                {
                    sampleMeasurementStart();
                    measurementStarted = true;
                }
                long nextBoundary = measurementStarted ? workload.EndsAt : workload.MeasurementStartsAt;
                double delayMs = Math.Clamp(Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), nextBoundary).TotalMilliseconds, 1, 100);
                await Task.Delay(TimeSpan.FromMilliseconds(delayMs), stop);
            }
        }
        finally
        {
            window.Cancel();
            await Task.WhenAll(movers);
        }
    }

    private static void RequireHealthy(SimulatedClient[] clients, RunErrors errors,
        Dictionary<SimulatedClient, ClientState> rounds, bool allowTransitions = false)
    {
        if (errors.Total != 0) throw new InvalidOperationException("Workload có client báo lỗi.");
        foreach (var client in clients)
        {
            ClientState state = client.ReadState();
            if (!state.Connected || !state.Ready)
                throw new InvalidOperationException("Chưa đủ số client đã kết nối và Ready.");
            if (rounds.TryGetValue(client, out ClientState expected) &&
                (state.RoomId != expected.RoomId || state.ReadyInRoom != expected.ReadyInRoom ||
                    (!allowTransitions && (!IsPlaying(state) || !state.Alive || state.RoundId != expected.RoundId))))
                throw new InvalidOperationException($"Slot {state.Id} mất trạng thái của round yêu cầu.");
        }
    }

    private static bool IsPlaying(ClientState state) =>
        state.Connected && state.Ready && state.HasRoundData && state.Phase == MatchPhase.Playing;
}
