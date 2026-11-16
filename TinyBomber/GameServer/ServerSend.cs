using System;
using GameShared;

namespace GameServer
{
    class ServerSend
    {
        private static void SendTCPData(int _toClient, Packet _packet)
        {
            _packet.WriteLength();
            _packet.EncryptPayload();
            Server.clients[_toClient].tcp.SendData(_packet);
        }

        private static void SendUDPDataToAll(int _exceptClient, Packet _packet)
        {
            _packet.WriteLength();
            _packet.EncryptPayload();

            for (int i = 1; i <= Server.MaxPlayers; i++)
            {
                if (i != _exceptClient && Server.clients[i].tcp.socket != null)
                {
                    if (Server.clients[i].udp.IsConnected())
                    {
                        Server.clients[i].udp.SendData(_packet);
                    }
                    else
                    {
                        Server.clients[i].tcp.SendData(_packet);
                    }
                }
            }
        }

        public static void Welcome(int _toClient, string _msg)
        {
            using (Packet _packet = new Packet((int)ServerPackets.welcome))
            {
                _packet.Write(_msg);
                _packet.Write(_toClient);
                SendTCPData(_toClient, _packet);
            }
        }

        public static void SpawnPlayer(int _toClient, int _playerToSpawnId, float _x, float _y)
        {
            using (Packet _packet = new Packet((int)ServerPackets.spawnPlayer))
            {
                _packet.Write(_playerToSpawnId);
                _packet.Write(_x);
                _packet.Write(_y);
                SendTCPData(_toClient, _packet);
            }
        }

        public static void PlayerDisconnected(int id)
        {
            using var packet = new Packet((int)ServerPackets.playerDisconnected);
            packet.Write(id);
            packet.WriteLength();
            packet.EncryptPayload();
            foreach (var slot in Server.clients.Values)
            {
                if (slot.id != id && slot.player != null) slot.tcp.SendData(packet);
            }
        }

        public static void BroadcastPlayerPosition(int _playerId, float _x, float _y)
        {
            using (Packet _packet = new Packet((int)ServerPackets.playerPosition))
            {
                _packet.Write(_playerId);
                _packet.Write(_x);
                _packet.Write(_y);
                SendUDPDataToAll(_playerId, _packet);
            }
        }

        public static void BombPlaced(int col, int row, int? recipient = null)
        {
            using var packet = new Packet((int)ServerPackets.bombPlaced);
            packet.Write(col);
            packet.Write(row);
            if (recipient.HasValue) SendTCPData(recipient.Value, packet);
            else BroadcastTCP(packet);
        }

        public static void BombExploded(int col, int row, List<(int, int)> affected, List<(int, int)> destroyed)
        {
            using var packet = new Packet((int)ServerPackets.bombExploded);
            packet.Write(col);
            packet.Write(row);
            WriteCells(packet, affected);
            WriteCells(packet, destroyed);
            BroadcastTCP(packet);
        }

        private static void WriteCells(Packet packet, List<(int, int)> cells)
        {
            packet.Write(cells.Count);
            foreach (var (col, row) in cells)
            {
                packet.Write(col);
                packet.Write(row);
            }
        }

        private static void BroadcastTCP(Packet packet)
        {
            packet.WriteLength();
            packet.EncryptPayload();
            foreach (var slot in Server.clients.Values)
                if (slot.player != null) slot.tcp.SendData(packet);
        }

        public static void SendMapData(int _toClient)
        {
            using (Packet _packet = new Packet((int)ServerPackets.mapData))
            {
                for (int col = 0; col < Constants.MAP_WIDTH; col++)
                {
                    for (int row = 0; row < Constants.MAP_HEIGHT; row++)
                    {
                        _packet.Write(GameLogic.mapData[col, row]);
                    }
                }
                SendTCPData(_toClient, _packet);
            }
        }

        public static void SnapPlayerPosition(int _toClient, float _x, float _y)
        {
            using (Packet _packet = new Packet((int)ServerPackets.playerPosition))
            {
                _packet.Write(_toClient);
                _packet.Write(_x);
                _packet.Write(_y);
                SendTCPData(_toClient, _packet);
            }
        }
    }
}
