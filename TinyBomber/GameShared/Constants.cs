using System;

namespace GameShared
{
    public class Constants
    {
        public const int TICKS_PER_SEC = 30;
        public const int MS_PER_TICK = 1000 / TICKS_PER_SEC;

        public const int PORT = 26950;
        public const int UDP_PORT = PORT + 1;
        public const int MAX_PLAYERS = 4;

        public const int DATA_BUFFER_SIZE = 4096;

        public const int MAP_WIDTH = 17;
        public const int MAP_HEIGHT = 15;   

        public static readonly byte[] ENCRYPTION_KEY = new byte[16]
        {
            0xA1, 0xB2, 0xC3, 0xD4, 0xE5, 0xF6, 0x07, 0x18,
            0x19, 0x2A, 0x3B, 0x4C, 0x5D, 0x6E, 0x7F, 0x80
        };

        public const int AES_IV_SIZE = 16;
        public const int HMAC_SIZE = 32;
    }

    public static class BombCollision
    {
        public const float PlayerRadius = 0.3f;

        // Sweep the player's centre against the bomb cell expanded by the player's footprint.
        public static bool BlocksStep(int col, int row, float x, float y, float nextX, float nextY)
        {
            float offsetX = x - (col - 8.5f), offsetY = y - (row - 7.5f);
            float extent = 0.5f + PlayerRadius;
            if (Math.Abs(offsetX) < extent && Math.Abs(offsetY) < extent)
                return false; // Already overlapping: allow the player to leave.
            float enter = 0f, exit = 1f;
            return ClipAxis(offsetX, nextX - x, extent, ref enter, ref exit)
                && ClipAxis(offsetY, nextY - y, extent, ref enter, ref exit);
        }

        private static bool ClipAxis(float start, float delta, float extent, ref float enter, ref float exit)
        {
            if (delta == 0f) return Math.Abs(start) < extent;
            float a = (-extent - start) / delta, b = (extent - start) / delta;
            enter = Math.Max(enter, Math.Min(a, b));
            exit = Math.Min(exit, Math.Max(a, b));
            return enter < exit;
        }
    }

    public enum ServerPackets
    {
        welcome = 1,
        spawnPlayer,
        playerPosition,
        playerDisconnected,
        mapData,
        bombPlaced,
        bombExploded
    }

    public enum ClientPackets
    {
        welcomeReceived = 1,
        playerMovement,
        placeBomb
    }
}
