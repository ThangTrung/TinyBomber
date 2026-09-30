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

        private static void SendUDPData(int _toClient, Packet _packet)
        {
            Client client = Server.clients[_toClient];

            // Check if UDP is ready
            if (!client.udp.IsConnected())
            {
                Console.WriteLine($"[WARNING] UDP not connected for Client {_toClient}, using TCP fallback");
                SendTCPData(_toClient, _packet);
                return;
            }

            try
            {
                _packet.WriteLength();
                _packet.EncryptPayload();  // Encrypt UDP packets same as TCP
                client.udp.SendData(_packet);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] UDP send failed for Client {_toClient}: {ex.Message}, fallback to TCP");
                // Fallback to TCP
                _packet.WriteLength();
                _packet.EncryptPayload();
                client.tcp.SendData(_packet);
            }
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

        public static void PlayerPositionUDP(int _toClient, float _posX, float _posY)
        {
            using (Packet _packet = new Packet((int)ServerPackets.playerPosition))
            {
                _packet.Write(_posX);
                _packet.Write(_posY);
                SendUDPData(_toClient, _packet);  // Try UDP first
            }
        }

        public static void SpawnPlayerTCP(int _toClient, int _spawnClientId)
        {
            using (Packet _packet = new Packet((int)ServerPackets.spawnPlayer))
            {
                _packet.Write(_spawnClientId);
                SendTCPData(_toClient, _packet);  // Always TCP for critical events
            }
        }

        public static void PlayerDisconnectedTCP(int _toClient, int _disconnectedClientId)
        {
            using (Packet _packet = new Packet((int)ServerPackets.playerDisconnected))
            {
                _packet.Write(_disconnectedClientId);
                SendTCPData(_toClient, _packet);  // Always TCP for critical events
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
                SendTCPDataToAll(_playerId, _packet);
            }
        }
    }
}