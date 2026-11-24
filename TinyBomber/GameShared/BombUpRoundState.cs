using System.Collections.Generic;

namespace GameShared
{
    public sealed class BombUpRoundState
    {
        private readonly HashSet<(int, int)> visible = new HashSet<(int, int)>();
        private readonly HashSet<(int, int)> collected = new HashSet<(int, int)>();
        private readonly Dictionary<int, int> capacities = new Dictionary<int, int>();
        public bool HasItem(int col, int row) => visible.Contains((col, row));
        public bool TryGetCapacity(int actor, out int capacity) => capacities.TryGetValue(actor, out capacity);

        public void SpawnPlayer(int id) => capacities.TryAdd(id, ProtocolV5.InitialCapacity);
        public void RemovePlayer(int id) => capacities.Remove(id);
        public bool Spawn(int col, int row) => !collected.Contains((col, row)) && visible.Add((col, row));
        public bool Collect(int col, int row, int actor, int capacity)
        {
            if (!capacities.TryGetValue(actor, out int previous) || capacity != previous + 1 ||
                capacity > ProtocolV5.MaximumCapacity || !visible.Remove((col, row))) return false;
            collected.Add((col, row));
            capacities[actor] = capacity;
            return true;
        }
        public void Clear()
        {
            capacities.Clear();
            collected.Clear();
            visible.Clear();
        }
    }
}
