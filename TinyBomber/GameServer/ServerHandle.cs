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
            slot.Username = _username;
            Console.WriteLine($"[ĐĂNG NHẬP] Người chơi ID {slot.id} (Tên: {_username}) đã kết nối.");
        }

        internal static void ConfirmUdp(Client slot, int challenge)
        {
            if (slot.Username == null || slot.UdpConfirmed || slot.udp.endPoint == null ||
                challenge != slot.tcp.UdpChallenge) return;

            slot.UdpConfirmed = true;
            ServerSend.LobbyReady(slot);
        }

        internal static void CreateRoom(Client slot)
        {
            if (!CanEnterRoom(slot)) return;
            EnterRoom(slot, Server.CreateRoom());
        }

        internal static void JoinRoom(Client slot, int roomId)
        {
            if (!CanEnterRoom(slot)) return;

            GameLogic? room = Server.FindRoom(roomId);
            if (room == null)
            {
                ServerSend.RoomFailure(slot, RoomError.RoomNotFound);
                return;
            }

            EnterRoom(slot, room);
        }

        internal static void LeaveRoom(Client slot, int roomId)
        {
            if (slot.Room is not GameLogic room || room.Id != roomId)
            {
                ServerSend.RoomFailure(slot, RoomError.RoomMismatch);
                return;
            }

            Server.RemoveFromRoom(slot);
            ServerSend.RoomChanged(slot, 0);
        }

        private static bool CanEnterRoom(Client slot)
        {
            if (!slot.IsReady)
            {
                ServerSend.RoomFailure(slot, RoomError.NotReady);
                return false;
            }

            if (slot.Room == null) return true;

            ServerSend.RoomFailure(slot, RoomError.AlreadyInRoom);
            return false;
        }

        private static void EnterRoom(Client slot, GameLogic room)
        {
            if (!room.TryAddMember(slot))
            {
                ServerSend.RoomFailure(slot, RoomError.RoomFull);
                return;
            }

            ServerSend.RoomChanged(slot, room.Id);
            ServerSend.MatchState(room);
        }

        public static void PlayerMovement(Client slot, int roomId, int roundId, float _x, float _y)
        {
            if (slot.Room is not GameLogic room || !room.AcceptsInput(slot, roomId, roundId)) return;

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
