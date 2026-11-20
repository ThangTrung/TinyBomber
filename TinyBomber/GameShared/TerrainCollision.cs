using System;

namespace GameShared
{
    public static class TerrainCollision
    {
        public static (int col, int row) WorldToGrid(float x, float y)
        {
            return ((int)Math.Floor(x) + 9, (int)Math.Floor(y) + 8);
        }

        public static bool CanStandAt(int[,] map, float x, float y)
        {
            const float r = BombCollision.PlayerRadius;
            var (left, bottom) = WorldToGrid(x - r, y - r);
            var (right, top) = WorldToGrid(x + r, y + r);
            if (left < 0 || bottom < 0 || right < left || top < bottom ||
                right >= map.GetLength(0) || top >= map.GetLength(1))
                return false;

            return map[left, bottom] == 0 && map[right, bottom] == 0
                && map[left, top] == 0 && map[right, top] == 0;
        }
    }
}
