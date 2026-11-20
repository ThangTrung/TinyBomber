using System.Text;
using GameShared;

namespace GameServer.Tests;

public class PacketStringTests
{
    [Theory]
    [InlineData("Player_1", "08000000506C617965725F31")]
    [InlineData("Đá", "04000000C490C3A1")]
    [InlineData("🙂", "04000000F09F9982")]
    [InlineData("a\u0301", "0300000061CC81")]
    [InlineData("", "00000000")]
    public void StringUsesUtf8ByteLengthAndPreservesNextField(string value, string wireHex)
    {
        using var writer = new Packet();
        writer.Write(value);
        writer.Write(42);
        byte[] expected = Convert.FromHexString(wireHex + "2A000000");
        Assert.Equal(expected, writer.ToArray());

        using var reader = new Packet(expected);
        Assert.Equal(value, reader.ReadString());
        Assert.Equal(42, reader.ReadInt());
        Assert.Equal(0, reader.UnreadLength());
    }

    [Theory]
    [InlineData("Tiếng Việt 🙂")]
    [InlineData("")]
    public void PeekAtLastStringLeavesCursorUnchanged(string value)
    {
        using var writer = new Packet(17);
        writer.Write(value);
        using var reader = new Packet(writer.ToArray());
        Assert.Equal(17, reader.ReadInt());
        int remaining = reader.UnreadLength();

        Assert.Equal(value, reader.ReadString(false));
        Assert.Equal(remaining, reader.UnreadLength());
        Assert.Equal(value, reader.ReadString(false));
        Assert.Equal(remaining, reader.UnreadLength());
        Assert.Equal(value, reader.ReadString());
        Assert.Equal(0, reader.UnreadLength());
    }

    [Theory]
    [InlineData("")]
    [InlineData("01")]
    [InlineData("0100")]
    [InlineData("010000")]
    [InlineData("FFFFFFFF")]
    [InlineData("FFFFFF7F")]
    [InlineData("04000000C490C3")]
    [InlineData("01000000FF")]
    [InlineData("03000000F09F99")]
    public void InvalidStringFailsWithoutConsumingBytes(string fieldHex)
    {
        using var reader = new Packet(Convert.FromHexString("2A000000" + fieldHex));
        Assert.Equal(42, reader.ReadInt());
        int remaining = reader.UnreadLength();

        Assert.Throws<InvalidDataException>(() => reader.ReadString(false));
        Assert.Equal(remaining, reader.UnreadLength());
        Assert.Throws<InvalidDataException>(() => reader.ReadString());
        Assert.Equal(remaining, reader.UnreadLength());
    }

    [Fact]
    public void InvalidStringInputLeavesWrittenBytesUnchanged()
    {
        using var writer = new Packet(42);
        byte[] original = writer.ToArray();
        Assert.Throws<ArgumentNullException>(() => writer.Write((string)null!));
        Assert.Equal(original, writer.ToArray());
        Assert.Throws<EncoderFallbackException>(() => writer.Write("\uD800"));
        Assert.Equal(original, writer.ToArray());
    }

    private static byte[] WelcomeFrame(string message, int clientId)
    {
        using var writer = new Packet((int)ServerPackets.welcome);
        writer.Write(message);
        writer.Write(clientId);
        writer.WriteLength();
        writer.EncryptPayload();
        return writer.ToArray();
    }

    [Fact]
    public void FragmentedAndCoalescedWelcomeFramesPreserveMessageAndId()
    {
        // At the 16 KiB encrypted payload limit, allow for IV, HMAC,
        // three Int32 fields and at least one byte of PKCS7 padding.
        const int maxPayload = 16 * 1024;
        const int maxTextBytes = maxPayload - Constants.AES_IV_SIZE - Constants.HMAC_SIZE - 3 * sizeof(int) - 1;
        string message = new string('a', maxTextBytes - 4) + "Đá";
        byte[] first = WelcomeFrame(message, 42);
        byte[] second = WelcomeFrame("Ván mới 🙂", 7);
        Assert.Equal(maxPayload + sizeof(int), first.Length);

        var frames = new TcpFrameReader();
        var received = new List<(string, int)>();
        void ReadWelcome(byte[] data)
        {
            using var reader = new Packet(data);
            reader.DecryptPayload();
            Assert.Equal(data.Length - sizeof(int), reader.ReadInt());
            Assert.Equal((int)ServerPackets.welcome, reader.ReadInt());
            received.Add((reader.ReadString(), reader.ReadInt()));
            Assert.Equal(0, reader.UnreadLength());
        }

        byte[] both = first.Concat(second).ToArray();
        frames.Feed(both.AsSpan(0, 2), ReadWelcome);
        frames.Feed(both.AsSpan(2, first.Length - 3), ReadWelcome);
        Assert.Empty(received);
        frames.Feed(both.AsSpan(first.Length - 1), ReadWelcome);
        Assert.Equal(new[] { (message, 42), ("Ván mới 🙂", 7) }, received);

        byte[] oversized = WelcomeFrame(message + "a", 42);
        Assert.Throws<InvalidDataException>(() => frames.Feed(oversized, ReadWelcome));
        Assert.Equal(2, received.Count);
    }
}
