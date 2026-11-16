using System;
using GameShared;
using System.Diagnostics;

namespace GameServer
{
    public class GameLogic
    {
        public static int[,] mapData = new int[Constants.MAP_WIDTH, Constants.MAP_HEIGHT];
        private sealed record Bomb(Player Owner, int Col, int Row, int Range, long Deadline);
        private static readonly List<Bomb> bombs = new();
        private static readonly (int dx, int dy)[] directions = { (1, 0), (-1, 0), (0, 1), (0, -1) };

        internal static void PlaceBomb(Player player)
        {
            var (col, row) = WorldToGrid(player.x, player.y);
            if (!player.isAlive || !InBounds(col, row) || mapData[col, row] != 0) return;
            if (bombs.Any(b => b.Col == col && b.Row == row) ||
                bombs.Count(b => b.Owner == player) >= player.maxActiveBombs) return;

            bombs.Add(new Bomb(player, col, row, player.blastRange,
                Stopwatch.GetTimestamp() + 2 * Stopwatch.Frequency));
            ServerSend.BombPlaced(col, row);
        }

        internal static void SendActiveBombs(int clientId)
        {
            foreach (var bomb in bombs) ServerSend.BombPlaced(bomb.Col, bomb.Row, clientId);
        }

        internal static void UpdateBombs()
        {
            long now = Stopwatch.GetTimestamp();
            // With a fixed fuse, insertion order is deadline order.
            while (bombs.Count > 0 && bombs[0].Deadline <= now)
            {
                Bomb bomb = bombs[0];
                bombs.RemoveAt(0);
                var affected = new List<(int, int)> { (bomb.Col, bomb.Row) };
                var destroyed = new List<(int, int)>();
                foreach (var (dx, dy) in directions)
                {
                    for (int step = 1; step <= bomb.Range; step++)
                    {
                        int col = bomb.Col + dx * step, row = bomb.Row + dy * step;
                        if (!InBounds(col, row) || mapData[col, row] == 1) break;
                        affected.Add((col, row));
                        if (mapData[col, row] == 2)
                        {
                            mapData[col, row] = 0;
                            destroyed.Add((col, row));
                            break;
                        }
                    }
                }
                ServerSend.BombExploded(bomb.Col, bomb.Row, affected, destroyed);
            }
        }

        private static bool InBounds(int col, int row) =>
            col >= 0 && col < Constants.MAP_WIDTH && row >= 0 && row < Constants.MAP_HEIGHT;

        internal static bool CanMove(Player player, float x, float y)
        {
            if (!CanMoveTo(x, y)) return false;
            foreach (var bomb in bombs)
                if (BombCollision.BlocksStep(bomb.Col, bomb.Row, player.x, player.y, x, y)) return false;
            return true;
        }

        private static readonly int[,] customHardBlocks = new int[15, 17]
        {
            {1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1},
            {1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1},
            {1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1},
            {1, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 1},
            {1, 0, 1, 1, 0, 1, 1, 0, 0, 0, 1, 1, 0, 1, 1, 0, 1},
            {1, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 1},
            {1, 1, 0, 1, 1, 1, 0, 1, 1, 1, 0, 1, 1, 1, 0, 1, 1},
            {1, 1, 0, 0, 1, 0, 0, 1, 1, 1, 0, 0, 1, 0, 0, 1, 1}, // Tâm đài đá
            {1, 1, 0, 1, 1, 1, 0, 1, 1, 1, 0, 1, 1, 1, 0, 1, 1},
            {1, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 1},
            {1, 0, 1, 1, 0, 1, 1, 0, 0, 0, 1, 1, 0, 1, 1, 0, 1},
            {1, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 1},
            {1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1},
            {1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1},
            {1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1}
        };

        public static void InitializeMap()
        {
            Random rand = new Random();
            bombs.Clear();

            for (int col = 0; col < Constants.MAP_WIDTH; col++)
            {
                for (int row = 0; row < Constants.MAP_HEIGHT; row++)
                {
                    // Lật ngược hàng vì C# đọc từ trên xuống, còn Unity vẽ Y từ dưới lên
                    int visualRow = (Constants.MAP_HEIGHT - 1) - row;

                    if (customHardBlocks[visualRow, col] == 1)
                    {
                        mapData[col, row] = 1;
                    }
                    else
                    {
                        // Nếu là đất trống, né 4 góc spawn ra
                        if (IsSpawnSafeZone(col, row))
                        {
                            mapData[col, row] = 0;
                        }
                        else
                        {
                            // Đặt gạch mềm (2) ngẫu nhiên
                            mapData[col, row] = (rand.Next(100) < 65) ? 2 : 0;
                        }
                    }
                }
            }
            Console.WriteLine("[MAP] Da khoi tao ban do Custom 17x15 tren Server thanh cong!");
        }

        private static bool IsSpawnSafeZone(int col, int row)
        {
            // Bottom-Left
            if ((col == 1 && row == 1) || (col == 2 && row == 1) || (col == 1 && row == 2)) return true;
            // Bottom-Right
            if ((col == 15 && row == 1) || (col == 14 && row == 1) || (col == 15 && row == 2)) return true;
            // Top-Left
            if ((col == 1 && row == 13) || (col == 2 && row == 13) || (col == 1 && row == 12)) return true;
            // Top-Right
            if ((col == 15 && row == 13) || (col == 14 && row == 13) || (col == 15 && row == 12)) return true;
            return false;
        }

        public static (int col, int row) WorldToGrid(float worldX, float worldY)
        {
            int col = (int)Math.Floor(worldX) + 9;
            int row = (int)Math.Floor(worldY) + 8;
            return (col, row);
        }

        public static (float x, float y) GridToWorld(int col, int row)
        {
            return (col - 8.5f, row - 7.5f);
        }

        public static bool CanMoveTo(float targetX, float targetY)
        {
            float r = BombCollision.PlayerRadius;
            return IsWalkable(targetX - r, targetY - r) &&
                   IsWalkable(targetX + r, targetY - r) &&
                   IsWalkable(targetX - r, targetY + r) &&
                   IsWalkable(targetX + r, targetY + r);
        }

        private static bool IsWalkable(float wx, float wy)
        {
            var (col, row) = WorldToGrid(wx, wy);
            if (col < 0 || col >= Constants.MAP_WIDTH || row < 0 || row >= Constants.MAP_HEIGHT)
                return false;
            return mapData[col, row] == 0;
        }
    }
}
