using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using GameShared;

namespace GameServer.Tests;

[Collection("Network metrics server state")]
public sealed class TcpHeartbeatTests
{
    private static long At(int seconds) => 100 + seconds * Stopwatch.Frequency;

    [Fact]
    public void ReadyConnectionSendsOneChallengeWithTwoRetriesAndExpiresAtTwentySeconds()
    {
        Client slot = ReadySlot();
        var connection = slot.tcp;
        var sent = new List<long>();
        bool Send(long sequence) { sent.Add(sequence); return true; }

        for (int second = 5; second <= 15; second += 5)
        {
            int expected = second / 5;
            connection.SendHeartbeatIfDue(At(second) - 1, Send);
            Assert.Equal(expected - 1, sent.Count);
            connection.SendHeartbeatIfDue(At(second), Send);
            connection.SendHeartbeatIfDue(At(second) + 1, Send);
            Assert.Equal(expected, sent.Count);
        }

        Assert.Equal(new long[] { 1, 1, 1 }, sent);
        Assert.False(connection.HeartbeatHasExpired(At(20) - 1));
        Assert.True(connection.HeartbeatHasExpired(At(20)));
        Assert.True(connection.HeartbeatHasExpired(At(20) + 1));
        connection.SendHeartbeatIfDue(At(20), Send);
        Assert.Equal(3, sent.Count);
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    public void OnlyCurrentPongAppliedBeforeDeadlineExtendsLiveness(int offset, bool accepted)
    {
        Client slot = ReadySlot();
        var connection = slot.tcp;
        connection.SendHeartbeatIfDue(At(5), sequence => sequence == 1);
        ServerHandle.HeartbeatPong(slot, connection, 2, At(6));
        Assert.True(connection.HeartbeatHasExpired(At(20)));

        ServerHandle.HeartbeatPong(slot, connection, 1, At(20) + offset);
        Assert.Equal(!accepted, connection.HeartbeatHasExpired(At(20)));
        if (!accepted) return;

        connection.SendHeartbeatIfDue(At(25) + offset, sequence =>
        {
            Assert.Equal(2, sequence);
            return true;
        });
        ServerHandle.HeartbeatPong(slot, connection, 1, At(30));
        Assert.True(connection.HeartbeatHasExpired(At(40) + offset));
    }

    [Fact]
    public void DuplicatePongAndStalledUpdateCannotExtendDeadlineOrBurstRetries()
    {
        Client slot = ReadySlot();
        var connection = slot.tcp;
        int sends = 0;
        bool Send(long _) { sends++; return true; }

        connection.SendHeartbeatIfDue(At(16), Send);
        connection.SendHeartbeatIfDue(At(16) + 1, Send);
        Assert.Equal(1, sends);
        ServerHandle.HeartbeatPong(slot, connection, 1, At(17));
        ServerHandle.HeartbeatPong(slot, connection, 1, At(19));
        Assert.False(connection.HeartbeatHasExpired(At(37) - 1));
        Assert.True(connection.HeartbeatHasExpired(At(37)));
    }

    [Fact]
    public void TimeoutReleasesSlotAndRetiredWrapperCannotAffectReplacementWithSameSequence()
    {
        Client slot = ReadySlot();
        var survivor = new Client(slot.id - 1);
        MakeReady(survivor, At(0));
        GameLogic room = Server.CreateRoom();
        Assert.True(room.TryAddMember(slot));
        Assert.True(room.TryAddMember(survivor));
        room.StartRound(slot, room.Id, 0);
        Assert.Equal(MatchPhase.Playing, room.Phase);
        Assert.NotNull(slot.player);
        var retired = slot.tcp;
        using var retiredSocket = new TcpClient();
        var handle = retiredSocket.Client.SafeHandle;
        retired.socket = retiredSocket;
        byte[] retiredToken = retired.SessionToken!;
        Server.clients.Add(slot.id, slot);
        try
        {
            retired.SendHeartbeatIfDue(At(15), sequence => sequence == 1);
            Server.UpdateConnection(slot.id, retired, At(20) - 1);
            Assert.Same(retired, slot.tcp);
            Server.UpdateConnection(slot.id, retired, At(20));
            Assert.True(handle.IsClosed);
            Assert.False(slot.IsReady);
            Assert.Null(slot.Room);
            Assert.Null(slot.player);
            Assert.Same(survivor, Assert.Single(room.Members));
            room.UpdateRound(At(20));
            Assert.Equal(MatchPhase.Finished, room.Phase);
            Assert.Equal(survivor.id, room.WinnerId);
            var replacement = slot.tcp;
            Assert.NotSame(retired, replacement);

            MakeReady(slot, At(20));
            Assert.NotEqual(retiredToken, replacement.SessionToken);
            using var nextSocket = new TcpClient();
            replacement.socket = nextSocket;
            replacement.SendHeartbeatIfDue(At(25), sequence => sequence == 1);

            ServerHandle.HeartbeatPong(slot, retired, 1, At(39));
            Server.UpdateConnection(slot.id, retired, At(40));
            Server.Disconnect(slot.id, retired);
            Assert.Same(replacement, slot.tcp);
            Assert.True(replacement.HeartbeatHasExpired(At(40)));
            ServerHandle.HeartbeatPong(slot, replacement, 1, At(39));
            Assert.False(replacement.HeartbeatHasExpired(At(40)));
        }
        finally
        {
            Server.Disconnect(slot.id, slot.tcp);
            Server.clients.Remove(slot.id);
            Server.RemoveFromRoom(survivor);
        }
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    public void ClientWatchdogOnlyAcceptsNewPingBeforeDeadline(int offset, bool reply)
    {
        var watchdog = new HeartbeatWatchdog();
        Assert.False(watchdog.ShouldReply(1, At(0)));
        watchdog.Start(At(0));
        Assert.True(watchdog.ShouldReply(1, At(5)));
        Assert.True(watchdog.ShouldReply(1, At(10)));
        Assert.False(watchdog.ShouldReply(0, At(20)));
        Assert.Equal(At(30), watchdog.Deadline);
        Assert.Equal(!reply, watchdog.HasExpired(At(30) + offset));
        Assert.Equal(reply, watchdog.ShouldReply(2, At(30) + offset));
        Assert.Equal(reply ? At(55) + offset : At(30), watchdog.Deadline);
        Assert.False(watchdog.ShouldReply(1, At(31)));
    }

    private static Client ReadySlot()
    {
        var slot = new Client(int.MaxValue);
        MakeReady(slot, At(0));
        return slot;
    }

    private static void MakeReady(Client slot, long now)
    {
        var connection = slot.tcp;
        connection.StartHandshake(now);
        ServerHandle.ClientHello(slot, connection, ProtocolV3.Version, slot.id, "Player", now);
        byte[] token = connection.SessionToken!;
        Assert.True(ServerHandle.RegisterUdp(slot, connection, slot.udp,
            new IPEndPoint(IPAddress.Loopback, 12345), token, now));
        ServerHandle.ConfirmUdp(slot, connection, token, slot.udp.ProbeNonce!, now);
        Assert.True(slot.IsReady);
    }
}
