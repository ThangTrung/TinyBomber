using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace GameServer.Monitoring;

internal sealed class MonitoringDashboard : IAsyncDisposable
{
    private readonly WebApplication host;
    private MonitoringDashboard(WebApplication app) => host = app;
    internal string Address => host.Urls.Single();

    internal static async Task<MonitoringDashboard?> StartAsync(int port, long serverStarted,
        Func<ServerMetricsSnapshot?> latest, Func<MetricsExporterStatus?> exporter, Func<bool> captureFailed)
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, port));
        builder.Services.AddRouting();
        var app = builder.Build();
        try
        {
            var runId = Guid.NewGuid().ToString("N");
            MapAsset("/", "index.html", "text/html; charset=utf-8");
            MapAsset("/monitor.css", "monitor.css", "text/css; charset=utf-8");
            MapAsset("/monitor.js", "monitor.js", "text/javascript; charset=utf-8");
            app.MapGet("/api/monitor", (HttpContext context) =>
            {
                var sample = latest();
                var diagnostics = exporter();
                context.Response.Headers.CacheControl = "no-store";
                return Results.Json(new
                {
                    runId, uptimeSeconds = Stopwatch.GetElapsedTime(serverStarted).TotalSeconds,
                    state = sample == null ? "starting" : "running", sampleId = sample?.SampleId,
                    sampleUtc = sample?.TimestampUtc, windowSeconds = sample?.WindowSeconds,
                    metrics = sample == null ? null : Project(sample),
                    rooms = sample?.Rooms.Select(room => new { room.Id, room.RoundId, phase = room.Phase.ToString(),
                        room.Members, room.ReadyClients, room.ActivePlayers, room.AlivePlayers, room.RoundParticipants }),
                    diagnostics = new { observedUtc = DateTimeOffset.UtcNow, captureFailed = captureFailed(),
                        droppedSnapshots = diagnostics?.DroppedSnapshots, pendingSnapshots = diagnostics?.PendingSnapshots, lastCsvSampleId = diagnostics?.LastCsvSampleId,
                        csvFailed = diagnostics?.CsvError != null, consoleFailed = diagnostics?.ConsoleError != null }
                });
            });
            await app.StartAsync();
            return new MonitoringDashboard(app);
        }
        catch (Exception e) when (e is IOException or SocketException)
        {
            await app.DisposeAsync();
            Console.Error.WriteLine($"[MONITOR] Loopback port {port} unavailable ({e.GetType().Name}). GameServer continues.");
            return null;
        }
        catch { await app.DisposeAsync(); throw; }

        void MapAsset(string route, string name, string contentType)
        {
            using var input = typeof(MonitoringDashboard).Assembly.GetManifestResourceStream("TinyBomber.Monitor." + name)
                ?? throw new InvalidOperationException($"Missing embedded monitor asset {name}.");
            using var memory = new MemoryStream();
            input.CopyTo(memory);
            var bytes = memory.ToArray();
            app.MapGet(route, () => Results.Bytes(bytes, contentType));
        }
    }

    // Only numeric/enum data from the published sample. Process/exporter exception text can contain local paths.
    private static object Project(ServerMetricsSnapshot s) => new
    {
        s.ConnectedClients, s.ReadyClients, s.RoomCount, s.PlayingRooms, s.ActivePlayers, s.AlivePlayers,
        s.TickRate, intervalMs = s.TickIntervalMs.Average, gameplayMs = s.GameplayWorkMs.Average,
        p95Ms = s.Timing?.P95Ms, p99Ms = s.Timing?.P99Ms, s.WaitingActions, s.LargestActionBatch, s.FailedActions,
        s.SnapshotCaptureMs, s.Transport, resources = s.Process?.Resources,
        cpuPercent = s.Process?.CpuPercent, workingSetBytes = s.Process?.WorkingSetBytes, network = s.Network?.Rates, totals = s.Network?.Totals, s.Events
    };

    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await host.StopAsync(timeout.Token); }
        finally { await host.DisposeAsync(); }
    }
}

