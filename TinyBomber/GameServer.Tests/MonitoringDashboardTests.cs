using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Diagnostics;
using GameServer.Monitoring;
using GameShared;

namespace GameServer.Tests;

[Collection("Network metrics server state")]
public sealed class MonitoringDashboardTests
{
    [Fact]
    public void OnlyExplicitValidPortEnablesDashboard()
    {
        Assert.True(Program.TryParseOptions([], out int limit, out int? port));
        Assert.Null(port);
        Assert.Equal(8, limit);
        Assert.True(Program.TryParseOptions(["--dashboard-port","8080","--max-connections","4"], out limit, out port));
        Assert.Equal(8080, port);
        Assert.Equal(4, limit);
        foreach (string[] args in new[] { new[] {"--dashboard-port","0"}, ["--dashboard-port","65536"],
            ["--dashboard-port","8080","--dashboard-port","8081"], ["--dashboard-port"] })
            Assert.False(Program.TryParseOptions(args, out _, out _));
    }

    [Fact]
    public async Task AmbientEndpointConfigurationCannotAddAListener()
    {
        const string setting = "Kestrel__Endpoints__Unexpected__Url";
        string? original = Environment.GetEnvironmentVariable(setting);
        using var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Start();
        try
        {
            Environment.SetEnvironmentVariable(setting, $"http://127.0.0.1:{((IPEndPoint)occupied.LocalEndpoint).Port}");
            await using var web = Assert.IsType<MonitoringDashboard>(await MonitoringDashboard.StartAsync(
                0, Stopwatch.GetTimestamp(), () => null, () => null, () => false));
            var endpoint = new Uri(web.Address);
            Assert.Equal("127.0.0.1", endpoint.Host);
            Assert.NotEqual(((IPEndPoint)occupied.LocalEndpoint).Port, endpoint.Port);
        }
        finally { Environment.SetEnvironmentVariable(setting, original); }
    }

    [Fact]
    public async Task ReadsPublishedSnapshotOnceAndServesOnlyEmbeddedAssets()
    {
        var metrics = new ServerMetrics(0);
        var network = new NetworkMetrics();
        int reads = 0;
        var timer = Stopwatch.GetTimestamp();
        await using var app = Assert.IsType<MonitoringDashboard>(await MonitoringDashboard.StartAsync(0, timer,
            () => { Interlocked.Increment(ref reads); return metrics.Latest; }, () => null, () => false));
        using var client = new HttpClient { BaseAddress = new Uri(app.Address), Timeout = TimeSpan.FromSeconds(5) };
        Assert.Equal("127.0.0.1", client.BaseAddress.Host);
        using var initial = await client.GetAsync("/api/monitor");
        Assert.True(initial.Headers.CacheControl?.NoStore);
        var starting = JsonDocument.Parse(await initial.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("starting", starting.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, starting.GetProperty("metrics").ValueKind);
        foreach (string asset in new[] {"/","/monitor.css","/monitor.js"})
            Assert.NotEmpty(await client.GetStringAsync(asset));
        using var denied = await client.PostAsync("/api/monitor", null);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, denied.StatusCode);

        network.RecordTcpReceive(100);
        var sample = network.Capture(timer);
        var snapshot = Publish(1);
        var row = MetricsExporter.FormatRoomCsv(snapshot,snapshot.Rooms.Single());
        var snapshotJson = await client.GetStringAsync("/api/monitor");
        Assert.DoesNotContain("private-test-error",snapshotJson);
        var json = JsonDocument.Parse(snapshotJson).RootElement;
        Assert.Equal(snapshot.SampleId,json.GetProperty("sampleId").GetInt64());
        Assert.Equal(snapshot.TimestampUtc,json.GetProperty("sampleUtc").GetDateTimeOffset());
        Assert.Equal("1",row.Split(',')[0]);
        Assert.Equal(1,json.GetProperty("rooms")[0].GetProperty("roundId").GetInt32());

        using var stop = new CancellationTokenSource();
        var publisher = Task.Run(async () =>
        {
            int id=2;
            while(!stop.IsCancellationRequested) { Publish(id++); await Task.Delay(1); }
        });
        try
        {
            await Task.WhenAll(Enumerable.Range(0,24).Select(async _ =>
            {
                var response=JsonDocument.Parse(await client.GetStringAsync("/api/monitor")).RootElement;
                Assert.Equal(response.GetProperty("sampleId").GetInt64(),response.GetProperty("rooms")[0].GetProperty("roundId").GetInt64());
            }));
        }
        finally { stop.Cancel();await publisher; }
        Assert.Equal(26,reads);
        network.RecordTcpReceive(100);
        Assert.Equal(100,network.Capture(timer+Stopwatch.Frequency).Rates.TcpReceivedBytesPerSecond);
        ServerMetricsSnapshot Publish(int id) => metrics.Publish(Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow,2,2,
            [new RoomMetrics(1,MatchPhase.Playing,2,2,2,2,2,id)],0,Stopwatch.GetTimestamp(),
            new ProcessMetricsSample(timer,DateTimeOffset.UtcNow,null,null,1,0,"private-test-error"),sample);
    }

    [Fact]
    public async Task BindFailureIsRecoverableAndDisposeReleasesListener()
    {
        var time=Stopwatch.GetTimestamp();
        Task<MonitoringDashboard?> Start(int port) => MonitoringDashboard.StartAsync(port,time,()=>null,()=>null,()=>false);
        int port;
        using (var listener=new TcpListener(IPAddress.Loopback,0))
        {
            listener.Start();
            port=((IPEndPoint)listener.LocalEndpoint).Port;
            Assert.Null(await Start(port));
        }
        await using (var web=Assert.IsType<MonitoringDashboard>(await Start(port)))
            Assert.Equal(port,new Uri(web.Address).Port);
        using var again = new TcpListener(IPAddress.Loopback,port);
        again.Start();
    }
}

