using System;

namespace GameShared
{
    public class Constants
    {
        public const int TICKS_PER_SEC = 30;
        public const int MS_PER_TICK = 1000 / TICKS_PER_SEC;

        public const int PORT = 26950;
        public const int MAX_PLAYERS = 4;

        public const int DATA_BUFFER_SIZE = 4096;

        public static readonly byte[] ENCRYPTION_KEY = new byte[16]
        {
            0xA1, 0xB2, 0xC3, 0xD4, 0xE5, 0xF6, 0x07, 0x18,
            0x19, 0x2A, 0x3B, 0x4C, 0x5D, 0x6E, 0x7F, 0x80
        };

        public const int AES_IV_SIZE = 16;
        public const int HMAC_SIZE = 32;
    }

    public enum ServerPackets
    {
        welcome = 1,
        spawnPlayer,
        playerPosition,
        playerDisconnected
    }

    public enum ClientPackets
    {
        welcomeReceived = 1,
        playerMovement
    }
}
