using System.Buffers.Binary;
using System.Security.Cryptography;
using GameShared;
using Xunit;

namespace GameServer.Tests;

public sealed class ProtocolV4ContractTests
{
    private const string SlotHex = "2A000000";
    private const string TokenHex = "000102030405060708090A0B0C0D0E0F";
    private const string RoundHex = "0700000009000000";
    private const string SeqHex = "0807060504030201";
    private const string CoordinatesHex = "0000C03F000010C0"; // 1.5f, -2.25f
    private const string TcpMoveHex = "0F000000" + RoundHex + SeqHex + CoordinatesHex;
    private const string UdpMoveHex = "10000000" + TokenHex + RoundHex + SeqHex + CoordinatesHex;
    private const string TcpPositionHex = "15000000" + RoundHex + SlotHex + SeqHex + CoordinatesHex;
    private const string UdpPositionHex = "16000000" + TokenHex + RoundHex + SlotHex + SeqHex + CoordinatesHex;
    private const string CorrectionHex = "17000000" + RoundHex + SlotHex + "0907060504030201" +
        SeqHex + CoordinatesHex;
    private const long Sequence = 0x0102030405060708;

    [Fact]
    public void TcpMovementHasExactLayoutAndRoundTrips()
    {
        using var encoded = ProtocolV4.CreateTcpPlayerMovement(7, 9, Sequence, 1.5f, -2.25f);
        AssertEnvelope(encoded, TcpMoveHex, 84);
        using var packet = OpenTcp(SealFixture(TcpMoveHex));
        Assert.Equal((7, 9, Sequence, 1.5f, -2.25f), ProtocolV4.ReadTcpPlayerMovement(packet));
        Assert.Equal(0, packet.UnreadLength());
    }

    [Fact]
    public void UdpMovementHasExactLayoutAndRoundTripsThroughExistingClientEnvelope()
    {
        byte[] token = Bytes(TokenHex);
        using var encoded = ProtocolV4.CreateUdpPlayerMovement(42, token, 7, 9, Sequence, 1.5f, -2.25f);
        AssertEnvelope(encoded, UdpMoveHex, 104, SlotHex);
        using var packet = ProtocolV2.OpenClientDatagram(SealFixture(UdpMoveHex, SlotHex), out int slotId);
        var movement = ProtocolV4.ReadUdpPlayerMovement(packet);
        Assert.Equal(42, slotId);
        Assert.Equal(token, movement.Token);
        Assert.Equal((7, 9, Sequence, 1.5f, -2.25f),
            (movement.RoomId, movement.RoundId, movement.MoveSeq, movement.X, movement.Y));
        Assert.Equal(0, packet.UnreadLength());
    }

    [Fact]
    public void TcpPeerPositionHasExactLayoutAndRoundTrips()
    {
        using var encoded = ProtocolV4.CreateTcpPlayerPosition(7, 9, 42, Sequence, 1.5f, -2.25f);
        AssertEnvelope(encoded, TcpPositionHex, 100);
        using var packet = OpenTcp(SealFixture(TcpPositionHex));
        Assert.Equal((7, 9, 42, Sequence, 1.5f, -2.25f), ProtocolV4.ReadTcpPlayerPosition(packet));
        Assert.Equal(0, packet.UnreadLength());
    }

    [Fact]
    public void UdpPeerPositionHasExactLayoutAndRoundTrips()
    {
        byte[] token = Bytes(TokenHex);
        using var encoded = ProtocolV4.CreateUdpPlayerPosition(token, 7, 9, 42, Sequence, 1.5f, -2.25f);
        AssertEnvelope(encoded, UdpPositionHex, 116);
        using var packet = ProtocolV4.OpenServerDatagram(SealFixture(UdpPositionHex));
        var position = ProtocolV4.ReadUdpPlayerPosition(packet);
        Assert.Equal(token, position.Token);
        Assert.Equal((7, 9, 42, Sequence, 1.5f, -2.25f),
            (position.RoomId, position.RoundId, position.PlayerId, position.AcceptedMoveSeq, position.X, position.Y));
        Assert.Equal(0, packet.UnreadLength());
    }

    [Fact]
    public void TcpOwnerCorrectionHasExactLayoutAndRoundTrips()
    {
        using var encoded = ProtocolV4.CreateTcpPlayerCorrection(7, 9, 42, Sequence + 1, Sequence, 1.5f, -2.25f);
        AssertEnvelope(encoded, CorrectionHex, 100);
        using var packet = OpenTcp(SealFixture(CorrectionHex));
        Assert.Equal((7, 9, 42, Sequence + 1, Sequence, 1.5f, -2.25f),
            ProtocolV4.ReadTcpPlayerCorrection(packet));
        Assert.Equal(0, packet.UnreadLength());
    }

    [Theory]
    [InlineData(15, TcpMoveHex)]
    [InlineData(16, UdpMoveHex)]
    [InlineData(21, TcpPositionHex)]
    [InlineData(22, UdpPositionHex)]
    [InlineData(23, CorrectionHex)]
    public void MovementReadersRejectWrongIdAndInexactPayloadLengths(int id, string payloadHex)
    {
        using var shortPacket = new Packet(Bytes(payloadHex[..^2]));
        using var longPacket = new Packet(Bytes(payloadHex + "00"));
        using var wrongId = new Packet(Bytes("FF000000" + payloadHex[8..]));
        Assert.Throws<InvalidDataException>(() => Read(id, shortPacket));
        Assert.Throws<InvalidDataException>(() => Read(id, longPacket));
        Assert.Throws<InvalidDataException>(() => Read(id, wrongId));
    }

    [Theory]
    [InlineData(0L, "0000000000000000")]
    [InlineData(-1L, "FFFFFFFFFFFFFFFF")]
    [InlineData(long.MinValue, "0000000000000080")]
    public void AllMovementCodecsRejectNonpositivePrimarySequences(long sequence, string sequenceHex)
    {
        byte[] token = Bytes(TokenHex);
        Assert.Throws<ArgumentOutOfRangeException>(() => ProtocolV4.CreateTcpPlayerMovement(7, 9, sequence, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProtocolV4.CreateUdpPlayerMovement(42, token, 7, 9, sequence, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProtocolV4.CreateTcpPlayerPosition(7, 9, 42, sequence, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProtocolV4.CreateUdpPlayerPosition(token, 7, 9, 42, sequence, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProtocolV4.CreateTcpPlayerCorrection(7, 9, 42, sequence, 0, 0, 0));

        using var tcpMove = new Packet(Bytes(TcpMoveHex.Replace(SeqHex, sequenceHex)));
        using var udpMove = new Packet(Bytes(UdpMoveHex.Replace(SeqHex, sequenceHex)));
        using var tcpPosition = new Packet(Bytes(TcpPositionHex.Replace(SeqHex, sequenceHex)));
        using var udpPosition = new Packet(Bytes(UdpPositionHex.Replace(SeqHex, sequenceHex)));
        using var correction = new Packet(Bytes(CorrectionHex.Replace("0907060504030201", sequenceHex)
            .Replace(SeqHex, "0000000000000000")));
        Assert.Throws<InvalidDataException>(() => ProtocolV4.ReadTcpPlayerMovement(tcpMove));
        Assert.Throws<InvalidDataException>(() => ProtocolV4.ReadUdpPlayerMovement(udpMove));
        Assert.Throws<InvalidDataException>(() => ProtocolV4.ReadTcpPlayerPosition(tcpPosition));
        Assert.Throws<InvalidDataException>(() => ProtocolV4.ReadUdpPlayerPosition(udpPosition));
        Assert.Throws<InvalidDataException>(() => ProtocolV4.ReadTcpPlayerCorrection(correction));
    }

    [Theory]
    [InlineData(1L, "0100000000000000")]
    [InlineData(long.MaxValue, "FFFFFFFFFFFFFF7F")]
    public void AllMovementCodecsAcceptFirstAndLargestSequence(long sequence, string sequenceHex)
    {
        byte[] token = Bytes(TokenHex);
        using var tcpMove = ProtocolV4.CreateTcpPlayerMovement(7, 9, sequence, 1.5f, -2.25f);
        using var udpMove = ProtocolV4.CreateUdpPlayerMovement(42, token, 7, 9, sequence, 1.5f, -2.25f);
        using var tcpPosition = ProtocolV4.CreateTcpPlayerPosition(7, 9, 42, sequence, 1.5f, -2.25f);
        using var udpPosition = ProtocolV4.CreateUdpPlayerPosition(token, 7, 9, 42, sequence, 1.5f, -2.25f);
        using var correction = ProtocolV4.CreateTcpPlayerCorrection(7, 9, 42, sequence, 0, 1.5f, -2.25f);
        string correctionHex = "17000000" + RoundHex + SlotHex + sequenceHex + "0000000000000000" + CoordinatesHex;
        AssertEnvelope(tcpMove, TcpMoveHex.Replace(SeqHex, sequenceHex), 84);
        AssertEnvelope(udpMove, UdpMoveHex.Replace(SeqHex, sequenceHex), 104, SlotHex);
        AssertEnvelope(tcpPosition, TcpPositionHex.Replace(SeqHex, sequenceHex), 100);
        AssertEnvelope(udpPosition, UdpPositionHex.Replace(SeqHex, sequenceHex), 116);
        AssertEnvelope(correction, correctionHex, 100);

        using var tcpMoveFixture = new Packet(Bytes(TcpMoveHex.Replace(SeqHex, sequenceHex)));
        using var udpMoveFixture = new Packet(Bytes(UdpMoveHex.Replace(SeqHex, sequenceHex)));
        using var tcpPositionFixture = new Packet(Bytes(TcpPositionHex.Replace(SeqHex, sequenceHex)));
        using var udpPositionFixture = new Packet(Bytes(UdpPositionHex.Replace(SeqHex, sequenceHex)));
        using var correctionFixture = new Packet(Bytes(correctionHex));
        Assert.Equal(sequence, ProtocolV4.ReadTcpPlayerMovement(tcpMoveFixture).MoveSeq);
        Assert.Equal(sequence, ProtocolV4.ReadUdpPlayerMovement(udpMoveFixture).MoveSeq);
        Assert.Equal(sequence, ProtocolV4.ReadTcpPlayerPosition(tcpPositionFixture).AcceptedMoveSeq);
        Assert.Equal(sequence, ProtocolV4.ReadUdpPlayerPosition(udpPositionFixture).AcceptedMoveSeq);
        var decodedCorrection = ProtocolV4.ReadTcpPlayerCorrection(correctionFixture);
        Assert.Equal((sequence, 0L), (decodedCorrection.ProcessedMoveSeq, decodedCorrection.LastAcceptedMoveSeq));
    }

    [Fact]
    public void SenderStartsAtOneAndStopsAtExhaustionWithoutWrapping()
    {
        Assert.True(ProtocolV4.TryGetNextMovementSequence(0, out long first));
        Assert.Equal(1, first);
        Assert.True(ProtocolV4.TryGetNextMovementSequence(first, out long second));
        Assert.Equal(2, second);
        Assert.True(ProtocolV4.TryGetNextMovementSequence(long.MaxValue - 1, out long last));
        Assert.Equal(long.MaxValue, last);
        Assert.False(ProtocolV4.TryGetNextMovementSequence(last, out long exhausted));
        Assert.Equal(0, exhausted);
        Assert.Throws<ArgumentOutOfRangeException>(() => ProtocolV4.TryGetNextMovementSequence(-1, out _));
    }

    [Fact]
    public void MovementCodecPreservesCoordinatesAndLeavesGameplayValidationToServer()
    {
        const string nonFiniteCoordinates = "0000C07F000080FF"; // NaN, negative infinity
        using var tcp = new Packet(Bytes("0F000000" + RoundHex + SeqHex + nonFiniteCoordinates));
        using var udp = new Packet(Bytes("10000000" + TokenHex + RoundHex + SeqHex + nonFiniteCoordinates));
        var tcpMove = ProtocolV4.ReadTcpPlayerMovement(tcp);
        var udpMove = ProtocolV4.ReadUdpPlayerMovement(udp);
        Assert.Equal(Sequence, tcpMove.MoveSeq);
        Assert.Equal(Sequence, udpMove.MoveSeq);
        Assert.True(float.IsNaN(tcpMove.X) && float.IsNegativeInfinity(tcpMove.Y));
        Assert.True(float.IsNaN(udpMove.X) && float.IsNegativeInfinity(udpMove.Y));
    }

    [Theory]
    [InlineData(-1L, "FFFFFFFFFFFFFFFF")]
    [InlineData(2L, "0200000000000000")]
    [InlineData(3L, "0300000000000000")]
    public void CorrectionRequiresAcceptedSequenceBelowRejectedSequence(long accepted, string acceptedHex)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ProtocolV4.CreateTcpPlayerCorrection(7, 9, 42, 2, accepted, 0, 0));
        using var invalid = new Packet(Bytes("17000000" + RoundHex + SlotHex + "0200000000000000" + acceptedHex + CoordinatesHex));
        Assert.Throws<InvalidDataException>(() => ProtocolV4.ReadTcpPlayerCorrection(invalid));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(17)]
    public void UdpWritersRequireExactlySixteenTokenBytes(int length)
    {
        byte[] token = new byte[length];
        Assert.Throws<ArgumentException>(() => ProtocolV4.CreateUdpPlayerMovement(42, token, 7, 9, 1, 0, 0));
        Assert.Throws<ArgumentException>(() => ProtocolV4.CreateUdpPlayerPosition(token, 7, 9, 42, 1, 0, 0));
    }

    [Theory]
    [InlineData(true, UdpMoveHex, SlotHex)]
    [InlineData(false, UdpPositionHex, "")]
    public void UdpEnvelopeRejectsBadSizeLengthAndAuthentication(bool client, string payloadHex, string prefixHex)
    {
        byte[] datagram = SealFixture(payloadHex, prefixHex);
        Assert.Throws<InvalidDataException>(() => OpenUdp(client, datagram[..^1]));
        Assert.Throws<InvalidDataException>(() => OpenUdp(client, [.. datagram, 0]));
        Assert.Throws<InvalidDataException>(() => OpenUdp(!client, datagram));
        int lengthOffset = client ? 4 : 0;
        datagram[lengthOffset] ^= 1;
        Assert.Throws<InvalidDataException>(() => OpenUdp(client, datagram));
        datagram[lengthOffset] ^= 1;
        datagram[^1] ^= 1;
        Assert.Throws<InvalidDataException>(() => OpenUdp(client, datagram));
    }

    [Theory]
    [InlineData(TcpMoveHex)]
    [InlineData(TcpPositionHex)]
    [InlineData(CorrectionHex)]
    public void TcpEnvelopeRejectsInvalidLengthAndAuthentication(string payloadHex)
    {
        byte[] badLength = SealFixture(payloadHex);
        badLength[0] ^= 1;
        Assert.Throws<InvalidDataException>(() => new TcpFrameReader().Feed(badLength, _ => { }));

        byte[] badTag = SealFixture(payloadHex);
        badTag[^1] ^= 1;
        using var packet = new Packet(badTag);
        Assert.Throws<Exception>(packet.DecryptPayload);
    }

    [Fact]
    public void VersionFourHelloAdvertisesVersionInTheExistingBootstrapLayout()
    {
        const string serverHex = "0F00000004000000" + SlotHex;
        const string clientHex = "0900000004000000" + SlotHex + "0300000041C3A9";
        using var server = ProtocolV4.CreateServerHello(42);
        using var client = ProtocolV4.CreateClientHello(42, "Aé");
        Assert.Equal(4, ProtocolV4.Version);
        AssertEnvelope(server, serverHex, 68);
        AssertEnvelope(client, clientHex, 84);
        using var serverPacket = OpenTcp(SealFixture(serverHex));
        using var clientPacket = OpenTcp(SealFixture(clientHex));
        Assert.Equal((ProtocolV4.Version, 42), ProtocolV2.ReadServerHello(serverPacket));
        Assert.Equal((ProtocolV4.Version, 42, "Aé"), ProtocolV2.ReadClientHello(clientPacket));
        using var legacy = ProtocolV3.CreateServerHello(42);
        using var legacyPacket = OpenTcp(legacy.ToArray());
        Assert.NotEqual(ProtocolV4.Version, ProtocolV2.ReadServerHello(legacyPacket).Version);

        using var shortServer = new Packet(Bytes(serverHex[..^2]));
        using var longClient = new Packet(Bytes(clientHex + "00"));
        Assert.Throws<InvalidDataException>(() => ProtocolV2.ReadServerHello(shortServer));
        Assert.Throws<InvalidDataException>(() => ProtocolV2.ReadClientHello(longClient));
    }

    [Fact]
    public void V4MovementIsDistinctFromV3MovementAndHealth()
    {
        byte[] token = Bytes(TokenHex);
        using var legacyMove = ProtocolV2.CreateUdpPlayerMovement(42, token, 7, 9, 1.5f, -2.25f);
        using var legacyPacket = ProtocolV2.OpenClientDatagram(legacyMove.ToArray(), out _);
        Assert.Throws<InvalidDataException>(() => ProtocolV4.ReadUdpPlayerMovement(legacyPacket));
        using var v4Packet = ProtocolV2.OpenClientDatagram(SealFixture(UdpMoveHex, SlotHex), out _);
        Assert.Throws<InvalidDataException>(() => ProtocolV2.ReadUdpPlayerMovement(v4Packet));
        Assert.Throws<InvalidDataException>(() => ProtocolV3.OpenServerDatagram(SealFixture(UdpPositionHex)));

        using var health = ProtocolV3.CreateUdpHealthEcho(token, Sequence);
        using var healthPacket = ProtocolV4.OpenServerDatagram(health.ToArray());
        var echo = ProtocolV3.ReadUdpHealthEcho(healthPacket);
        Assert.Equal(token, echo.Token);
        Assert.Equal(Sequence, echo.Sequence);
        using var probe = ProtocolV2.CreateUdpProbe(token, token);
        using var probePacket = ProtocolV4.OpenServerDatagram(probe.ToArray());
        Assert.Equal(token, ProtocolV2.ReadUdpProbe(probePacket).Nonce);
    }

    private static object Read(int id, Packet packet) => id switch
    {
        15 => ProtocolV4.ReadTcpPlayerMovement(packet),
        16 => ProtocolV4.ReadUdpPlayerMovement(packet),
        21 => ProtocolV4.ReadTcpPlayerPosition(packet),
        22 => ProtocolV4.ReadUdpPlayerPosition(packet),
        23 => ProtocolV4.ReadTcpPlayerCorrection(packet),
        _ => throw new ArgumentOutOfRangeException(nameof(id))
    };

    private static Packet OpenUdp(bool client, byte[] datagram) => client
        ? ProtocolV2.OpenClientDatagram(datagram, out _)
        : ProtocolV4.OpenServerDatagram(datagram);

    private static Packet OpenTcp(byte[] wire)
    {
        var reader = new TcpFrameReader();
        var frames = new List<byte[]>();
        reader.Feed(wire.AsSpan(0, 3), frames.Add);
        Assert.Empty(frames);
        reader.Feed(wire.AsSpan(3), frames.Add);
        var packet = new Packet(Assert.Single(frames));
        packet.DecryptPayload();
        packet.ReadInt();
        return packet;
    }

    private static byte[] Bytes(string hex) => Convert.FromHexString(hex);

    // Both the plaintext layout and the encrypted fixtures are independent of the codec under test.
    private static byte[] SealFixture(string payloadHex, string prefixHex = "")
    {
        byte[] iv = Bytes("202122232425262728292A2B2C2D2E2F");
        using var aes = Aes.Create();
        aes.Key = Constants.ENCRYPTION_KEY;
        byte[] ciphertext = aes.EncryptCbc(Bytes(payloadHex), iv, PaddingMode.PKCS7);
        byte[] authenticated = [.. iv, .. ciphertext];
        byte[] length = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, authenticated.Length + 32);
        return [.. Bytes(prefixHex), .. length, .. authenticated, .. HMACSHA256.HashData(aes.Key, authenticated)];
    }

    private static void AssertEnvelope(Packet encoded, string payloadHex, int wireBytes, string prefixHex = "")
    {
        byte[] bytes = encoded.ToArray();
        byte[] prefix = Bytes(prefixHex);
        Assert.Equal(wireBytes, bytes.Length);
        Assert.Equal(prefix, bytes[..prefix.Length]);
        ReadOnlySpan<byte> frame = bytes.AsSpan(prefix.Length);
        Assert.Equal(frame.Length - 4, BinaryPrimitives.ReadInt32LittleEndian(frame));
        ReadOnlySpan<byte> authenticated = frame[4..^32];
        Assert.Equal(HMACSHA256.HashData(Constants.ENCRYPTION_KEY, authenticated), frame[^32..].ToArray());
        using var aes = Aes.Create();
        aes.Key = Constants.ENCRYPTION_KEY;
        Assert.Equal(Bytes(payloadHex), aes.DecryptCbc(authenticated[16..], authenticated[..16], PaddingMode.PKCS7));
    }
}
