using GameShared;

namespace GameServer.Tests;

public sealed class TcpFrameReaderTests
{
    // Frozen v1 frames produced with platform AES-CBC, PKCS7, and HMAC-SHA256 directly.
    // The fixed IVs are fixture bytes. Generating these frames did not use Packet.
    private const string WelcomeFrameHex =
        "50000000000102030405060708090A0B0C0D0E0F" +
        "AABE4DABE9A7B9E577A516900505A5E25790695C5BAA10D9FD323E39112E0CA5" +
        "FF2459FA979B775844DEB7EC9B91922C7B2E545061812091FFB00350980CE96A";

    private const string LobbyReadyFrameHex =
        "40000000101112131415161718191A1B1C1D1E1F" +
        "A642438F7E681BC12F48E90DC95C1F76" +
        "6BA77C93E41AEA04D0357BD88F561D19E244480546379733B43ADD75296535A9";

    [Theory]
    [InlineData(1)]  // In the length header
    [InlineData(4)]  // Between the length and IV
    [InlineData(12)] // In the IV
    [InlineData(25)] // In the ciphertext
    [InlineData(83)] // In the HMAC
    public void FragmentedFrameIsOnlyDispatchedWhenComplete(int splitAt)
    {
        byte[] frame = Convert.FromHexString(WelcomeFrameHex);
        var reader = new TcpFrameReader();
        var received = new List<byte[]>();

        reader.Feed(frame.AsSpan(0, splitAt), received.Add);
        Assert.Empty(received);

        reader.Feed(frame.AsSpan(splitAt), received.Add);
        Assert.Equal(frame, Assert.Single(received));
        AssertWelcome(received[0]);
    }

    [Fact]
    public void CoalescedFramesAreDispatchedInOrderAndPartialNextHeaderIsKept()
    {
        byte[] welcome = Convert.FromHexString(WelcomeFrameHex);
        byte[] lobbyReady = Convert.FromHexString(LobbyReadyFrameHex);
        var reader = new TcpFrameReader();
        var received = new List<byte[]>();

        reader.Feed(welcome.Concat(lobbyReady).Concat(welcome.Take(2)).ToArray(), received.Add);

        Assert.Collection(received,
            first => Assert.Equal(welcome, first),
            second => Assert.Equal(lobbyReady, second));
        using var packet = new Packet(received[1]);
        packet.DecryptPayload();
        Assert.Equal(64, packet.ReadInt());
        Assert.Equal(12, packet.ReadInt());
        Assert.Equal(0, packet.UnreadLength());

        reader.Feed(welcome.AsSpan(2), received.Add);
        Assert.Equal(3, received.Count);
        Assert.Equal(welcome, received[2]);
    }

    [Fact]
    public void LengthOfSixteenKiBIsAcceptedWhenAllBytesArrive()
    {
        const int maxLength = 16_384;
        var frame = new byte[4 + maxLength];
        Convert.FromHexString("00400000").CopyTo(frame, 0);
        var reader = new TcpFrameReader();
        var received = new List<byte[]>();

        // Synthetic body for the frame reader, which only inspects length and alignment.
        reader.Feed(frame.AsSpan(0, frame.Length - 1), received.Add);
        Assert.Empty(received);

        reader.Feed(frame.AsSpan(frame.Length - 1), received.Add);
        Assert.Equal(frame, Assert.Single(received));
    }

    [Theory]
    [InlineData("FFFFFFFF")] // Negative length
    [InlineData("00000000")] // Empty frame
    [InlineData("30000000")] // IV and HMAC with no ciphertext
    [InlineData("41000000")] // Ciphertext is not a whole number of AES blocks
    [InlineData("10400000")] // First aligned length above the limit
    public void InvalidHeaderIsRejectedBeforeReadingPayload(string headerHex)
    {
        var reader = new TcpFrameReader();
        var received = new List<byte[]>();

        Assert.Throws<InvalidDataException>(() => reader.Feed(Convert.FromHexString(headerHex), received.Add));
        Assert.Empty(received);
    }

    [Fact]
    public void FrameWithTamperedHmacIsRejectedDuringDecryption()
    {
        byte[] frame = Convert.FromHexString(WelcomeFrameHex);
        frame[^1] ^= 1;
        var reader = new TcpFrameReader();
        var received = new List<byte[]>();

        reader.Feed(frame, received.Add);

        using var packet = new Packet(Assert.Single(received));
        Assert.ThrowsAny<Exception>(() => packet.DecryptPayload());
    }

    private static void AssertWelcome(byte[] frame)
    {
        using var packet = new Packet(frame);
        packet.DecryptPayload();
        Assert.Equal(80, packet.ReadInt());
        Assert.Equal(1, packet.ReadInt());
        Assert.Equal("Chào", packet.ReadString());
        Assert.Equal(42, packet.ReadInt());
        Assert.Equal(0, packet.UnreadLength());
    }
}
