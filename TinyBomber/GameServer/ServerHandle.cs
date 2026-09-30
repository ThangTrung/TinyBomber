using System;
using GameShared;

namespace GameServer
{
    class ServerHandle
    {
        public static void WelcomeReceived(int _fromClient, int _clientIdCheck, string _username)
        {
            Console.WriteLine($"[ĐĂNG NHẬP] Người chơi ID {_fromClient} (Tên: {_username}) đã vào phòng!");

            if (_fromClient != _clientIdCheck)
            {
                Console.WriteLine($"[CẢNH BÁO] ID không khớp! Có dấu hiệu gian lận từ người chơi {_username}");
            }

            ServerSend.SpawnPlayer(_fromClient, _fromClient, 0f, 0f);

            for (int i = 1; i <= Server.MaxPlayers; i++)
            {
                if (i != _fromClient && Server.clients[i].tcp.socket != null)
                {
                    ServerSend.SpawnPlayer(i, _fromClient, 0f, 0f); // Báo cho người cũ
                    ServerSend.SpawnPlayer(_fromClient, i, 0f, 0f); // Gửi người cũ cho người mới
                }
            }
        }

        public static void PlayerMovement(int _fromClient, float _x, float _y)
        {
            ServerSend.BroadcastPlayerPosition(_fromClient, _x, _y);
        }

        public static void HandleUDPPacket(int _fromClient, int _packetId, Packet _packet)
        {
            switch (_packetId)
            {
                case (int)ClientPackets.playerMovement:
                    HandlePlayerMovementUDP(_fromClient, _packet);
                    break;

                default:
                    Console.WriteLine($"[WARNING] Unknown UDP packet ID: {_packetId} from Client {_fromClient}");
                    break;
            }
        }

        private static void HandlePlayerMovementUDP(int _fromClient, Packet _packet)
        {
            try
            {
                // Extract movement data
                float posX = _packet.ReadFloat();
                float posY = _packet.ReadFloat();
                bool isMoving = _packet.ReadBool();

                Console.WriteLine($"[UDP] Player {_fromClient} position: ({posX}, {posY}), moving: {isMoving}");

                // TODO: Update player position in game world
                // TODO: Broadcast to other clients if needed

                // For now, just log successful receipt
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Error handling UDP movement for Client {_fromClient}: {ex.Message}");
            }
        }
    }
}