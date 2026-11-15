using System;
using GameShared;

namespace GameServer
{
    public class GameLogic
    {
        public static int[,] mapData = new int[Constants.MAP_WIDTH, Constants.MAP_HEIGHT];

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
            float r = 0.3f;
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
