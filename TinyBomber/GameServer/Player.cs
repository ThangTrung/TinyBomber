using System;

using System.Diagnostics;

namespace GameServer
{
    public class Player
    {
        private const double MovementRefillPerSecond = 4.0001;
        private const double MovementCreditMargin = 0.00001;
        private const double InitialMovementCredit = 0.08 + MovementCreditMargin;
        private const double MaximumMovementCredit = 0.32 + MovementCreditMargin;

        public int id;
        public string username;
        public float x;
        public float y;
        public int hp = 1;
        public bool isAlive = true;
        public int maxActiveBombs = 1;
        public int blastRange = 1;

        private static readonly (float x, float y)[] SpawnPoints = new[]
        {
            (-7.5f, 5.5f),
            (6.5f, 5.5f),
            (-7.5f, -6.5f),
            (6.5f, -6.5f)
        };

        private double movementCredit = InitialMovementCredit;
        private long lastMovementRefillAt;

        internal Player(int _id, string _username, int spawnIndex, long spawnedAt)
        {
            id = _id;
            username = _username;

            x = SpawnPoints[spawnIndex].x;
            y = SpawnPoints[spawnIndex].y;
            lastMovementRefillAt = spawnedAt;
        }

        internal bool TrySpendMovementCredit(float targetX, float targetY, long now)
        {
            if (now > lastMovementRefillAt)
            {
                double elapsedSeconds = (now - lastMovementRefillAt) / (double)Stopwatch.Frequency;
                movementCredit = Math.Min(MaximumMovementCredit,
                    movementCredit + elapsedSeconds * MovementRefillPerSecond);
                lastMovementRefillAt = now;
            }

            double dx = (double)targetX - x;
            double dy = (double)targetY - y;
            double distance = Math.Sqrt(dx * dx + dy * dy);
            if (!(distance <= movementCredit)) return false;

            movementCredit -= distance;
            return true;
        }
    }
}
