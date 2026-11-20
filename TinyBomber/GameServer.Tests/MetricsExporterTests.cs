using System.Diagnostics;
using System.Globalization;
using System.Text;
using GameShared;

namespace GameServer.Tests;

public sealed class MetricsExporterTests
{
    private static ServerMetricsSnapshot Snapshot(long id = 1, int roomCount = 1)
    {
        var rooms = Enumerable.Range(1, roomCount).Select(room =>
            new RoomMetrics(room, MatchPhase.Playing, 3, 2, 2, 2, 1));
        var collector = new ServerMetrics(0);
        long now = Stopwatch.GetTimestamp();
        return collector.Publish(now, new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.FromHours(7)),
            3, 2, rooms, 4, now, null) with { SampleId = id, WindowSeconds = 1.25 };
    }

    private static Dictionary<string, string> ServerRow(string row) =>
        MetricsExporter.ServerCsvHeader.Split(',').Zip(ParseCsv(row)).ToDictionary(pair => pair.First, pair => pair.Second);

    private static string[] ParseCsv(string row)
    {
        var result = new List<string>();
        var cell = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < row.Length; i++)
        {
            char ch = row[i];
            if (ch == '"')
            {
                if (quoted && i + 1 < row.Length && row[i + 1] == '"') { cell.Append('"'); i++; }
                else quoted = !quoted;
            }
            else if (ch == ',' && !quoted) { result.Add(cell.ToString()); cell.Clear(); }
            else cell.Append(ch);
        }
        result.Add(cell.ToString());
        return result.ToArray();
    }

    [Fact]
    public void FormattingUsesSameSampleAndDistinguishesMissingValuesFromZero()
    {
        var sample = Snapshot(42);
        var row = ServerRow(MetricsExporter.FormatServerCsv(sample, 7));
        string console = MetricsExporter.FormatConsole(sample, 7);
        Assert.Equal(MetricsExporter.ServerCsvHeader.Split(',').Length, ParseCsv(MetricsExporter.FormatServerCsv(sample, 7)).Length);
        Assert.Equal("42", row["sample_id"]);
        Assert.Contains("sample=42", console);
        Assert.Contains("connections=3", console);
        Assert.Contains("ready=2", console);
        Assert.Contains("queue=4", console);
        Assert.Contains("dropped=7", console);
        Assert.Equal("3", row["connections"]);
        Assert.Equal("7", row["dropped_snapshots"]);
        Assert.Equal("", row["tick_rate_per_second"]);
        Assert.Contains("tick/s=N/A", console);
        Assert.Equal("", row["cpu_percent_total_logical_capacity"]);
        var withZeros = sample with { TickRate = 0 };
        Assert.Equal("0", ServerRow(MetricsExporter.FormatServerCsv(withZeros, 0))["tick_rate_per_second"]);
        Assert.Contains("tick/s=0", MetricsExporter.FormatConsole(withZeros, 0));
        string[] room = ParseCsv(MetricsExporter.FormatRoomCsv(sample, sample.Rooms[0]));
        Assert.Equal(row["sample_id"], room[0]);
        Assert.Equal(row["timestamp_utc"], room[1]);
        Assert.EndsWith("+00:00", room[1]);
    }

    [Theory]
    [InlineData("vi-VN")]
    [InlineData("fr-FR")]
    public void CsvUsesInvariantCultureAndEscapesText(string culture)
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var sample = Snapshot() with
            {
                TickRate = 29.5,
                Process = new ProcessMetricsSample(0, DateTimeOffset.UtcNow, 0, 0, 12, 0.25,
                    "lỗi, \"ổ đĩa\"\r\nchi tiết")
            };
            string csv = MetricsExporter.FormatServerCsv(sample, 0);
            var row = ServerRow(csv);
            Assert.Equal("1.25", row["window_seconds"]);
            Assert.Equal("29.5", row["tick_rate_per_second"]);
            Assert.Equal("0.25", row["process_query_ms"]);
            Assert.Equal(sample.Process.Error, row["process_error"]);
            Assert.Contains("\"lỗi, \"\"ổ đĩa\"\"\r\nchi tiết\"", csv);
            Assert.Contains("tick/s=29.5", MetricsExporter.FormatConsole(sample, 0));
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [Fact]
    public async Task NormalShutdownFlushesClosesAndCreatesSeparateUtf8FilesForEachRun()
    {
        string directory = Path.Combine(Path.GetTempPath(), "TinyBomberMetricsTests_" + Guid.NewGuid().ToString("N"));
        using var console = new StringWriter();
        try
        {
            var exporter = new MetricsExporter(directory, console);
            var first = Snapshot(1, 6);
            Assert.True(exporter.TryEnqueue(first));
            Assert.True(exporter.TryEnqueue(first));
            Assert.True(exporter.TryEnqueue(Snapshot(2)));
            var stop = await exporter.StopAsync();
            Assert.True(stop.Completed);
            Assert.True(stop.Status.CsvFilesClosed);
            Assert.Null(stop.Status.CsvError);
            Assert.Equal(0, stop.Status.PendingSnapshots);
            Assert.Equal(2, stop.Status.LastCsvSampleId);
            string[] server = File.ReadAllLines(exporter.ServerCsvPath);
            string[] rooms = File.ReadAllLines(exporter.RoomCsvPath);
            Assert.Equal(3, server.Length);
            Assert.Equal(8, rooms.Length);
            Assert.Equal(MetricsExporter.ServerCsvHeader, server[0]);
            Assert.Equal(MetricsExporter.RoomCsvHeader, rooms[0]);
            Assert.Equal(6, rooms.Skip(1).Select(ParseCsv).Count(row => row[0] == "1"));
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, File.ReadAllBytes(exporter.ServerCsvPath).Take(3));
            using (File.Open(exporter.ServerCsvPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            using (File.Open(exporter.RoomCsvPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            Assert.Contains("rooms_hidden=2", console.ToString());
            Assert.DoesNotContain("phòng 5 ", console.ToString());
            Assert.Equal(2, console.ToString().Split("[METRICS] sample=").Length - 1);
            var nextRun = new MetricsExporter(directory, TextWriter.Null);
            Assert.True((await nextRun.StopAsync()).Completed);
            Assert.NotEqual(exporter.ServerCsvPath, nextRun.ServerCsvPath);
            Assert.Equal(4, Directory.GetFiles(directory).Length);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                foreach (string path in Directory.GetFiles(directory)) File.Delete(path);
                Directory.Delete(directory);
            }
        }
    }

    [Fact]
    public async Task FullQueueDropsNewestWithoutWaitingAndPreservesSampleIdGap()
    {
        using var console = new BlockingWriter();
        using var server = new StringWriter();
        using var rooms = new StringWriter();
        var exporter = new MetricsExporter("unused", console, () => (server, rooms));
        try
        {
            Assert.True(exporter.TryEnqueue(Snapshot(1)));
            Assert.True(console.Entered.Wait(TimeSpan.FromSeconds(5)));
            for (int id = 2; id <= 65; id++) Assert.True(exporter.TryEnqueue(Snapshot(id)));
            Assert.False(exporter.TryEnqueue(Snapshot(66)));
            Assert.Equal(1, exporter.ReadStatus().DroppedSnapshots);
            Assert.Equal(65, exporter.ReadStatus().PendingSnapshots);
            console.Release.Set();
            await WaitUntil(() => exporter.ReadStatus().PendingSnapshots == 0);
            Assert.True(exporter.TryEnqueue(Snapshot(67)));
            var result = await exporter.StopAsync();
            Assert.True(result.Completed);
            var lines = server.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            var ids = lines.Skip(1).Select(row => ServerRow(row)["sample_id"]).ToArray();
            Assert.DoesNotContain("66", ids);
            Assert.Equal("67", ids[^1]);
            Assert.Contains("dropped=1", console.ToString());
        }
        finally
        {
            console.Release.Set();
            await exporter.StopAsync();
        }
    }

    [Fact]
    public async Task ShutdownWithBlockedWorkerReportsTimeoutAndOutstandingData()
    {
        using var console = new BlockingWriter();
        using var server = new StringWriter();
        using var rooms = new StringWriter();
        var exporter = new MetricsExporter("unused", console, () => (server, rooms));
        try
        {
            exporter.TryEnqueue(Snapshot());
            Assert.True(console.Entered.Wait(TimeSpan.FromSeconds(5)));
            exporter.TryEnqueue(Snapshot(2));
            var result = await exporter.StopAsync(TimeSpan.FromMilliseconds(20));
            Assert.False(result.Completed);
            Assert.Equal(2, result.Status.PendingSnapshots);
            Assert.False(result.Status.CsvFilesClosed);
            string report = MetricsExporter.FormatStopResult(result);
            Assert.Contains("TIMEOUT", report);
            Assert.Contains("pending=2", report);
            Assert.Contains("chưa xác nhận flush/đóng", report);
        }
        finally
        {
            console.Release.Set();
            var drained = await exporter.StopAsync();
            Assert.True(drained.Completed);
            Assert.True(drained.Status.CsvFilesClosed);
            Assert.Equal(0, drained.Status.PendingSnapshots);
        }
    }

    [Fact]
    public async Task ConsoleFailureLeavesCsvOperational()
    {
        using var console = new FailingWriter();
        using var server = new StringWriter();
        using var rooms = new StringWriter();
        var exporter = new MetricsExporter("unused", console, () => (server, rooms));
        exporter.TryEnqueue(Snapshot());
        exporter.TryEnqueue(Snapshot(2));
        var result = await exporter.StopAsync();
        Assert.True(result.Completed);
        Assert.Contains("controlled", result.Status.ConsoleError);
        Assert.Null(result.Status.CsvError);
        Assert.True(result.Status.CsvFilesClosed);
        Assert.Equal(2, result.Status.LastCsvSampleId);
        Assert.Contains("controlled", server.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShutdownReportsTimeoutDuringCsvFlushOrClose(bool blockDispose)
    {
        using var server = new BlockingFinalizationWriter(blockDispose);
        using var rooms = new StringWriter();
        var exporter = new MetricsExporter("unused", TextWriter.Null, () => (server, rooms));
        try
        {
            // Headers also need final flush/close when no snapshot has been queued.
            Task<MetricsExporterStopResult> stopping = exporter.StopAsync(TimeSpan.FromMilliseconds(100));
            Assert.True(server.Entered.Wait(TimeSpan.FromSeconds(5)));
            var result = await stopping;
            Assert.False(result.Completed);
            Assert.Equal(0, result.Status.PendingSnapshots);
            Assert.False(result.Status.CsvFilesClosed);
            Assert.Contains("TIMEOUT", MetricsExporter.FormatStopResult(result));
            Assert.Contains("chưa xác nhận flush/đóng", MetricsExporter.FormatStopResult(result));
        }
        finally
        {
            server.Release.Set();
            var completed = await exporter.StopAsync();
            Assert.True(completed.Completed);
            Assert.True(completed.Status.CsvFilesClosed);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CsvOpenOrWriteFailureLeavesConsoleOperational(bool failOpen)
    {
        using var console = new StringWriter();
        using var server = new FailingWriter();
        using var rooms = new StringWriter();
        var exporter = new MetricsExporter("unused", console, () =>
            failOpen ? throw new IOException("controlled open failure") : (server, rooms));
        exporter.TryEnqueue(Snapshot());
        exporter.TryEnqueue(Snapshot(2));
        var result = await exporter.StopAsync();
        Assert.True(result.Completed);
        Assert.Contains("controlled", result.Status.CsvError);
        Assert.Null(result.Status.ConsoleError);
        Assert.Equal(2, result.Status.LastConsoleSampleId);
        Assert.Contains("sample=2", console.ToString());
        Assert.Contains("csv_error=", console.ToString());
        Assert.Contains("CSV=lỗi", MetricsExporter.FormatStopResult(result));
    }

    [Fact]
    public void CollectorAssignsIncreasingSampleIds()
    {
        var metrics = new ServerMetrics(0);
        long now = Stopwatch.GetTimestamp();
        var first = metrics.Publish(now, DateTimeOffset.UtcNow, 0, 0, Array.Empty<RoomMetrics>(), 0, now, null);
        var second = metrics.Publish(now + 1, DateTimeOffset.UtcNow, 0, 0, Array.Empty<RoomMetrics>(), 0, now, null);
        Assert.Equal(1, first.SampleId);
        Assert.Equal(2, second.SampleId);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        long started = Stopwatch.GetTimestamp();
        while (!condition())
        {
            if (Stopwatch.GetElapsedTime(started).TotalSeconds > 5) throw new TimeoutException();
            await Task.Delay(5);
        }
    }

    private sealed class FailingWriter : StringWriter
    {
        public override void WriteLine(string? value) => throw new IOException("controlled write failure");
    }

    private sealed class BlockingWriter : StringWriter
    {
        internal ManualResetEventSlim Entered { get; } = new();
        internal ManualResetEventSlim Release { get; } = new();
        public override void WriteLine(string? value)
        {
            Entered.Set();
            if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("controlled block");
            base.WriteLine(value);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Release.Set();
                Entered.Dispose();
                Release.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    private sealed class BlockingFinalizationWriter(bool blockDispose) : StringWriter
    {
        private bool disposed;
        internal ManualResetEventSlim Entered { get; } = new();
        internal ManualResetEventSlim Release { get; } = new();
        private void Block()
        {
            Entered.Set();
            if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("controlled finalization block");
        }
        public override void Flush()
        {
            if (!blockDispose) Block();
            base.Flush();
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && !disposed)
            {
                if (blockDispose) Block();
                disposed = true;
                Entered.Dispose();
                Release.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
