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

        private static void SendTCPDataToAll(int _exceptClient, Packet _packet)
        {
            _packet.WriteLength();
            _packet.EncryptPayload();
            for (int i = 1; i <= Server.MaxPlayers; i++)
            {
                if (i != _exceptClient && Server.clients[i].tcp.socket != null)
                {
                    Server.clients[i].tcp.SendData(_packet);
                }
            }
        }

        private static void SendTCPDataToAll(Packet _packet)
        {
            _packet.WriteLength();
            _packet.EncryptPayload();
            for (int i = 1; i <= Server.MaxPlayers; i++)
            {
                if (Server.clients[i].tcp.socket != null)
                {
                    Server.clients[i].tcp.SendData(_packet);
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

        public static void BroadcastPlayerPosition(int _playerId, float _x, float _y)
        {
            using (Packet _packet = new Packet((int)ServerPackets.playerPosition))
            {
                _packet.Write(_playerId);
                _packet.Write(_x);
                _packet.Write(_y);
                // Gửi cho tất cả trừ người vừa gửi tọa độ
                SendTCPDataToAll(_playerId, _packet);
            }
        }
    }
}