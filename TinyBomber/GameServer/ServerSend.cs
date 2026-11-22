using GameShared;

namespace GameServer
{
    static class ServerSend
    {
        private static void SendTCPData(Client.TCP connection, Packet packet)
        {
            Prepare(packet);
            connection.SendData(packet);
        }

        private static void Prepare(Packet packet)
        {
            packet.WriteLength();
            packet.EncryptPayload();
        }

        internal static void ServerHello(Client.TCP connection, int slotId)
        {
            using var packet = ProtocolV4.CreateServerHello(slotId);
            connection.SendData(packet);
        }

        internal static void SessionGranted(Client.TCP connection, byte[] token)
        {
            using var packet = ProtocolV2.CreateSessionGranted(token);
            connection.SendData(packet);
        }

        internal static void UdpProbe(Client slot)
        {
            using var packet = ProtocolV2.CreateUdpProbe(slot.tcp.SessionToken!, slot.udp.ProbeNonce!);
            slot.udp.SendData(packet);
        }

        internal static void LobbyReady(Client slot)
        {
            using var packet = ProtocolV2.CreateLobbyReady();
            slot.tcp.SendData(packet);
        }

        internal static bool HeartbeatPing(Client.TCP connection, long sequence)
        {
            using var packet = ProtocolV3.CreateHeartbeatPing(sequence);
            return connection.SendHeartbeat(packet);
        }

        internal static void UdpHealthEcho(Client.UDP udp, byte[] token, long sequence)
        {
            using var packet = ProtocolV3.CreateUdpHealthEcho(token, sequence);
            udp.SendData(packet);
        }

        internal static void RoomChanged(Client slot, int roomId)
        {
            using var packet = new Packet((int)ServerPackets.roomChanged);
            packet.Write(roomId);
            SendTCPData(slot.tcp, packet);
        }

        internal static void RoomFailure(Client slot, RoomError error)
        {
            using var packet = new Packet((int)ServerPackets.roomError);
            packet.Write((int)error);
            SendTCPData(slot.tcp, packet);
        }

        internal static void MatchState(GameLogic room)
        {
            int readyCount = room.Members.Count(slot => slot.IsReady);
            foreach (var slot in room.Members)
            {
                if (slot.Username == null) continue;

                using var packet = new Packet((int)ServerPackets.matchState);
                WriteRound(packet, room);
                packet.Write((int)room.Phase);
                packet.Write(readyCount);
                packet.Write(room.WinnerId);
                SendTCPData(slot.tcp, packet);
            }
        }

        internal static void RoundBegin(GameLogic room, Client recipient, Client[] participants)
        {
            using var packet = new Packet((int)ServerPackets.roundBegin);
            WriteRound(packet, room);
            packet.Write(participants.Length);
            foreach (var slot in participants) packet.Write(slot.id);
            SendTCPData(recipient.tcp, packet);
        }

        internal static void PlayerEliminated(GameLogic room, int id)
        {
            using var packet = new Packet((int)ServerPackets.playerEliminated);
            WriteRound(packet, room);
            packet.Write(id);
            BroadcastTCP(room, packet);
        }

        internal static void SpawnPlayer(GameLogic room, Client recipient, Player player)
        {
            using var packet = new Packet((int)ServerPackets.spawnPlayer);
            WritePosition(packet, room, player);
            SendTCPData(recipient.tcp, packet);
        }

        internal static void PlayerDisconnected(GameLogic room, int id)
        {
            using var packet = new Packet((int)ServerPackets.playerDisconnected);
            WriteRound(packet, room);
            packet.Write(id);
            BroadcastTCP(room, packet);
        }

        internal static void BroadcastPlayerPosition(GameLogic room, Player player)
        {
            bool ordered = player.LastAcceptedMoveSeq > 0;
            Packet? tcpPosition = null;
            try
            {
                foreach (var slot in room.Members)
                {
                    if (slot.id == player.id || slot.player == null) continue;
                    // Movement formats are activated together. Do not send across versions.
                    if (slot.tcp.UsesMovementOrdering != ordered) continue;

                    if (slot.UdpConfirmed && slot.tcp.SessionToken is byte[] token && slot.udp.endPoint != null)
                    {
                        using var position = ordered
                            ? ProtocolV4.CreateUdpPlayerPosition(token, room.Id, room.RoundId, player.id,
                                player.LastAcceptedMoveSeq, player.x, player.y)
                            : ProtocolV2.CreateUdpPlayerPosition(token, room.Id, room.RoundId, player.id, player.x, player.y);
                        slot.udp.SendData(position);
                    }
                    else
                    {
                        tcpPosition ??= ordered
                            ? ProtocolV4.CreateTcpPlayerPosition(room.Id, room.RoundId, player.id,
                                player.LastAcceptedMoveSeq, player.x, player.y)
                            : CreateLegacyPosition(room, player);
                        slot.tcp.SendData(tcpPosition);
                    }
                }
            }
            finally
            {
                tcpPosition?.Dispose();
            }
        }

        internal static void BombPlaced(GameLogic room, int col, int row)
        {
            using var packet = new Packet((int)ServerPackets.bombPlaced);
            WriteRound(packet, room);
            packet.Write(col);
            packet.Write(row);
            BroadcastTCP(room, packet);
        }

        internal static void BombExploded(GameLogic room, int col, int row,
            List<(int, int)> affected, List<(int, int)> destroyed)
        {
            using var packet = new Packet((int)ServerPackets.bombExploded);
            WriteRound(packet, room);
            packet.Write(col);
            packet.Write(row);
            WriteCells(packet, affected);
            WriteCells(packet, destroyed);
            BroadcastTCP(room, packet);
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

        private static void BroadcastTCP(GameLogic room, Packet packet)
        {
            Prepare(packet);
            foreach (var slot in room.Members)
                if (slot.player != null) slot.tcp.SendData(packet);
        }

        internal static void SendMapData(GameLogic room, Client recipient)
        {
            using var packet = new Packet((int)ServerPackets.mapData);
            WriteRound(packet, room);
            for (int col = 0; col < Constants.MAP_WIDTH; col++)
            {
                for (int row = 0; row < Constants.MAP_HEIGHT; row++)
                    packet.Write(room.mapData[col, row]);
            }
            SendTCPData(recipient.tcp, packet);
        }

        internal static void AcknowledgeMovement(GameLogic room, Client owner, Player player)
        {
            using var packet = ProtocolV4.CreateTcpPlayerPosition(room.Id, room.RoundId, player.id,
                player.LastAcceptedMoveSeq, player.x, player.y);
            owner.tcp.SendData(packet);
        }

        internal static void RefreshFinalPeerPosition(GameLogic room, Player player)
        {
            Packet? position = null;
            try
            {
                foreach (var slot in room.Members)
                {
                    // TCP-only recipients already received this sequence on the reliable path.
                    if (slot.id == player.id || slot.player == null || !slot.tcp.UsesMovementOrdering ||
                        !slot.IsReady || slot.tcp.SessionToken == null || slot.udp.endPoint == null)
                        continue;

                    position ??= ProtocolV4.CreateTcpPlayerPosition(room.Id, room.RoundId, player.id,
                        player.LastAcceptedMoveSeq, player.x, player.y);
                    slot.tcp.SendData(position);
                }
            }
            finally
            {
                position?.Dispose();
            }
        }

        internal static void SnapPlayerPosition(GameLogic room, Client recipient, Player player)
        {
            using var packet = recipient.tcp.UsesMovementOrdering
                ? ProtocolV4.CreateTcpPlayerCorrection(room.Id, room.RoundId, player.id,
                    player.LastProcessedMoveSeq, player.LastAcceptedMoveSeq, player.x, player.y)
                : CreateLegacyPosition(room, player);
            recipient.tcp.SendData(packet);
        }

        private static void WriteRound(Packet packet, GameLogic room)
        {
            packet.Write(room.Id);
            packet.Write(room.RoundId);
        }

        private static Packet CreateLegacyPosition(GameLogic room, Player player)
        {
            var packet = new Packet((int)ServerPackets.playerPosition);
            WritePosition(packet, room, player);
            Prepare(packet);
            return packet;
        }

        private static void WritePosition(Packet packet, GameLogic room, Player player)
        {
            WriteRound(packet, room);
            packet.Write(player.id);
            packet.Write(player.x);
            packet.Write(player.y);
        }
    }
}
