using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using GameShared;

namespace GameServer.Tests;

[Collection("Network metrics server state")]
public sealed class ProtocolV2SessionTests
{
    [Theory]
    [InlineData(1, 42)]
    [InlineData(2, 43)]
    public void ClientHelloWithWrongVersionOrSlotReleasesConnectionBeforeGrant(int version, int slotId)
    {
        var slot = new Client(42);
        var connection = slot.tcp;
        using var socket = new TcpClient();
        var handle = socket.Client.SafeHandle;
        connection.socket = socket;
        connection.StartHandshake(100);
        Server.clients.Add(slot.id, slot);
        try
        {
            ServerHandle.ClientHello(slot, connection, version, slotId, "Player", 100);

            Assert.Null(connection.SessionToken);
            Assert.Null(slot.Username);
            Assert.Null(slot.udp.endPoint);
            Assert.NotSame(connection, slot.tcp);
            Assert.True(handle.IsClosed);
        }
        finally
        {
            Server.clients.Remove(slot.id);
        }
    }

    [Fact]
    public void DuplicateHelloAndRegistrationRetainTheTokenAndNonce()
    {
        var slot = new Client(1);
        var endpoint = new IPEndPoint(IPAddress.Loopback, 12345);
        slot.tcp.StartHandshake(100);

        Assert.False(ServerHandle.RegisterUdp(slot, slot.tcp, slot.udp, endpoint, new byte[16], 100));
        Assert.Null(slot.udp.endPoint);
        ServerHandle.ClientHello(slot, slot.tcp, ProtocolV2.Version, slot.id, "Player", 100);
        byte[] token = Assert.IsType<byte[]>(slot.tcp.SessionToken);
        ServerHandle.ClientHello(slot, slot.tcp, ProtocolV2.Version, slot.id, "Repeated", 101);
        Assert.Same(token, slot.tcp.SessionToken);
        Assert.Equal("Player", slot.Username);

        Assert.True(ServerHandle.RegisterUdp(slot, slot.tcp, slot.udp, endpoint, token, 101));
        byte[] nonce = Assert.IsType<byte[]>(slot.udp.ProbeNonce);
        Assert.True(ServerHandle.RegisterUdp(slot, slot.tcp, slot.udp, endpoint, token, 102));
        Assert.Same(nonce, slot.udp.ProbeNonce);
        Assert.False(ServerHandle.RegisterUdp(slot, slot.tcp, slot.udp,
            new IPEndPoint(IPAddress.Loopback, endpoint.Port + 1), token, 102));
        Assert.Equal(endpoint, slot.udp.endPoint);
    }

    [Fact]
    public void ConfirmRequiresCurrentWrapperValidTokenAndBoundEndpointNonce()
    {
        var slot = new Client(1);
        var endpoint = new IPEndPoint(IPAddress.Loopback, 12345);
        slot.tcp.StartHandshake(100);
        ServerHandle.ClientHello(slot, slot.tcp, ProtocolV2.Version, slot.id, "Player", 100);
        byte[] token = slot.tcp.SessionToken!;
        ServerHandle.ConfirmUdp(slot, slot.tcp, token, new byte[16], 101);
        Assert.False(slot.IsReady);

        Assert.True(ServerHandle.RegisterUdp(slot, slot.tcp, slot.udp, endpoint, token, 101));
        byte[] nonce = slot.udp.ProbeNonce!;
        ServerHandle.ConfirmUdp(slot, new Client.TCP(slot.id), token, nonce, 102);
        ServerHandle.ConfirmUdp(slot, slot.tcp, Different(token), nonce, 102);
        ServerHandle.ConfirmUdp(slot, slot.tcp, token, Different(nonce), 102);
        Assert.False(slot.IsReady);

        ServerHandle.ConfirmUdp(slot, slot.tcp, token, nonce, 102);
        Assert.True(slot.IsReady);
        Assert.False(ServerHandle.RegisterUdp(slot, slot.tcp, slot.udp, endpoint, token, 103));
        Assert.Equal(endpoint, slot.udp.endPoint);
    }

    [Fact]
    public async Task BroadcastFallbackKeepsLegacyTcpPositionLayout()
    {
        var mover = new Client(1) { Username = "Mover", UdpConfirmed = true };
        var recipient = new Client(2) { Username = "Recipient", UdpConfirmed = true };
        var room = new GameLogic(42);
        Assert.True(room.TryAddMember(mover));
        Assert.True(room.TryAddMember(recipient));
        room.StartRound(mover, room.Id, 0);
        Player player = Assert.IsType<Player>(mover.player);
        recipient.UdpConfirmed = false;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        CancellationToken token = timeout.Token;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            using var peer = new TcpClient();
            await peer.ConnectAsync((IPEndPoint)listener.LocalEndpoint, token);
            using TcpClient accepted = await listener.AcceptTcpClientAsync(token);
            recipient.tcp.Connect(accepted);
            try
            {
                using Packet hello = await ReadFrameAsync(peer.GetStream(), token);
                Assert.Equal(recipient.id, ProtocolV2.ReadServerHello(hello).SlotId);

                ServerSend.BroadcastPlayerPosition(room, player);

                using Packet position = await ReadFrameAsync(peer.GetStream(), token);
                Assert.Equal((int)ServerPackets.playerPosition, position.ReadInt());
                Assert.Equal((room.Id, room.RoundId, mover.id, player.x, player.y),
                    (position.ReadInt(), position.ReadInt(), position.ReadInt(), position.ReadFloat(), position.ReadFloat()));
                Assert.Equal(0, position.UnreadLength());
            }
            finally
            {
                recipient.tcp.Close();
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    private static byte[] Different(byte[] bytes)
    {
        byte[] copy = (byte[])bytes.Clone();
        copy[0] ^= 1;
        return copy;
    }

    private static async Task<Packet> ReadFrameAsync(NetworkStream stream, CancellationToken token)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, token);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        Assert.InRange(length, 64, 16_384);
        var frame = new byte[header.Length + length];
        header.CopyTo(frame, 0);
        await stream.ReadExactlyAsync(frame.AsMemory(header.Length), token);
        var packet = new Packet(frame);
        try
        {
            packet.DecryptPayload();
            packet.ReadInt();
            return packet;
        }
        catch
        {
            packet.Dispose();
            throw;
        }
    }
}
