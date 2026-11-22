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

        public static bool CanSweep(int[,] map, float startX, float startY, float targetX, float targetY)
        {
            const float r = BombCollision.PlayerRadius;
            // Use the same float footprint edges as CanStandAt at both ends.
            var start = (Left: startX - r, Bottom: startY - r, Right: startX + r, Top: startY + r);
            var target = (Left: targetX - r, Bottom: targetY - r, Right: targetX + r, Top: targetY + r);
            float minX = Math.Min(start.Left, target.Left);
            float minY = Math.Min(start.Bottom, target.Bottom);
            float maxX = Math.Max(start.Right, target.Right);
            float maxY = Math.Max(start.Top, target.Top);

            // Positive comparisons also reject NaN and infinities before conversion to grid indices.
            if (!(minX >= -9 && minY >= -8 &&
                  maxX < map.GetLength(0) - 9d && maxY < map.GetLength(1) - 8d))
                return false;

            var (left, bottom) = WorldToGrid(minX, minY);
            var (right, top) = WorldToGrid(maxX, maxY);
            for (int col = left; col <= right; col++)
            for (int row = bottom; row <= top; row++)
            {
                if (map[col, row] != 0 && OverlapsTile(col - 9d, row - 8d))
                    return false;
            }

            return true;

            bool OverlapsTile(double x, double y)
            {
                double enter = 0d, exit = 1d;
                bool enterIncluded = true, exitIncluded = true;

                // A tile occupies [x, x + 1) by [y, y + 1), as in WorldToGrid.
                // Each edge narrows the interval during which the footprint overlaps the tile.
                return Clip(start.Right - x, target.Right - x, inclusive: true)
                    && Clip(x + 1d - start.Left, x + 1d - target.Left, inclusive: false)
                    && Clip(start.Top - y, target.Top - y, inclusive: true)
                    && Clip(y + 1d - start.Bottom, y + 1d - target.Bottom, inclusive: false);

                bool Clip(double startGap, double targetGap, bool inclusive)
                {
                    bool startsInside = startGap > 0d || (inclusive && startGap == 0d);
                    bool endsInside = targetGap > 0d || (inclusive && targetGap == 0d);
                    if (startsInside == endsInside) return startsInside;

                    double crossing = startGap / (startGap - targetGap);
                    if (startsInside && crossing <= exit)
                    {
                        exitIncluded = inclusive && (crossing < exit || exitIncluded);
                        exit = crossing;
                    }
                    else if (endsInside && crossing >= enter)
                    {
                        enterIncluded = inclusive && (crossing > enter || enterIncluded);
                        enter = crossing;
                    }

                    return enter < exit || (enter == exit && enterIncluded && exitIncluded);
                }
            }
        }
    }
}
