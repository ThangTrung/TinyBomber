using GameShared;

namespace GameServer.Tests;

public class ProtocolV5ContractTests
{
    [Theory]
    [InlineData(24, "0100000002000000100000000E000000", 0, 0)]
    [InlineData(25, "0100000002000000100000000E0000006300000003000000", 99, 3)]
    public void FixedPayload(int id, string hex, int actor, int capacity)
    {
        Assert.Equal(24, (int)ServerPackets.bombUpSpawned);
        Assert.Equal(25, (int)ServerPackets.bombUpCollected);
        using var packet = new Packet(Convert.FromHexString(hex));
        Assert.Equal((1, 2, 16, 14, actor, capacity), ProtocolV5.ReadBombUp(packet, (ServerPackets)id));
        Assert.Equal(0, packet.UnreadLength());
    }

    [Theory]
    [InlineData(24, 1, 1, 17, 0)]
    [InlineData(24, 1, 1, 0, 15)]
    [InlineData(24, 0, 1, 0, 0)]
    [InlineData(24, 1, 0, 0, 0)]
    [InlineData(24, 1, 1, 0, 0, 0)]
    [InlineData(25, 1, 1, 0, 0, 1)]
    [InlineData(25, 1, 1, 0, 0, 0, 2)]
    [InlineData(25, 1, 1, 0, 0, 1, 4)]
    public void MalformedFields(int id, params int[] fields)
    {
        using var packet = new Packet(fields.SelectMany(BitConverter.GetBytes).ToArray());
        Assert.Throws<InvalidDataException>(() => ProtocolV5.ReadBombUp(packet, (ServerPackets)id));
    }
}
