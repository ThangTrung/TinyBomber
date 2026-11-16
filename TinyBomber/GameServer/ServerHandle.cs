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
                return;
            }

            ServerSend.SendMapData(_fromClient);
            GameLogic.SendActiveBombs(_fromClient);
            Player newPlayer = new Player(_fromClient, _username);
            Server.clients[_fromClient].player = newPlayer;

            // Báo cho chính người đó vị trí sinh ra (Lấy từ x, y của class Player)
            ServerSend.SpawnPlayer(_fromClient, _fromClient, newPlayer.x, newPlayer.y);

            // Đồng bộ với người chơi khác
            for (int i = 1; i <= Server.MaxPlayers; i++)
            {
                if (i != _fromClient && Server.clients[i].player is Player other)
                {
                    ServerSend.SpawnPlayer(i, _fromClient, newPlayer.x, newPlayer.y);
                    ServerSend.SpawnPlayer(_fromClient, i, other.x, other.y);
                }
            }
        }

        public static void PlayerMovement(int _fromClient, float _x, float _y)
        {
            Player? player = Server.clients[_fromClient].player;
            if (player == null || !player.isAlive) return;

            if (GameLogic.CanMove(player, _x, _y))
            {
                player.x = _x;
                player.y = _y;
                ServerSend.BroadcastPlayerPosition(_fromClient, _x, _y);
            }
            else
            {
                ServerSend.SnapPlayerPosition(_fromClient, player.x, player.y);
            }
        }
    }
}
