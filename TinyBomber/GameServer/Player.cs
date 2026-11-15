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

        private static readonly (float x, float y)[] SpawnPoints = new[]
        {
            (-7.5f, 5.5f),
            (6.5f, 5.5f),
            (-7.5f, -6.5f),
            (6.5f, -6.5f)
        };

        public Player(int _id, string _username)
        {
            id = _id;
            username = _username;

            int spawnIndex = (_id - 1) % SpawnPoints.Length;
            x = SpawnPoints[spawnIndex].x;
            y = SpawnPoints[spawnIndex].y;
        }
    }
}