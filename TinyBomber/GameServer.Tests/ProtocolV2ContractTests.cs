using System.Buffers.Binary;
using System.Security.Cryptography;
using GameShared;

namespace GameServer.Tests;

public sealed class ProtocolV2ContractTests
{
    private const string TokenHex = "000102030405060708090A0B0C0D0E0F";
    private const string NonceHex = "F0F1F2F3F4F5F6F7F8F9FAFBFCFDFEFF";
    private const string SlotHex = "2A000000";
    private const string RoomRoundHex = "070000000B000000";
    private const string CoordinatesHex = "0000C03F000010C0";

    // Frozen datagrams generated directly with platform AES-CBC and HMAC, without Packet or ProtocolV2.
    private const string RegistrationDatagramHex =
        SlotHex + "50000000202122232425262728292A2B2C2D2E2F" +
        "A257B1F5E3B17A25AD83087ABADB9FFAB09EAF970987F18F9EA8B4BDD0C648AE" +
        "1BCECE10CEF180EEA0B71E70BC1A34259172DE2577DF9C96F6EDBF78B00B8B1B";
    private const string ProbeDatagramHex =
        "60000000303132333435363738393A3B3C3D3E3F" +
        "2E385F1C2097A0CEF2B06022BF5243D093A82AAFB60D2CA3B676C51FBF6B2CDC83E62EFEFC5E07352EC2C8ABCF7D747" +
        "0CE4BE0DDD9781B4D069D947410D24298062C31EBD4C0D14424BC69E515754156";

    private static byte[] Bytes(string hex) => Convert.FromHexString(hex);

    [Fact]
    public void ServerHelloCarriesVersionThenSlot()
    {
        const string hex = "0F00000002000000" + SlotHex;
        using var encoded = ProtocolV2.CreateServerHello(42);
        AssertFrame(encoded, hex, 68);

        using var packet = new Packet(Bytes(hex));
        Assert.Equal((2, 42), ProtocolV2.ReadServerHello(packet));
    }

    [Fact]
    public void ClientHelloPreservesWhitespaceAndUnicodeComposition()
    {
        const string username = " A\u0301 ";
        const string hex = "0900000002000000" + SlotHex + "050000002041CC8120";
        using var encoded = ProtocolV2.CreateClientHello(42, username);
        AssertFrame(encoded, hex, 84);

        using var packet = new Packet(Bytes(hex));
        Assert.Equal((2, 42, username), ProtocolV2.ReadClientHello(packet));
    }

    [Fact]
    public void SessionGrantedCarriesExactlyOneRawToken()
    {
        const string hex = "10000000" + TokenHex;
        using var encoded = ProtocolV2.CreateSessionGranted(Bytes(TokenHex));
        AssertFrame(encoded, hex, 84);

        using var packet = new Packet(Bytes(hex));
        Assert.Equal(Bytes(TokenHex), ProtocolV2.ReadSessionGranted(packet));
    }

    [Fact]
    public void UdpRegistrationHasSlotPrefixOutsideEncryptedFrame()
    {
        using var encoded = ProtocolV2.CreateUdpRegistration(42, Bytes(TokenHex));
        AssertFrame(encoded, "0A000000" + TokenHex, 88, clientUdp: true);

        using var packet = ProtocolV2.OpenClientDatagram(Bytes(RegistrationDatagramHex), out int slot);
        Assert.Equal(42, slot);
        Assert.Equal(Bytes(TokenHex), ProtocolV2.ReadUdpRegistration(packet));
    }

    [Fact]
    public void UdpProbeCarriesTokenThenNonceWithNoSlotPrefix()
    {
        using var encoded = ProtocolV2.CreateUdpProbe(Bytes(TokenHex), Bytes(NonceHex));
        AssertFrame(encoded, "11000000" + TokenHex + NonceHex, 100);

        using var packet = ProtocolV2.OpenServerDatagram(Bytes(ProbeDatagramHex));
        var probe = ProtocolV2.ReadUdpProbe(packet);
        Assert.Equal(Bytes(TokenHex), probe.Token);
        Assert.Equal(Bytes(NonceHex), probe.Nonce);
    }

    [Fact]
    public void UdpConfirmCarriesTokenThenNonceInTcpFrame()
    {
        const string hex = "0B000000" + TokenHex + NonceHex;
        using var encoded = ProtocolV2.CreateUdpConfirm(Bytes(TokenHex), Bytes(NonceHex));
        AssertFrame(encoded, hex, 100);

        using var packet = new Packet(Bytes(hex));
        var confirm = ProtocolV2.ReadUdpConfirm(packet);
        Assert.Equal(Bytes(TokenHex), confirm.Token);
        Assert.Equal(Bytes(NonceHex), confirm.Nonce);
    }

    [Fact]
    public void LobbyReadyKeepsV1IdAndEmptyBody()
    {
        const string hex = "0C000000";
        using var encoded = ProtocolV2.CreateLobbyReady();
        AssertFrame(encoded, hex, 68);

        using var packet = new Packet(Bytes(hex));
        ProtocolV2.ReadLobbyReady(packet);
        Assert.Equal(0, packet.UnreadLength());
    }

    [Fact]
    public void UdpMovementCarriesTokenRoomRoundAndCoordinates()
    {
        const string hex = "0C000000" + TokenHex + RoomRoundHex + CoordinatesHex;
        using var encoded = ProtocolV2.CreateUdpPlayerMovement(42, Bytes(TokenHex), 7, 11, 1.5f, -2.25f);
        AssertFrame(encoded, hex, 104, clientUdp: true);

        using var packet = ProtocolV2.OpenClientDatagram(encoded.ToArray(), out int slot);
        var move = ProtocolV2.ReadUdpPlayerMovement(packet);
        Assert.Equal(42, slot);
        Assert.Equal(Bytes(TokenHex), move.Token);
        Assert.Equal((7, 11, 1.5f, -2.25f), (move.RoomId, move.RoundId, move.X, move.Y));
    }

    [Fact]
    public void UdpPositionCarriesTokenRoomRoundPlayerAndCoordinates()
    {
        const string hex = "12000000" + TokenHex + RoomRoundHex + "0D000000" + CoordinatesHex;
        using var encoded = ProtocolV2.CreateUdpPlayerPosition(Bytes(TokenHex), 7, 11, 13, 1.5f, -2.25f);
        AssertFrame(encoded, hex, 100);

        using var packet = ProtocolV2.OpenServerDatagram(encoded.ToArray());
        var position = ProtocolV2.ReadUdpPlayerPosition(packet);
        Assert.Equal(Bytes(TokenHex), position.Token);
        Assert.Equal((7, 11, 13, 1.5f, -2.25f),
            (position.RoomId, position.RoundId, position.PlayerId, position.X, position.Y));
    }

    [Theory]
    [InlineData(1, 1, "0100000041", 84)]
    [InlineData(32, 64, "40000000", 148)]
    public void ClientHelloAcceptsBothUtf8ByteBoundaries(int characters, int byteCount, string nameHex, int wireSize)
    {
        string username = new string(byteCount == 1 ? 'A' : 'é', characters);
        string hex = "0900000002000000" + SlotHex + nameHex;
        if (byteCount == 64) hex += string.Concat(Enumerable.Repeat("C3A9", 32));

        using var encoded = ProtocolV2.CreateClientHello(42, username);
        AssertFrame(encoded, hex, wireSize);
        using var packet = new Packet(Bytes(hex));
        Assert.Equal(username, ProtocolV2.ReadClientHello(packet).Username);
    }

    [Fact]
    public void ClientHelloRejects65BytesEvenWithFewerThan64Characters()
    {
        string username = new string('é', 32) + "A";
        Assert.Throws<ArgumentException>(() => ProtocolV2.CreateClientHello(42, username));

        string hex = "0900000002000000" + SlotHex + "41000000" + string.Concat(Enumerable.Repeat("C3A9", 32)) + "41";
        using var packet = new Packet(Bytes(hex));
        Assert.Throws<InvalidDataException>(() => ProtocolV2.ReadClientHello(packet));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t\u00A0")]
    public void ClientHelloWriterRejectsEmptyAndWhitespaceOnlyNames(string username)
    {
        Assert.Throws<ArgumentException>(() => ProtocolV2.CreateClientHello(42, username));
    }

    [Fact]
    public void ClientHelloWriterRejectsUnpairedSurrogate()
    {
        Assert.ThrowsAny<ArgumentException>(() => ProtocolV2.CreateClientHello(42, "\uD800"));
    }

    [Theory]
    [InlineData("00000000")] // Empty
    [InlineData("040000002009C2A0")] // Whitespace only
    [InlineData("FFFFFFFF")] // Negative length
    [InlineData("0200000041")] // Missing byte
    [InlineData("010000004142")] // Trailing byte
    [InlineData("02000000C328")] // Invalid UTF-8
    [InlineData("010000")] // Partial length
    public void ClientHelloReaderRejectsMalformedUsername(string usernameHex)
    {
        using var packet = new Packet(Bytes("0900000002000000" + SlotHex + usernameHex));
        Assert.Throws<InvalidDataException>(() => ProtocolV2.ReadClientHello(packet));
    }

    [Fact]
    public void BootstrapReturnsUnsupportedVersionFromWellFormedPayload()
    {
        using var serverHello = new Packet(Bytes("0F00000007000000" + SlotHex));
        using var clientHello = new Packet(Bytes("0900000007000000" + SlotHex + "0100000041"));

        Assert.Equal((7, 42), ProtocolV2.ReadServerHello(serverHello));
        Assert.Equal((7, 42, "A"), ProtocolV2.ReadClientHello(clientHello));
        Assert.Equal(2, ProtocolV2.Version);
    }

    [Fact]
    public void AnAdvertisedVersionDoesNotMakeMalformedBootstrapValid()
    {
        using var shortHello = new Packet(Bytes("0F00000007000000"));
        using var wrongId = new Packet(Bytes("0100000007000000" + SlotHex));
        using var trailing = new Packet(Bytes("0900000007000000" + SlotHex + "010000004100"));

        Assert.Throws<InvalidDataException>(() => ProtocolV2.ReadServerHello(shortHello));
        Assert.Throws<InvalidDataException>(() => ProtocolV2.ReadServerHello(wrongId));
        Assert.Throws<InvalidDataException>(() => ProtocolV2.ReadClientHello(trailing));
    }

    [Theory]
    [InlineData(15)]
    [InlineData(17)]
    public void WritersRequireExactTokenAndNonceSizes(int wrongSize)
    {
        byte[] wrong = new byte[wrongSize];
        byte[] token = Bytes(TokenHex);
        byte[] nonce = Bytes(NonceHex);
        Assert.Throws<ArgumentException>(() => ProtocolV2.CreateSessionGranted(wrong));
        Assert.Throws<ArgumentException>(() => ProtocolV2.CreateUdpRegistration(42, wrong));
        Assert.Throws<ArgumentException>(() => ProtocolV2.CreateUdpProbe(wrong, nonce));
        Assert.Throws<ArgumentException>(() => ProtocolV2.CreateUdpProbe(token, wrong));
        Assert.Throws<ArgumentException>(() => ProtocolV2.CreateUdpConfirm(wrong, nonce));
        Assert.Throws<ArgumentException>(() => ProtocolV2.CreateUdpConfirm(token, wrong));
        Assert.Throws<ArgumentException>(() => ProtocolV2.CreateUdpPlayerMovement(42, wrong, 7, 11, 1.5f, -2.25f));
        Assert.Throws<ArgumentException>(() => ProtocolV2.CreateUdpPlayerPosition(wrong, 7, 11, 13, 1.5f, -2.25f));
    }

    [Fact]
    public void FixedPayloadReadersRejectMissingOrTrailingBytes()
    {
        AssertExactPayload("0F00000002000000" + SlotHex, packet => ProtocolV2.ReadServerHello(packet));
        AssertExactPayload("10000000" + TokenHex, packet => ProtocolV2.ReadSessionGranted(packet));
        AssertExactPayload("0A000000" + TokenHex, packet => ProtocolV2.ReadUdpRegistration(packet));
        AssertExactPayload("11000000" + TokenHex + NonceHex, packet => ProtocolV2.ReadUdpProbe(packet));
        AssertExactPayload("0B000000" + TokenHex + NonceHex, packet => ProtocolV2.ReadUdpConfirm(packet));
        AssertExactPayload("0C000000", ProtocolV2.ReadLobbyReady);
        AssertExactPayload("0C000000" + TokenHex + RoomRoundHex + CoordinatesHex,
            packet => ProtocolV2.ReadUdpPlayerMovement(packet));
        AssertExactPayload("12000000" + TokenHex + RoomRoundHex + "0D000000" + CoordinatesHex,
            packet => ProtocolV2.ReadUdpPlayerPosition(packet));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UdpFrameRejectsBadSizeLengthHeaderAndHmac(bool clientUdp)
    {
        byte[] datagram = Bytes(clientUdp ? RegistrationDatagramHex : ProbeDatagramHex);
        Packet Open(byte[] bytes) => clientUdp
            ? ProtocolV2.OpenClientDatagram(bytes, out _)
            : ProtocolV2.OpenServerDatagram(bytes);

        Assert.Throws<InvalidDataException>(() => Open(datagram[..^1]));
        Assert.Throws<InvalidDataException>(() => Open([.. datagram, 0]));

        int lengthOffset = clientUdp ? 4 : 0;
        datagram[lengthOffset] ^= 1;
        Assert.Throws<InvalidDataException>(() => Open(datagram));
        datagram[lengthOffset] ^= 1;

        datagram[^1] ^= 1;
        Assert.Throws<InvalidDataException>(() => Open(datagram));
    }

    [Fact]
    public void ClientUdpRejectsLegacyFourByteRegistration()
    {
        Assert.Throws<InvalidDataException>(() => ProtocolV2.OpenClientDatagram(Bytes(SlotHex), out _));
    }

    [Fact]
    public void RandomTokenAndProbeNonceHaveTheApprovedWidths()
    {
        Assert.Equal(16, ProtocolV2.NewSessionToken().Length);
        Assert.Equal(16, ProtocolV2.NewProbeNonce().Length);
    }

    private static void AssertExactPayload(string hex, Action<Packet> read)
    {
        using var shortPacket = new Packet(Bytes(hex[..^2]));
        using var longPacket = new Packet(Bytes(hex + "00"));
        Assert.Throws<InvalidDataException>(() => read(shortPacket));
        Assert.Throws<InvalidDataException>(() => read(longPacket));
    }

    private static void AssertFrame(Packet encoded, string expectedPayloadHex, int wireSize, bool clientUdp = false)
    {
        byte[] bytes = encoded.ToArray();
        Assert.Equal(wireSize, bytes.Length);
        int frameOffset = clientUdp ? 4 : 0;
        if (clientUdp) Assert.Equal(Bytes(SlotHex), bytes[..4]);

        ReadOnlySpan<byte> frame = bytes.AsSpan(frameOffset);
        Assert.Equal(wireSize - frameOffset - 4, BinaryPrimitives.ReadInt32LittleEndian(frame));
        ReadOnlySpan<byte> authenticated = frame[4..^32];
        Assert.Equal(HMACSHA256.HashData(Constants.ENCRYPTION_KEY, authenticated), frame[^32..].ToArray());

        using var aes = Aes.Create();
        aes.Key = Constants.ENCRYPTION_KEY;
        byte[] plaintext = aes.DecryptCbc(authenticated[16..], authenticated[..16], PaddingMode.PKCS7);
        Assert.Equal(Bytes(expectedPayloadHex), plaintext);
    }
}
