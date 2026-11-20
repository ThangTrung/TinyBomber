using System;

namespace GameServer
{
    public class Player
    {
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

        internal Player(int _id, string _username, int spawnIndex)
        {
            id = _id;
            username = _username;

            x = SpawnPoints[spawnIndex].x;
            y = SpawnPoints[spawnIndex].y;
        }
    }
}
