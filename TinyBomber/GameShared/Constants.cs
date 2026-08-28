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
