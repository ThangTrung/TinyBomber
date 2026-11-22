using System;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using GameShared;

namespace GameServer
{
    class ServerHandle
    {
        internal static void ClientHello(Client slot, Client.TCP connection, int version, int slotId,
            string username, long? now = null)
        {
            if (slot.tcp != connection || connection.HandshakeHasExpired(now ?? Stopwatch.GetTimestamp())) return;

            if (version != ProtocolV3.Version || slotId != slot.id)
            {
                Console.WriteLine(version != ProtocolV3.Version
                    ? $"Client protocol version {version} does not match {ProtocolV3.Version}."
                    : $"ClientHello slot does not match connection {slot.id}.");
                Server.Disconnect(slot.id, connection);
                return;
            }

            if (connection.SessionToken != null) return;
            byte[] sessionToken = ProtocolV2.NewSessionToken();
            connection.AcceptedProtocolVersion = version;
            connection.SessionToken = sessionToken;
            slot.Username = username;
            ServerSend.SessionGranted(connection, sessionToken);
        }

        internal static bool RegisterUdp(Client slot, Client.TCP connection, Client.UDP udp, IPEndPoint endpoint,
            byte[] token, long? now = null)
        {
            if (slot.tcp != connection || slot.udp != udp || slot.Username == null || slot.UdpConfirmed ||
                connection.HandshakeHasExpired(now ?? Stopwatch.GetTimestamp()) || !HasToken(connection, token))
                return false;

            if (udp.endPoint == null) udp.Connect(endpoint);
            return endpoint.Equals(udp.endPoint);
        }

        internal static void ConfirmUdp(Client slot, Client.TCP connection, byte[] token, byte[] nonce, long? now = null)
        {
            long appliedAt = now ?? Stopwatch.GetTimestamp();
            if (slot.tcp != connection || slot.Username == null || slot.UdpConfirmed || slot.udp.endPoint == null ||
                slot.udp.ProbeNonce is not byte[] probeNonce || !HasToken(connection, token) ||
                !CryptographicOperations.FixedTimeEquals(probeNonce, nonce) ||
                connection.HandshakeHasExpired(appliedAt)) return;

            slot.UdpConfirmed = true;
            connection.StartHeartbeat(appliedAt);
            ServerSend.LobbyReady(slot);
        }

        internal static void HeartbeatPong(Client slot, Client.TCP connection, long sequence, long? now = null)
        {
            if (slot.tcp != connection || !slot.IsReady) return;
            connection.AcceptHeartbeatPong(sequence, now ?? Stopwatch.GetTimestamp());
        }

        internal static void UdpMovement(Client slot, Client.TCP connection, Client.UDP udp, IPEndPoint endpoint,
            byte[] token, int roomId, int roundId, float x, float y, long? now = null, long? moveSeq = null)
        {
            if (slot.tcp != connection || slot.udp != udp || !slot.IsReady ||
                !endpoint.Equals(udp.endPoint) || !HasToken(connection, token)) return;

            PlayerMovement(slot, roomId, roundId, x, y, now, moveSeq);
        }

        internal static void UdpHealthProbe(Client slot, Client.TCP connection, Client.UDP udp, IPEndPoint endpoint,
            byte[] token, long sequence)
        {
            if (slot.tcp != connection || slot.udp != udp || !slot.IsReady ||
                !endpoint.Equals(udp.endPoint) || !HasToken(connection, token)) return;

            ServerSend.UdpHealthEcho(udp, token, sequence);
        }

        private static bool HasToken(Client.TCP connection, byte[] token) =>
            connection.SessionToken is byte[] expected && CryptographicOperations.FixedTimeEquals(expected, token);

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

        public static void PlayerMovement(Client slot, int roomId, int roundId, float x, float y,
            long? now = null, long? moveSeq = null)
        {
            if (slot.tcp.UsesMovementOrdering != moveSeq.HasValue) return;
            if (slot.Room is not GameLogic room || !room.AcceptsInput(slot, roomId, roundId)) return;

            Player player = slot.player!;
            if (moveSeq is long sequence &&
                (!slot.IsReady || slot.tcp.SessionToken == null || !player.TryMarkMovementProcessed(sequence)))
                return;

            long appliedAt = now ?? Stopwatch.GetTimestamp();
            if (room.CanMove(player, x, y) && player.TrySpendMovementCredit(x, y, appliedAt))
            {
                player.AcceptMovement(x, y, moveSeq);
                ServerSend.BroadcastPlayerPosition(room, player);
            }
            else
            {
                ServerSend.SnapPlayerPosition(room, slot, player);
            }
        }
    }
}
