using System.Buffers.Binary;
using System.Security.Cryptography;
using GameShared;

namespace GameServer.Tests;

public sealed class ProtocolV1ContractTests
{
    private const int SlotId = 42;
    private const int Challenge = 0x12345678;

    [Theory]
    [InlineData(ServerPackets.welcome, 1)]
    [InlineData(ServerPackets.spawnPlayer, 2)]
    [InlineData(ServerPackets.playerPosition, 3)]
    [InlineData(ServerPackets.playerDisconnected, 4)]
    [InlineData(ServerPackets.mapData, 5)]
    [InlineData(ServerPackets.bombPlaced, 6)]
    [InlineData(ServerPackets.bombExploded, 7)]
    [InlineData(ServerPackets.udpProbe, 8)]
    [InlineData(ServerPackets.matchState, 9)]
    [InlineData(ServerPackets.roundBegin, 10)]
    [InlineData(ServerPackets.playerEliminated, 11)]
    [InlineData(ServerPackets.lobbyReady, 12)]
    [InlineData(ServerPackets.roomChanged, 13)]
    [InlineData(ServerPackets.roomError, 14)]
    public void ServerPacketIdsMatchV1(ServerPackets packet, int expectedId)
    {
        Assert.Equal(expectedId, (int)packet);
    }

    [Theory]
    [InlineData(ClientPackets.welcomeReceived, 1)]
    [InlineData(ClientPackets.playerMovement, 2)]
    [InlineData(ClientPackets.placeBomb, 3)]
    [InlineData(ClientPackets.udpReady, 4)]
    [InlineData(ClientPackets.requestStart, 5)]
    [InlineData(ClientPackets.createRoom, 6)]
    [InlineData(ClientPackets.joinRoom, 7)]
    [InlineData(ClientPackets.leaveRoom, 8)]
    public void ClientPacketIdsMatchV1(ClientPackets packet, int expectedId)
    {
        Assert.Equal(expectedId, (int)packet);
    }

    [Fact]
    public void WelcomeCarriesUtf8MessageThenSlot()
    {
        using var packet = new Packet((int)ServerPackets.welcome);
        packet.Write("Chào");
        packet.Write(SlotId);

        AssertEncryptedFrame(packet, "01000000" + "050000004368C3A06F" + "2A000000", 80);
    }

    [Fact]
    public void WelcomeReceivedCarriesSlotThenUtf8Username()
    {
        using var packet = new Packet((int)ClientPackets.welcomeReceived);
        packet.Write(SlotId);
        packet.Write("Bé");

        AssertEncryptedFrame(packet, "01000000" + "2A000000" + "0300000042C3A9", 64);
    }

    [Fact]
    public void UdpRegistrationContainsOnlyFourSlotBytes()
    {
        using var packet = new Packet();
        packet.InsertInt(SlotId);

        Assert.Equal(Convert.FromHexString("2A000000"), packet.ToArray());
    }

    [Fact]
    public void UdpProbeCarriesChallengeWithoutSlotPrefix()
    {
        using var packet = new Packet((int)ServerPackets.udpProbe);
        packet.Write(Challenge);

        AssertEncryptedFrame(packet, "0800000078563412", 64);
    }

    [Fact]
    public void UdpReadyCarriesChallengeInTcpFrame()
    {
        using var packet = new Packet((int)ClientPackets.udpReady);
        packet.Write(Challenge);

        AssertEncryptedFrame(packet, "0400000078563412", 64);
    }

    [Fact]
    public void LobbyReadyCarriesOnlyPacketId()
    {
        using var packet = new Packet((int)ServerPackets.lobbyReady);

        AssertEncryptedFrame(packet, "0C000000", 64);
    }

    [Fact]
    public void UdpMovementPrefixesSlotBeforeEncryptedFrame()
    {
        using var packet = new Packet((int)ClientPackets.playerMovement);
        packet.Write(7);  // Room
        packet.Write(11); // Round
        packet.Write(1.5f);
        packet.Write(-2.25f);

        AssertEncryptedFrame(packet, "02000000" + "070000000B000000" + "0000C03F000010C0", 80,
            prefixWithSlot: true);
    }

    [Fact]
    public void UdpPositionAndTcpCorrectionShareTheUnprefixedFrameFormat()
    {
        using var packet = new Packet((int)ServerPackets.playerPosition);
        packet.Write(7);  // Room
        packet.Write(11); // Round
        packet.Write(13); // Player
        packet.Write(1.5f);
        packet.Write(-2.25f);

        AssertEncryptedFrame(packet, "03000000" + "070000000B0000000D000000" + "0000C03F000010C0", 80);
    }

    private static void AssertEncryptedFrame(Packet writer, string plaintextHex, int expectedLength,
        bool prefixWithSlot = false)
    {
        writer.WriteLength();
        writer.EncryptPayload();
        if (prefixWithSlot) writer.InsertInt(SlotId);

        byte[] wire = writer.ToArray();
        int prefixLength = prefixWithSlot ? 4 : 0;
        Assert.Equal(prefixLength + 4 + expectedLength, wire.Length);
        if (prefixWithSlot) Assert.Equal(Convert.FromHexString("2A000000"), wire[..4]);

        ReadOnlySpan<byte> frame = wire.AsSpan(prefixLength);
        Assert.Equal(expectedLength, BinaryPrimitives.ReadInt32LittleEndian(frame));

        // Open the frame independently so writer and reader cannot mask a shared format change.
        const int ivLength = 16;
        const int tagLength = 32;
        ReadOnlySpan<byte> authenticated = frame[4..^tagLength];
        Assert.Equal(HMACSHA256.HashData(Constants.ENCRYPTION_KEY, authenticated), frame[^tagLength..].ToArray());

        using var aes = Aes.Create();
        aes.Key = Constants.ENCRYPTION_KEY;
        byte[] plaintext = aes.DecryptCbc(authenticated[ivLength..], authenticated[..ivLength], PaddingMode.PKCS7);
        Assert.Equal(Convert.FromHexString(plaintextHex), plaintext);
    }
}
