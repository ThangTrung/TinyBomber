using System;
using GameShared;
using System.Diagnostics;

namespace GameServer
{
    internal sealed class GameLogic
    {
        internal const int DefaultFinalPositionIdleMs = 200;
        private readonly TimeSpan finalPositionIdleDelay;
        private readonly List<Client> members = new();
        internal IReadOnlyList<Client> Members => members;
        internal readonly int[,] mapData = new int[Constants.MAP_WIDTH, Constants.MAP_HEIGHT];
        private sealed record Bomb(Player Owner, int Col, int Row, int Range, long Deadline);
        private readonly List<Bomb> bombs = new();
        private const int BombUpDropPercent = 25;
        private readonly Random itemRandom;
        internal readonly HashSet<(int Col, int Row)> BombUps = new();
        private static readonly (int dx, int dy)[] directions = { (1, 0), (-1, 0), (0, 1), (0, -1) };

        internal GameLogic(int id, int finalPositionIdleMs = DefaultFinalPositionIdleMs, Random? itemRandom = null)
        {
            Id = id;
            this.itemRandom = itemRandom ?? Random.Shared;
            finalPositionIdleDelay = TimeSpan.FromMilliseconds(finalPositionIdleMs);
        }

        internal int Id { get; }
        internal MatchPhase Phase { get; private set; }
        internal int RoundId { get; private set; }
        internal int WinnerId { get; private set; }

        internal bool TryAddMember(Client slot)
        {
            if (slot.Room != null || members.Count >= Constants.MAX_PLAYERS) return false;

            members.Add(slot);
            slot.Room = this;
            return true;
        }

        internal void RemoveMember(Client slot)
        {
            if (!members.Remove(slot)) return;

            bool hadPlayer = slot.player != null;
            slot.player = null;
            slot.Room = null;
            if (hadPlayer) ServerSend.PlayerDisconnected(this, slot.id);
            ServerSend.MatchState(this);
        }

        internal bool AcceptsInput(Client slot, int roomId, int roundId) =>
            slot.Room == this && roomId == Id && Phase == MatchPhase.Playing && roundId == RoundId &&
            slot.player is { isAlive: true };

        internal void StartRound(Client requester, int roomId, int expectedRoundId, long? now = null)
        {
            if (requester.Room != this || roomId != Id || Phase == MatchPhase.Playing ||
                expectedRoundId != RoundId || !requester.IsReady)
                return;

            var participants = members.Where(slot => slot.IsReady).OrderBy(slot => slot.id).ToArray();
            if (participants.Length < 2) return;

            RoundId++;
            WinnerId = 0;
            InitializeMap();
            long spawnedAt = now ?? Stopwatch.GetTimestamp();
            foreach (var slot in members) slot.player = null;
            for (int i = 0; i < participants.Length; i++)
            {
                Client slot = participants[i];
                slot.player = new Player(slot.id, slot.Username!, i, spawnedAt);
            }
            Phase = MatchPhase.Playing;

            foreach (var slot in participants)
            {
                ServerSend.RoundBegin(this, slot, participants);
                ServerSend.SendMapData(this, slot);
                foreach (var participant in participants)
                {
                    var player = participant.player!;
                    ServerSend.SpawnPlayer(this, slot, player);
                }
            }
            ServerSend.MatchState(this);
        }

        internal int ActiveBombCount(Player player) => bombs.Count(b => b.Owner == player);

        internal void PlaceBomb(Client slot, int roomId, int roundId, long? placedAt = null)
        {
            if (!AcceptsInput(slot, roomId, roundId)) return;

            Player player = slot.player!;
            var (col, row) = WorldToGrid(player.x, player.y);
            if (!InBounds(col, row) || mapData[col, row] != 0) return;
            if (bombs.Any(b => b.Col == col && b.Row == row) ||
                ActiveBombCount(player) >= player.maxActiveBombs) return;

            bombs.Add(new Bomb(player, col, row, player.blastRange,
                (placedAt ?? Stopwatch.GetTimestamp()) + 2 * Stopwatch.Frequency));
            ServerSend.BombPlaced(this, col, row);
        }

        internal void UpdateRound(long now)
        {
            if (Phase != MatchPhase.Playing) return;

            var alive = members.Select(slot => slot.player)
                .OfType<Player>().Where(player => player.isAlive).ToList();
            List<(int Col, int Row)>? broken = null;
            var hit = new HashSet<Player>();
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
                            (broken ??= new()).Add((col, row));
                            break;
                        }
                    }
                }
                foreach (var player in alive)
                {
                    if (affected.Contains(WorldToGrid(player.x, player.y))) hit.Add(player);
                }

                ServerSend.BombExploded(this, bomb.Col, bomb.Row, affected, destroyed);
            }

            foreach (var player in hit)
            {
                player.hp = 0;
                player.isAlive = false;
                ServerSend.PlayerEliminated(this, player.id);
            }

            alive.RemoveAll(hit.Contains);
            if (alive.Count > 1)
            {
                if (broken != null)
                    foreach (var cell in broken)
                        if (itemRandom.Next(100) < BombUpDropPercent && BombUps.Add(cell))
                            ServerSend.BombUp(this, cell.Col, cell.Row);

                if (BombUps.Count > 0)
                    foreach (var player in alive.OrderBy(p => p.id))
                    {
                        var cell = WorldToGrid(player.x, player.y);
                        if (!BombUps.Remove(cell)) continue;
                        player.maxActiveBombs = Math.Min(player.maxActiveBombs + 1, ProtocolV5.MaximumCapacity);
                        ServerSend.BombUp(this, cell.col, cell.row, player);
                    }

                foreach (var player in alive)
                    if (player.TryTakeFinalPositionRefresh(now, finalPositionIdleDelay))
                        ServerSend.RefreshFinalPeerPosition(this, player);
                return;
            }

            WinnerId = alive.Count == 1 ? alive[0].id : 0;
            Phase = MatchPhase.Finished;
            bombs.Clear();
            BombUps.Clear();
            ServerSend.MatchState(this);
        }

        private static bool InBounds(int col, int row) =>
            col >= 0 && col < Constants.MAP_WIDTH && row >= 0 && row < Constants.MAP_HEIGHT;

        internal bool CanMove(Player player, float x, float y)
        {
            if (!TerrainCollision.CanSweep(mapData, player.x, player.y, x, y)) return false;
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

        private void InitializeMap()
        {
            Random rand = new Random();
            bombs.Clear();
            BombUps.Clear();

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
            return TerrainCollision.WorldToGrid(worldX, worldY);
        }

        public static (float x, float y) GridToWorld(int col, int row)
        {
            return (col - 8.5f, row - 7.5f);
        }
    }
}
