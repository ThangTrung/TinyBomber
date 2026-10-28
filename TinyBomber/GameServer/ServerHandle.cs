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
                    ServerSend.SpawnPlayer(i, _fromClient, 0f, 0f); 
                    ServerSend.SpawnPlayer(_fromClient, i, 0f, 0f); 
                }
            }
        }

        public static void PlayerMovement(int _fromClient, float _x, float _y)
        {
            ServerSend.BroadcastPlayerPosition(_fromClient, _x, _y);
        }
    }
}