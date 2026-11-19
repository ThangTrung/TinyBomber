using System;
using GameShared;

namespace GameServer
{
    class ServerHandle
    {
        public static void WelcomeReceived(Client slot, int _clientIdCheck, string _username)
        {
            if (slot.id != _clientIdCheck)
            {
                Console.WriteLine($"[CẢNH BÁO] ID không khớp! Có dấu hiệu gian lận từ người chơi {_username}");
                return;
            }

            if (slot.Username != null) return;
            if (slot.Room == null && !Server.DefaultRoom.TryAddMember(slot))
            {
                Server.Disconnect(slot.id, slot.tcp);
                return;
            }

            slot.Username = _username;
            Console.WriteLine($"[ĐĂNG NHẬP] Người chơi ID {slot.id} (Tên: {_username}) đã vào phòng!");
            ServerSend.MatchState(slot.Room!);
        }

        internal static void ConfirmUdp(Client slot, int challenge)
        {
            if (slot.Room is not GameLogic room || slot.Username == null ||
                slot.UdpConfirmed || slot.udp.endPoint == null ||
                challenge != slot.tcp.UdpChallenge) return;

            slot.UdpConfirmed = true;
            ServerSend.MatchState(room);
        }

        public static void PlayerMovement(Client slot, int roundId, float _x, float _y)
        {
            if (slot.Room is not GameLogic room || !room.AcceptsInput(slot, roundId)) return;

            Player player = slot.player!;
            if (room.CanMove(player, _x, _y))
            {
                player.x = _x;
                player.y = _y;
                ServerSend.BroadcastPlayerPosition(room, player);
            }
            else
            {
                ServerSend.SnapPlayerPosition(room, slot, player);
            }
        }
    }
}
