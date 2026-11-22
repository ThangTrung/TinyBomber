using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using GameShared;
using Xunit;

namespace GameServer.Tests;

// Socket lifecycle tests temporarily replace the server's shared UDP socket.
[CollectionDefinition("Network metrics server state", DisableParallelization = true)]
public sealed class NetworkMetricsCollection { }

[Collection("Network metrics server state")]
public sealed class NetworkMetricsTests
{
    private static long At(double seconds) => (long)(seconds * Stopwatch.Frequency);
    private static readonly FieldInfo UdpSocket = typeof(Server).GetField(
        "udpSocket", BindingFlags.Static | BindingFlags.NonPublic)!;

    [Fact]
    public void RatesUseTwoCumulativeSnapshotsAndMonotonicElapsedTime()
    {
        var metrics = new NetworkMetrics();
        metrics.RecordTcpReceive(100);
        var first = metrics.Capture(At(1));
        Assert.Null(first.IntervalSeconds);
        Assert.Equal(new NetworkByteRates(null, null, null, null), first.Rates);
        metrics.RecordTcpReceive(1000);
        metrics.RecordTcpWrite(2000);
        metrics.RecordUdpReceive(3000);
        metrics.RecordUdpSend(4000);
        metrics.RecordTcpSendError();
        metrics.RecordUdpSendError();
        metrics.ReadTotals();
        var second = metrics.Capture(At(3));
        Assert.Equal(2, second.IntervalSeconds);
        Assert.Equal(new NetworkByteRates(500, 1000, 1500, 2000), second.Rates);
        Assert.Equal(new NetworkTotals(1100, 2000, 1, 3000, 4000, 1), second.Totals);
        Assert.Equal(100, first.Totals.TcpReceivedBytes);
        Assert.Equal(new NetworkByteRates(0, 0, 0, 0), metrics.Capture(At(4)).Rates);
    }

    [Fact]
    public void InvalidSamplingIntervalHasNoRatesAndDoesNotAdvanceBaseline()
    {
        var metrics = new NetworkMetrics();
        metrics.Capture(At(2));
        metrics.RecordUdpSend(100);
        Assert.Null(metrics.Capture(At(2)).Rates.UdpCompletedSendBytesPerSecond);
        Assert.Null(metrics.Capture(At(1)).Rates.UdpCompletedSendBytesPerSecond);
        Assert.Equal(50, metrics.Capture(At(4)).Rates.UdpCompletedSendBytesPerSecond);
    }

    [Fact]
    public async Task ConcurrentWritersAndSnapshotsPreserveEveryIncrement()
    {
        var metrics = new NetworkMetrics();
        metrics.RecordTcpReceive(1);
        var old = metrics.Capture(Stopwatch.GetTimestamp());
        using var start = new ManualResetEventSlim();
        Task[] producers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            start.Wait();
            for (int i = 0; i < 10000; i++)
            {
                metrics.RecordTcpReceive(2);
                metrics.RecordTcpWrite(3);
                metrics.RecordTcpSendError();
                metrics.RecordUdpReceive(4);
                metrics.RecordUdpSend(5);
                metrics.RecordUdpSendError();
            }
        })).ToArray();
        Task done = Task.WhenAll(producers);
        var previous = old.Totals;
        start.Set();
        do
        {
            var snapshot = metrics.Capture(Stopwatch.GetTimestamp());
            Assert.True(snapshot.Totals.TcpReceivedBytes >= previous.TcpReceivedBytes);
            Assert.True(snapshot.Totals.TcpCompletedWriteBytes >= previous.TcpCompletedWriteBytes);
            Assert.True(snapshot.Totals.TcpSendErrors >= previous.TcpSendErrors);
            Assert.True(snapshot.Totals.UdpReceivedBytes >= previous.UdpReceivedBytes);
            Assert.True(snapshot.Totals.UdpCompletedSendBytes >= previous.UdpCompletedSendBytes);
            Assert.True(snapshot.Totals.UdpSendErrors >= previous.UdpSendErrors);
            previous = snapshot.Totals;
            await Task.Yield();
        } while (!done.IsCompleted);
        await done;
        Assert.Equal(new NetworkTotals(160001, 240000, 80000, 320000, 400000, 80000), metrics.ReadTotals());
        Assert.Equal(1, old.Totals.TcpReceivedBytes);
    }

    [Fact]
    public void ServerSnapshotEmbedsNetworkSampleWithoutRecalculatingIt()
    {
        var network = new NetworkMetrics();
        var sample = network.Capture(At(1));
        var metrics = new ServerMetrics(0);
        var snapshot = metrics.Publish(At(1), DateTimeOffset.UtcNow, 0, 0,
            Array.Empty<RoomMetrics>(), 0, Stopwatch.GetTimestamp(), null, sample);
        network.RecordTcpReceive(99);
        Assert.Same(sample, snapshot.Network);
        Assert.Equal(0, snapshot.Network!.Totals.TcpReceivedBytes);
        Assert.Null(snapshot.Network.Rates.TcpReceivedBytesPerSecond);
    }

    [Fact]
    public void UdpStartFailureIsCountedOnceWithoutCompletedBytes()
    {
        using var sender = new UdpClient();
        sender.Dispose();
        object? original = UdpSocket.GetValue(null);
        NetworkTotals before = Server.Network.ReadTotals();
        try
        {
            UdpSocket.SetValue(null, sender);
            using var packet = new Packet(new byte[10]);
            Server.SendUDPData(new IPEndPoint(IPAddress.Loopback, 12345), packet);
            Assert.Equal(before.UdpSendErrors + 1, Server.Network.ReadTotals().UdpSendErrors);
            Assert.Equal(before.UdpCompletedSendBytes, Server.Network.ReadTotals().UdpCompletedSendBytes);
        }
        finally { UdpSocket.SetValue(null, original); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UdpCompletionKeepsOriginalSocketAndDoesNotTouchReusedSlot(bool failEnd)
    {
        using var sender = new UdpClient();
        using var replacement = new UdpClient();
        object? original = UdpSocket.GetValue(null);
        const int slotId = int.MaxValue;
        var slot = new Client(slotId);
        Server.clients.Add(slotId, slot);
        NetworkTotals before = Server.Network.ReadTotals();
        try
        {
            UdpSocket.SetValue(null, sender);
            using var recipient = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            byte[] payload = new byte[23];
            IAsyncResult operation = sender.BeginSend(payload, payload.Length,
                (IPEndPoint)recipient.Client.LocalEndPoint!, null, (sender, Server.Network));
            using var completed = operation.AsyncWaitHandle;
            Assert.True(completed.WaitOne(TimeSpan.FromSeconds(5)));
            Server.Disconnect(slotId, slot.tcp);
            var newTcp = slot.tcp;
            var newUdp = slot.udp;
            UdpSocket.SetValue(null, replacement);
            if (failEnd) sender.Dispose();
            typeof(Server).GetMethod("UdpSendCallback", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, new object[] { operation });
            NetworkTotals after = Server.Network.ReadTotals();
            Assert.Same(newTcp, slot.tcp);
            Assert.Same(newUdp, slot.udp);
            Assert.Equal(before.UdpSendErrors + (failEnd ? 1 : 0), after.UdpSendErrors);
            Assert.Equal(before.UdpCompletedSendBytes + (failEnd ? 0 : 23), after.UdpCompletedSendBytes);
        }
        finally
        {
            UdpSocket.SetValue(null, original);
            Server.clients.Remove(slotId);
        }
    }

    [Fact]
    public void ReplayRetainsServerLifetimeCounters()
    {
        var counters = Server.Network;
        var before = counters.ReadTotals();
        var room = new GameLogic(123);
        var first = new Client(1) { Username = "A", UdpConfirmed = true };
        var second = new Client(2) { Username = "B", UdpConfirmed = true };
        room.TryAddMember(first);
        room.TryAddMember(second);
        room.StartRound(first, room.Id, 0);
        first.player!.isAlive = false;
        room.UpdateRound(Stopwatch.GetTimestamp());
        room.StartRound(second, room.Id, room.RoundId);
        Assert.Same(counters, Server.Network);
        Assert.Equal(before, counters.ReadTotals());
    }

    [Fact]
    public async Task TcpWriteFailureCountsOnceAndDoesNotCountCompletedBytes()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var peer = new TcpClient();
        try
        {
            await peer.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
            using var accepted = await listener.AcceptTcpClientAsync();
            using var stream = new FailingNetworkStream(accepted.Client);
            const int slotId = int.MaxValue;
            var slot = new Client(slotId);
            var connection = slot.tcp;
            connection.socket = accepted;
            typeof(Client.TCP).GetField("stream", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(connection, stream);
            Server.clients.Add(slotId, slot);
            ThreadManager.Initialize();
            var before = Server.Network.ReadTotals();
            try
            {
                using var packet = new Packet(new byte[10]);
                connection.SendData(packet);
                Assert.Equal(before.TcpSendErrors + 1, Server.Network.ReadTotals().TcpSendErrors);
                Assert.Equal(before.TcpCompletedWriteBytes, Server.Network.ReadTotals().TcpCompletedWriteBytes);
            }
            finally
            {
                ThreadManager.UpdateMain();
                Server.clients.Remove(slotId);
            }
        }
        finally { listener.Stop(); }
    }

    private sealed class FailingNetworkStream(Socket socket) : NetworkStream(socket, false)
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("Controlled TCP write failure"));
    }

}
