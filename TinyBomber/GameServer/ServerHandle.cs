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

            Client slot = Server.clients[_fromClient];
            if (slot.Username != null) return;
            slot.Username = _username;
            ServerSend.MatchState();
        }

        internal static void ConfirmUdp(Client slot, int challenge)
        {
            if (slot.Username == null || slot.UdpConfirmed || slot.udp.endPoint == null ||
                challenge != slot.tcp.UdpChallenge) return;

            slot.UdpConfirmed = true;
            ServerSend.MatchState();
        }

        public static void PlayerMovement(int _fromClient, int roundId, float _x, float _y)
        {
            Player? player = Server.clients[_fromClient].player;
            if (player == null || !GameLogic.AcceptsInput(player, roundId)) return;

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
