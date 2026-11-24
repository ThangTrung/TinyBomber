using System.Globalization;
using System.Text;
using System.Threading.Channels;

namespace GameServer;

internal sealed record MetricsExporterStatus(
    long DroppedSnapshots, long PendingSnapshots, long LastConsoleSampleId,
    long LastCsvSampleId, bool CsvFilesClosed, string? ConsoleError, string? CsvError);

internal sealed record MetricsExporterStopResult(bool Completed, MetricsExporterStatus Status);

internal sealed class MetricsExporter
{
    internal const int QueueCapacity = 64;
    private const int ConsoleRoomLimit = 4;
    private readonly Channel<ServerMetricsSnapshot> queue = Channel.CreateBounded<ServerMetricsSnapshot>(
        new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false
        });
    private readonly TextWriter console;
    private readonly Func<(TextWriter Server, TextWriter Rooms)> openCsv;
    private readonly Task worker;
    private long dropped;
    private long pending;
    private long lastConsoleSampleId;
    private long lastCsvSampleId;
    private int csvFilesClosed;
    private string? consoleError;
    private string? csvError;

    internal string ServerCsvPath { get; }
    internal string RoomCsvPath { get; }

    internal MetricsExporter(string directory, TextWriter? console = null,
        Func<(TextWriter Server, TextWriter Rooms)>? openCsv = null)
    {
        this.console = console ?? Console.Out;
        string stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
        string runId = $"{stamp}_{Environment.ProcessId}_{Guid.NewGuid():N}";
        ServerCsvPath = Path.Combine(directory, $"server_metrics_{runId}.csv");
        RoomCsvPath = Path.Combine(directory, $"room_metrics_{runId}.csv");
        this.openCsv = openCsv ?? OpenCsv;
        worker = Task.Run(RunAsync);
    }

    // Pending includes the snapshot currently being written plus the bounded queue.
    // This method performs no I/O and never waits for queue capacity.
    internal bool TryEnqueue(ServerMetricsSnapshot snapshot)
    {
        Interlocked.Increment(ref pending);
        if (queue.Writer.TryWrite(snapshot)) return true;
        Interlocked.Decrement(ref pending);
        Interlocked.Increment(ref dropped);
        return false;
    }

    internal MetricsExporterStatus ReadStatus() => new(
        Interlocked.Read(ref dropped), Interlocked.Read(ref pending),
        Interlocked.Read(ref lastConsoleSampleId), Interlocked.Read(ref lastCsvSampleId),
        Volatile.Read(ref csvFilesClosed) != 0, Volatile.Read(ref consoleError), Volatile.Read(ref csvError));

    internal async Task<MetricsExporterStopResult> StopAsync(TimeSpan? timeout = null)
    {
        queue.Writer.TryComplete();
        try
        {
            await worker.WaitAsync(timeout ?? TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            return new MetricsExporterStopResult(true, ReadStatus());
        }
        catch (TimeoutException)
        {
            // The worker retains ownership of its writers and can finish if blocked I/O resumes.
            return new MetricsExporterStopResult(false, ReadStatus());
        }
    }

    internal static string FormatStopResult(MetricsExporterStopResult result)
    {
        var status = result.Status;
        string csv = status.CsvError != null ? $"lỗi {status.CsvError}"
            : status.CsvFilesClosed ? "đã flush/đóng" : "chưa xác nhận flush/đóng";
        return $"[METRICS] shutdown={(result.Completed ? "worker hoàn tất" : "TIMEOUT khi chờ exporter")} " +
            $"pending={status.PendingSnapshots} dropped={status.DroppedSnapshots} CSV={csv} " +
            $"console_error={status.ConsoleError ?? "none"}";
    }

    private (TextWriter Server, TextWriter Rooms) OpenCsv()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ServerCsvPath)!);
        var server = new StreamWriter(new FileStream(ServerCsvPath, FileMode.CreateNew,
            FileAccess.Write, FileShare.Read), new UTF8Encoding(true));
        try
        {
            var rooms = new StreamWriter(new FileStream(RoomCsvPath, FileMode.CreateNew,
                FileAccess.Write, FileShare.Read), new UTF8Encoding(true));
            return (server, rooms);
        }
        catch
        {
            server.Dispose();
            throw;
        }
    }

    private async Task RunAsync()
    {
        TextWriter? server = null;
        TextWriter? rooms = null;
        try
        {
            try
            {
                (server, rooms) = openCsv();
                server.WriteLine(ServerCsvHeader);
                rooms.WriteLine(RoomCsvHeader);
            }
            catch (Exception ex) { RecordCsvError(ex); }

            long? lastSampleId = null;
            await foreach (var snapshot in queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    if (lastSampleId.HasValue && snapshot.SampleId <= lastSampleId) continue;
                    long droppedAtWrite = Interlocked.Read(ref dropped);
                    if (consoleError == null)
                    {
                        try
                        {
                            console.WriteLine(FormatConsole(snapshot, droppedAtWrite, csvError));
                            Interlocked.Exchange(ref lastConsoleSampleId, snapshot.SampleId);
                        }
                        catch (Exception ex)
                        {
                            Volatile.Write(ref consoleError, Describe(ex));
                        }
                    }
                    if (csvError == null)
                    {
                        try
                        {
                            foreach (var room in snapshot.Rooms)
                                rooms!.WriteLine(FormatRoomCsv(snapshot, room));
                            rooms!.Flush();
                            // The summary follows all room rows for this sample.
                            server!.WriteLine(FormatServerCsv(snapshot, droppedAtWrite, consoleError, csvError));
                            server.Flush();
                            Interlocked.Exchange(ref lastCsvSampleId, snapshot.SampleId);
                        }
                        catch (Exception ex) { RecordCsvError(ex); }
                    }
                    lastSampleId = snapshot.SampleId;
                }
                finally { Interlocked.Decrement(ref pending); }
            }
        }
        finally
        {
            try
            {
                if (consoleError == null) console.Flush();
            }
            catch (Exception ex) { Volatile.Write(ref consoleError, Describe(ex)); }
            bool serverClosed = CloseCsv(server);
            bool roomsClosed = CloseCsv(rooms);
            if (serverClosed && roomsClosed) Volatile.Write(ref csvFilesClosed, 1);
        }
    }

    private bool CloseCsv(TextWriter? writer)
    {
        if (writer == null) return false;
        bool success = true;
        try { writer.Flush(); }
        catch (Exception ex) { success = false; RecordCsvError(ex); }
        try { writer.Dispose(); }
        catch (Exception ex) { success = false; RecordCsvError(ex); }
        return success;
    }

    private void RecordCsvError(Exception error) =>
        Interlocked.CompareExchange(ref csvError, Describe(error), null);

    private static string Describe(Exception error) => $"{error.GetType().Name}: {error.Message}";
    private static string Number(double? value) => value?.ToString("0.###", CultureInfo.InvariantCulture) ?? "N/A";

    internal static string FormatConsole(ServerMetricsSnapshot s, long dropped, string? csvError = null)
    {
        var p = s.Process;
        var r = s.Network?.Rates;
        string roomList = string.Join(" | ", s.Rooms.Take(ConsoleRoomLimit).Select(room =>
            $"phòng {room.Id} {room.Phase} members={room.Members} active={room.ActivePlayers} alive={room.AlivePlayers}"));
        return $"[METRICS] sample={s.SampleId} utc={s.TimestampUtc.ToUniversalTime():O} " +
            $"connections={s.ConnectedClients} ready={s.ReadyClients} members={s.RoomMembers} " +
            $"participants={s.RoundParticipants} active={s.ActivePlayers} alive={s.AlivePlayers} " +
            $"rooms={s.RoomCount} waiting/playing/finished={s.WaitingRooms}/{s.PlayingRooms}/{s.FinishedRooms} " +
            $"queue={s.WaitingActions} tick/s={Number(s.TickRate)} interval_ms={Number(s.TickIntervalMs.Average)} " +
            $"gameplay_ms={Number(s.GameplayWorkMs.Average)} non_sleep_wall_ms={Number(s.TickNonSleepWallMs.Average)} " +
            $"cpu_percent_total={Number(p?.CpuPercent)} ram_MiB={Number(p?.WorkingSetBytes / 1048576.0)} " +
            $"tcp_rx/tx_Bps={Number(r?.TcpReceivedBytesPerSecond)}/{Number(r?.TcpCompletedWriteBytesPerSecond)} " +
            $"udp_rx/tx_Bps={Number(r?.UdpReceivedBytesPerSecond)}/{Number(r?.UdpCompletedSendBytesPerSecond)} " +
            $"dropped={dropped} rooms_hidden={Math.Max(0, s.RoomCount - ConsoleRoomLimit)} " +
            $"process_error={p?.Error ?? "none"} csv_error={csvError ?? "none"}" +
            (roomList.Length == 0 ? "" : $" | {roomList}");
    }

    internal const string RoomCsvHeader =
        "sample_id,timestamp_utc,room_id,phase,members,ready_clients,round_participants,active_players,alive_players,round_id";

    internal static readonly string ServerCsvHeader =
        "sample_id,timestamp_utc,window_seconds,connections,ready_clients,rooms,waiting_rooms,playing_rooms,finished_rooms," +
        "room_members,round_participants,active_players,alive_players,waiting_actions,largest_action_batch,successful_actions,failed_actions," +
        "action_samples,action_avg_ms,action_max_ms,room_update_samples,room_update_avg_ms,room_update_max_ms," +
        "gameplay_work_samples,gameplay_work_avg_ms,gameplay_work_max_ms,sleep_samples,sleep_avg_ms,sleep_max_ms," +
        "tick_interval_samples,tick_interval_avg_ms,tick_interval_max_ms,non_sleep_wall_samples,non_sleep_wall_avg_ms,non_sleep_wall_max_ms," +
        "tick_rate_per_second,snapshot_capture_ms,process_timestamp_utc,cpu_percent_total_logical_capacity,logical_processors," +
        "working_set_bytes,process_query_ms,process_sample_age_ms,process_error," +
        "tcp_received_bytes,tcp_completed_write_bytes,tcp_send_errors,udp_received_bytes,udp_completed_send_bytes,udp_send_errors," +
        "network_interval_seconds,tcp_received_bytes_per_second,tcp_completed_write_bytes_per_second," +
        "udp_received_bytes_per_second,udp_completed_send_bytes_per_second,dropped_snapshots,console_error,csv_error," +
        "tcp_queue_depth,tcp_queue_high_water,tcp_in_flight,heartbeat_rtt_samples,heartbeat_rtt_total_ms," +
        "private_bytes,managed_bytes,gc_gen0,gc_gen1,gc_gen2,threads,handles,requested_sleep_avg_ms," +
        "sleep_overshoot_avg_ms,deadline_lateness_avg_ms,work_overruns,interval_overruns,tick_intervals_ms," +
        string.Join(",", EventMetrics.Names);

    internal static string FormatServerCsv(ServerMetricsSnapshot s, long dropped,
        string? consoleError = null, string? csvError = null)
    {
        var p = s.Process;
        var n = s.Network?.Totals;
        var r = s.Network?.Rates;
        return CsvRow(s.SampleId, s.TimestampUtc, s.WindowSeconds, s.ConnectedClients, s.ReadyClients,
            s.RoomCount, s.WaitingRooms, s.PlayingRooms, s.FinishedRooms, s.RoomMembers, s.RoundParticipants,
            s.ActivePlayers, s.AlivePlayers, s.WaitingActions, s.LargestActionBatch, s.SuccessfulActions, s.FailedActions,
            s.ActionProcessingMs.Samples, s.ActionProcessingMs.Average, s.ActionProcessingMs.Maximum,
            s.RoomUpdateMs.Samples, s.RoomUpdateMs.Average, s.RoomUpdateMs.Maximum,
            s.GameplayWorkMs.Samples, s.GameplayWorkMs.Average, s.GameplayWorkMs.Maximum,
            s.SleepElapsedMs.Samples, s.SleepElapsedMs.Average, s.SleepElapsedMs.Maximum,
            s.TickIntervalMs.Samples, s.TickIntervalMs.Average, s.TickIntervalMs.Maximum,
            s.TickNonSleepWallMs.Samples, s.TickNonSleepWallMs.Average, s.TickNonSleepWallMs.Maximum,
            s.TickRate, s.SnapshotCaptureMs, p?.TimestampUtc, p?.CpuPercent, p?.LogicalProcessors,
            p?.WorkingSetBytes, p?.QueryMs, s.ProcessSampleAgeMs, p?.Error,
            n?.TcpReceivedBytes, n?.TcpCompletedWriteBytes, n?.TcpSendErrors,
            n?.UdpReceivedBytes, n?.UdpCompletedSendBytes, n?.UdpSendErrors,
            s.Network?.IntervalSeconds, r?.TcpReceivedBytesPerSecond, r?.TcpCompletedWriteBytesPerSecond,
            r?.UdpReceivedBytesPerSecond, r?.UdpCompletedSendBytesPerSecond, dropped, consoleError, csvError,
            s.Transport?.Pending, s.Transport?.HighWater, s.Transport?.InFlight, s.Transport?.HeartbeatSamples, s.Transport?.HeartbeatTotalMs,
            p?.Resources?.PrivateBytes, p?.Resources?.ManagedBytes, p?.Resources?.Gen0, p?.Resources?.Gen1, p?.Resources?.Gen2,
            p?.Resources?.Threads, p?.Resources?.Handles, s.Timing?.RequestedSleepAvgMs, s.Timing?.SleepOvershootAvgMs,
            s.Timing?.DeadlineLatenessAvgMs, s.Timing?.WorkOverruns, s.Timing?.IntervalOverruns,
            s.Timing == null ? null : string.Join("|", s.Timing.IntervalsMs.Select(ms => ms.ToString("F4", CultureInfo.InvariantCulture)))) +
            "," + string.Join(",", EventMetrics.Names.Select(name => s.Events?.GetValueOrDefault(name.ToString()).ToString(CultureInfo.InvariantCulture)));
    }

    internal static string FormatRoomCsv(ServerMetricsSnapshot s, RoomMetrics room) =>
        CsvRow(s.SampleId, s.TimestampUtc, room.Id, room.Phase, room.Members, room.ReadyClients,
            room.RoundParticipants, room.ActivePlayers, room.AlivePlayers, room.RoundId);

    private static string CsvRow(params object?[] values) => string.Join(",", values.Select(value =>
    {
        string text = value switch
        {
            null => "",
            DateTimeOffset time => time.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString()!
        };
        return text.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0
            ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
    }));
}
