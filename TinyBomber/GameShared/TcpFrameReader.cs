using System;
using System.IO;

namespace GameShared
{
    public sealed class TcpFrameReader
    {
        private const int MaxPayloadLength = 16 * 1024;
        private readonly byte[] header = new byte[sizeof(int)];
        private byte[]? frame;
        private int buffered;

        // One reader per TCP connection. Only its receive callback feeds bytes.
        public void Feed(ReadOnlySpan<byte> input, Action<byte[]> onFrame)
        {
            while (!input.IsEmpty)
            {
                byte[] target = frame ?? header;
                int count = Math.Min(input.Length, target.Length - buffered);
                input.Slice(0, count).CopyTo(target.AsSpan(buffered));
                input = input.Slice(count);
                buffered += count;

                if (buffered < target.Length) continue;

                if (frame == null)
                {
                    int length = BitConverter.ToInt32(header, 0);
                    int overhead = Constants.AES_IV_SIZE + Constants.HMAC_SIZE;
                    if (length <= overhead || length > MaxPayloadLength ||
                        (length - overhead) % 16 != 0)
                        throw new InvalidDataException($"Invalid TCP payload length: {length}");

                    frame = new byte[header.Length + length];
                    header.CopyTo(frame, 0);
                }
                else
                {
                    buffered = 0;
                    frame = null;
                    onFrame(target);
                }
            }
        }
    }
}
