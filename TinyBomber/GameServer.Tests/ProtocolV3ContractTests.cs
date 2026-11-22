using System.Buffers.Binary;
using System.Security.Cryptography;
using GameShared;

namespace GameServer.Tests;

public sealed class ProtocolV3ContractTests
{
    private const string SlotHex = "2A000000";
    private const string TokenHex = "000102030405060708090A0B0C0D0E0F";
    private const string SequenceHex = "0807060504030201";
    private const long Sequence = 0x0102030405060708L;
    private const string PingHex = "13000000" + SequenceHex;
    private const string PongHex = "0D000000" + SequenceHex;
    private const string ProbeHex = "0E000000" + TokenHex + SequenceHex;
    private const string EchoHex = "14000000" + TokenHex + SequenceHex;

    [Theory]
    [InlineData(true, PingHex)]
    [InlineData(false, PongHex)]
    public void TcpHeartbeatHasExactIdSequenceAndEnvelope(bool ping, string payloadHex)
    {
        using var encoded = ping ? ProtocolV3.CreateHeartbeatPing(Sequence) : ProtocolV3.CreateHeartbeatPong(Sequence);
        AssertEnvelope(encoded, payloadHex, 68);

        var reader = new TcpFrameReader();
        var frames = new List<byte[]>();
        reader.Feed(SealFixture(payloadHex), frames.Add);
        using var packet = new Packet(Assert.Single(frames));
        packet.DecryptPayload();
        Assert.Equal(64, packet.ReadInt());
        Assert.Equal(Sequence, ReadHeartbeat(ping, packet));
        Assert.Equal(0, packet.UnreadLength());
    }

    [Theory]
    [InlineData(true, ProbeHex, 88, SlotHex)]
    [InlineData(false, EchoHex, 84, "")]
    public void UdpHealthHasExactIdTokenSequenceAndDirectionalPrefix(bool probe, string payloadHex,
        int wireBytes, string prefixHex)
    {
        byte[] token = Bytes(TokenHex);
        using var encoded = probe ? ProtocolV3.CreateUdpHealthProbe(42, token, Sequence)
            : ProtocolV3.CreateUdpHealthEcho(token, Sequence);
        AssertEnvelope(encoded, payloadHex, wireBytes, prefixHex);

        using Packet packet = OpenUdp(probe, SealFixture(payloadHex, prefixHex));
        var health = ReadUdpHealth(probe, packet);
        Assert.Equal(token, health.Token);
        Assert.Equal(Sequence, health.Sequence);
        Assert.Equal(0, packet.UnreadLength());
    }

    [Theory]
    [InlineData(true, PingHex, PongHex)]
    [InlineData(false, PongHex, PingHex)]
    public void TcpHeartbeatRejectsWrongDirectionAndInexactPlaintext(bool ping, string correct, string wrongDirection) =>
        AssertMalformedPayloads(correct, wrongDirection, packet => ReadHeartbeat(ping, packet));

    [Theory]
    [InlineData(true, ProbeHex, EchoHex)]
    [InlineData(false, EchoHex, ProbeHex)]
    public void UdpHealthRejectsWrongDirectionAndInexactPlaintext(bool probe, string correct, string wrongDirection) =>
        AssertMalformedPayloads(correct, wrongDirection, packet => ReadUdpHealth(probe, packet));

    [Theory]
    [InlineData(0L, "0000000000000000")]
    [InlineData(-1L, "FFFFFFFFFFFFFFFF")]
    [InlineData(long.MinValue, "0000000000000080")]
    public void AllHealthCodecsRejectNonpositiveSequence(long sequence, string sequenceHex)
    {
        byte[] token = Bytes(TokenHex);
        Assert.Throws<ArgumentOutOfRangeException>(() => ProtocolV3.CreateHeartbeatPing(sequence));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProtocolV3.CreateHeartbeatPong(sequence));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProtocolV3.CreateUdpHealthProbe(42, token, sequence));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProtocolV3.CreateUdpHealthEcho(token, sequence));

        using var ping = SequenceFixture(PingHex, sequenceHex);
        using var pong = SequenceFixture(PongHex, sequenceHex);
        using var probe = SequenceFixture(ProbeHex, sequenceHex);
        using var echo = SequenceFixture(EchoHex, sequenceHex);
        Assert.Throws<InvalidDataException>(() => ProtocolV3.ReadHeartbeatPing(ping));
        Assert.Throws<InvalidDataException>(() => ProtocolV3.ReadHeartbeatPong(pong));
        Assert.Throws<InvalidDataException>(() => ProtocolV3.ReadUdpHealthProbe(probe));
        Assert.Throws<InvalidDataException>(() => ProtocolV3.ReadUdpHealthEcho(echo));
    }

    [Theory]
    [InlineData(1L, "0100000000000000")]
    [InlineData(long.MaxValue, "FFFFFFFFFFFFFF7F")]
    public void AllHealthCodecsAcceptSequenceBoundaries(long sequence, string sequenceHex)
    {
        byte[] token = Bytes(TokenHex);
        using var ping = ProtocolV3.CreateHeartbeatPing(sequence);
        using var pong = ProtocolV3.CreateHeartbeatPong(sequence);
        using var probe = ProtocolV3.CreateUdpHealthProbe(42, token, sequence);
        using var echo = ProtocolV3.CreateUdpHealthEcho(token, sequence);
        AssertEnvelope(ping, WithSequence(PingHex, sequenceHex), 68);
        AssertEnvelope(pong, WithSequence(PongHex, sequenceHex), 68);
        AssertEnvelope(probe, WithSequence(ProbeHex, sequenceHex), 88, SlotHex);
        AssertEnvelope(echo, WithSequence(EchoHex, sequenceHex), 84);

        using var pingFixture = SequenceFixture(PingHex, sequenceHex);
        using var pongFixture = SequenceFixture(PongHex, sequenceHex);
        using var probeFixture = SequenceFixture(ProbeHex, sequenceHex);
        using var echoFixture = SequenceFixture(EchoHex, sequenceHex);
        Assert.Equal(sequence, ProtocolV3.ReadHeartbeatPing(pingFixture));
        Assert.Equal(sequence, ProtocolV3.ReadHeartbeatPong(pongFixture));
        Assert.Equal(sequence, ProtocolV3.ReadUdpHealthProbe(probeFixture).Sequence);
        Assert.Equal(sequence, ProtocolV3.ReadUdpHealthEcho(echoFixture).Sequence);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(17)]
    public void UdpHealthWritersRejectInvalidTokenWidths(int length)
    {
        byte[] token = new byte[length];
        Assert.Throws<ArgumentException>(() => ProtocolV3.CreateUdpHealthProbe(42, token, Sequence));
        Assert.Throws<ArgumentException>(() => ProtocolV3.CreateUdpHealthEcho(token, Sequence));
    }

    [Theory]
    [InlineData(PingHex)]
    [InlineData(PongHex)]
    public void TcpHeartbeatRejectsBadFrameLengthAndHmac(string payloadHex)
    {
        byte[] badLength = SealFixture(payloadHex);
        BinaryPrimitives.WriteInt32LittleEndian(badLength, 63);
        var reader = new TcpFrameReader();
        Assert.Throws<InvalidDataException>(() => reader.Feed(badLength, _ => { }));

        byte[] badHmac = SealFixture(payloadHex);
        badHmac[^1] ^= 1;
        using var packet = new Packet(badHmac);
        Assert.Throws<Exception>(packet.DecryptPayload);
    }

    [Theory]
    [InlineData(true, ProbeHex, SlotHex)]
    [InlineData(false, EchoHex, "")]
    public void UdpHealthRejectsBadDatagramSizeLengthAndHmac(bool probe, string payloadHex, string prefixHex)
    {
        byte[] datagram = SealFixture(payloadHex, prefixHex);
        Assert.Throws<InvalidDataException>(() => OpenUdp(probe, datagram[..^1]));
        Assert.Throws<InvalidDataException>(() => OpenUdp(probe, [.. datagram, 0]));
        Assert.Throws<InvalidDataException>(() => OpenUdp(!probe, datagram));

        int lengthOffset = probe ? 4 : 0;
        datagram[lengthOffset] ^= 1;
        Assert.Throws<InvalidDataException>(() => OpenUdp(probe, datagram));
        datagram[lengthOffset] ^= 1;
        datagram[^1] ^= 1;
        Assert.Throws<InvalidDataException>(() => OpenUdp(probe, datagram));
    }

    [Fact]
    public void OnlyV3ServerDatagramOpenerAcceptsHealthEchoAlongsideV2Datagrams()
    {
        byte[] health = SealFixture(EchoHex);
        Assert.Throws<InvalidDataException>(() => ProtocolV2.OpenServerDatagram(health));
        using var echo = ProtocolV3.OpenServerDatagram(health);
        Assert.Equal(Sequence, ProtocolV3.ReadUdpHealthEcho(echo).Sequence);

        const string nonceHex = "F0F1F2F3F4F5F6F7F8F9FAFBFCFDFEFF";
        byte[] v2Probe = SealFixture("11000000" + TokenHex + nonceHex);
        using var packet = ProtocolV3.OpenServerDatagram(v2Probe);
        var probe = ProtocolV2.ReadUdpProbe(packet);
        Assert.Equal(Bytes(TokenHex), probe.Token);
        Assert.Equal(Bytes(nonceHex), probe.Nonce);
    }

    [Theory]
    [InlineData(2, "02000000")]
    [InlineData(3, "03000000")]
    public void BootstrapRetainsLayoutAndExposesAdvertisedVersionForMismatch(int version, string versionHex)
    {
        string serverHex = "0F000000" + versionHex + SlotHex;
        string clientHex = "09000000" + versionHex + SlotHex + "0300000041C3A9";

        using var server = version == 2 ? ProtocolV2.CreateServerHello(42) : ProtocolV3.CreateServerHello(42);
        using var client = version == 2 ? ProtocolV2.CreateClientHello(42, "Aé") : ProtocolV3.CreateClientHello(42, "Aé");
        AssertEnvelope(server, serverHex, 68);
        AssertEnvelope(client, clientHex, 84);

        using var serverFixture = new Packet(Bytes(serverHex));
        using var clientFixture = new Packet(Bytes(clientHex));
        var serverHello = ProtocolV2.ReadServerHello(serverFixture);
        var clientHello = ProtocolV2.ReadClientHello(clientFixture);
        Assert.Equal((version, 42), serverHello);
        Assert.Equal((version, 42, "Aé"), clientHello);
        Assert.Equal(version == 2, serverHello.Version == ProtocolV2.Version);
        Assert.Equal(version == 3, clientHello.Version == ProtocolV3.Version);
    }

    [Theory]
    [InlineData("02000000")]
    [InlineData("03000000")]
    public void AdvertisedVersionDoesNotMakeMalformedBootstrapValid(string versionHex)
    {
        string serverHeader = "0F000000" + versionHex;
        string clientHeader = "09000000" + versionHex + SlotHex;
        using var shortServer = new Packet(Bytes(serverHeader + "2A0000"));
        using var longServer = new Packet(Bytes(serverHeader + SlotHex + "00"));
        using var shortClient = new Packet(Bytes(clientHeader + "0200000041"));
        using var longClient = new Packet(Bytes(clientHeader + "010000004100"));
        using var invalidUtf8 = new Packet(Bytes(clientHeader + "02000000C328"));
        Assert.Throws<InvalidDataException>(() => ProtocolV2.ReadServerHello(shortServer));
        Assert.Throws<InvalidDataException>(() => ProtocolV2.ReadServerHello(longServer));
        Assert.Throws<InvalidDataException>(() => ProtocolV2.ReadClientHello(shortClient));
        Assert.Throws<InvalidDataException>(() => ProtocolV2.ReadClientHello(longClient));
        Assert.Throws<InvalidDataException>(() => ProtocolV2.ReadClientHello(invalidUtf8));
    }

    private static byte[] Bytes(string hex) => Convert.FromHexString(hex);

    private static string WithSequence(string payloadHex, string sequenceHex) => payloadHex[..^16] + sequenceHex;

    private static Packet SequenceFixture(string payloadHex, string sequenceHex) =>
        new(Bytes(WithSequence(payloadHex, sequenceHex)));

    private static long ReadHeartbeat(bool ping, Packet packet) =>
        ping ? ProtocolV3.ReadHeartbeatPing(packet) : ProtocolV3.ReadHeartbeatPong(packet);

    private static (byte[] Token, long Sequence) ReadUdpHealth(bool probe, Packet packet) =>
        probe ? ProtocolV3.ReadUdpHealthProbe(packet) : ProtocolV3.ReadUdpHealthEcho(packet);

    private static Packet OpenUdp(bool probe, byte[] datagram)
    {
        if (!probe) return ProtocolV3.OpenServerDatagram(datagram);

        Packet packet = ProtocolV2.OpenClientDatagram(datagram, out int slotId);
        Assert.Equal(42, slotId);
        return packet;
    }

    private static void AssertMalformedPayloads(string validHex, string wrongDirectionHex, Action<Packet> read)
    {
        using var missing = new Packet(Bytes(validHex[..^2]));
        using var trailing = new Packet(Bytes(validHex + "00"));
        using var wrongDirection = new Packet(Bytes(wrongDirectionHex));
        Assert.Throws<InvalidDataException>(() => read(missing));
        Assert.Throws<InvalidDataException>(() => read(trailing));
        Assert.Throws<InvalidDataException>(() => read(wrongDirection));
    }

    // Build an independent encrypted fixture with a fixed IV and no calls to Packet or protocol writers.
    private static byte[] SealFixture(string payloadHex, string prefixHex = "")
    {
        byte[] iv = Bytes("202122232425262728292A2B2C2D2E2F");
        using var aes = Aes.Create();
        aes.Key = Constants.ENCRYPTION_KEY;
        byte[] ciphertext = aes.EncryptCbc(Bytes(payloadHex), iv, PaddingMode.PKCS7);
        byte[] authenticated = [.. iv, .. ciphertext];
        byte[] tag = HMACSHA256.HashData(aes.Key, authenticated);
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, authenticated.Length + tag.Length);
        return [.. Bytes(prefixHex), .. header, .. authenticated, .. tag];
    }

    private static void AssertEnvelope(Packet encoded, string payloadHex, int wireBytes, string prefixHex = "")
    {
        byte[] bytes = encoded.ToArray();
        byte[] prefix = Bytes(prefixHex);
        Assert.Equal(wireBytes, bytes.Length);
        Assert.Equal(prefix, bytes[..prefix.Length]);

        ReadOnlySpan<byte> frame = bytes.AsSpan(prefix.Length);
        Assert.Equal(wireBytes - prefix.Length - 4, BinaryPrimitives.ReadInt32LittleEndian(frame));
        ReadOnlySpan<byte> authenticated = frame[4..^32];
        Assert.Equal(HMACSHA256.HashData(Constants.ENCRYPTION_KEY, authenticated), frame[^32..].ToArray());

        using var aes = Aes.Create();
        aes.Key = Constants.ENCRYPTION_KEY;
        byte[] plaintext = aes.DecryptCbc(authenticated[16..], authenticated[..16], PaddingMode.PKCS7);
        Assert.Equal(Bytes(payloadHex), plaintext);
    }
}
