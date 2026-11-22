using System;
using System.Buffers.Binary;
using System.IO;

namespace GameShared
{
    /// <summary>
    /// V4 movement wire contract. Writers seal complete frames. Readers start at the packet ID
    /// after authentication. Bootstrap readers remain in ProtocolV2 and callers compare Version
    /// before dispatch. Registration, heartbeat and UDP health retain their existing codecs.
    /// </summary>
    public static class ProtocolV4
    {
        public const int Version = 4;
        private const int MovementBytes = 2 * sizeof(int) + sizeof(long) + 2 * sizeof(float);
        private const int PositionBytes = MovementBytes + sizeof(int);
        private const int CorrectionBytes = PositionBytes + sizeof(long);
        private const int ServerUdpPositionWireBytes = 116;

        public static Packet CreateServerHello(int slotId) => ProtocolV2.CreateServerHello(slotId, Version);

        public static Packet CreateClientHello(int slotId, string username) =>
            ProtocolV2.CreateClientHello(slotId, username, Version);

        /// <summary>
        /// Both transports share one sequence per player/round. Use 0 before the first send.
        /// False means the stream is exhausted. A new room/round/session scope starts at 1.
        /// </summary>
        public static bool TryGetNextMovementSequence(long lastSequence, out long nextSequence)
        {
            if (lastSequence < 0) throw new ArgumentOutOfRangeException(nameof(lastSequence));
            if (lastSequence == long.MaxValue)
            {
                nextSequence = 0;
                return false;
            }

            nextSequence = lastSequence + 1;
            return true;
        }

        public static Packet CreateTcpPlayerMovement(int roomId, int roundId, long moveSeq, float x, float y)
        {
            RequirePositive(moveSeq, nameof(moveSeq));
            var packet = new Packet((int)ClientPackets.playerMovementV4);
            WriteMovement(packet, roomId, roundId, moveSeq, x, y);
            return Seal(packet);
        }

        public static Packet CreateUdpPlayerMovement(int slotId, byte[] token, int roomId, int roundId,
            long moveSeq, float x, float y)
        {
            RequirePositive(moveSeq, nameof(moveSeq));
            var packet = CreateTokenPacket((int)ClientPackets.udpPlayerMovementV4, token);
            WriteMovement(packet, roomId, roundId, moveSeq, x, y);
            return Seal(packet, slotId);
        }

        // Runtime checks scope before advancing lastProcessedMoveSeq. Stale/duplicate moves have
        // no gameplay effects. A new in-scope move advances it even if collision or speed rejects.
        public static (int RoomId, int RoundId, long MoveSeq, float X, float Y) ReadTcpPlayerMovement(Packet packet)
        {
            ReadHeader(packet, (int)ClientPackets.playerMovementV4, MovementBytes);
            return ReadMovement(packet);
        }

        // The client UDP wrapper has the same wire size as v2 movement. Use ProtocolV2.OpenClientDatagram.
        public static (byte[] Token, int RoomId, int RoundId, long MoveSeq, float X, float Y)
            ReadUdpPlayerMovement(Packet packet)
        {
            ReadHeader(packet, (int)ClientPackets.udpPlayerMovementV4, ProtocolV2.SessionTokenBytes + MovementBytes);
            byte[] token = packet.ReadBytes(ProtocolV2.SessionTokenBytes);
            var (roomId, roundId, moveSeq, x, y) = ReadMovement(packet);
            return (token, roomId, roundId, moveSeq, x, y);
        }

        public static Packet CreateTcpPlayerPosition(int roomId, int roundId, int playerId,
            long acceptedMoveSeq, float x, float y)
        {
            RequirePositive(acceptedMoveSeq, nameof(acceptedMoveSeq));
            var packet = new Packet((int)ServerPackets.playerPositionV4);
            WritePosition(packet, roomId, roundId, playerId, acceptedMoveSeq, x, y);
            return Seal(packet);
        }

        public static Packet CreateUdpPlayerPosition(byte[] token, int roomId, int roundId, int playerId,
            long acceptedMoveSeq, float x, float y)
        {
            RequirePositive(acceptedMoveSeq, nameof(acceptedMoveSeq));
            var packet = CreateTokenPacket((int)ServerPackets.udpPlayerPositionV4, token);
            WritePosition(packet, roomId, roundId, playerId, acceptedMoveSeq, x, y);
            return Seal(packet);
        }

        // Peers compare acceptedMoveSeq within the sender's current room/round, across both transports.
        public static (int RoomId, int RoundId, int PlayerId, long AcceptedMoveSeq, float X, float Y)
            ReadTcpPlayerPosition(Packet packet)
        {
            ReadHeader(packet, (int)ServerPackets.playerPositionV4, PositionBytes);
            return ReadPosition(packet);
        }

        public static (byte[] Token, int RoomId, int RoundId, int PlayerId, long AcceptedMoveSeq, float X, float Y)
            ReadUdpPlayerPosition(Packet packet)
        {
            ReadHeader(packet, (int)ServerPackets.udpPlayerPositionV4, ProtocolV2.SessionTokenBytes + PositionBytes);
            byte[] token = packet.ReadBytes(ProtocolV2.SessionTokenBytes);
            var (roomId, roundId, playerId, acceptedMoveSeq, x, y) = ReadPosition(packet);
            return (token, roomId, roundId, playerId, acceptedMoveSeq, x, y);
        }

        /// <summary>
        /// An owner-only TCP snap after rejecting processedMoveSeq. The position belongs to
        /// lastAcceptedMoveSeq, or to spawn when lastAcceptedMoveSeq is 0.
        /// </summary>
        public static Packet CreateTcpPlayerCorrection(int roomId, int roundId, int playerId,
            long processedMoveSeq, long lastAcceptedMoveSeq, float x, float y)
        {
            RequirePositive(processedMoveSeq, nameof(processedMoveSeq));
            if (lastAcceptedMoveSeq < 0 || lastAcceptedMoveSeq >= processedMoveSeq)
                throw new ArgumentOutOfRangeException(nameof(lastAcceptedMoveSeq));

            var packet = new Packet((int)ServerPackets.playerCorrectionV4);
            packet.Write(roomId);
            packet.Write(roundId);
            packet.Write(playerId);
            WriteSequence(packet, processedMoveSeq);
            WriteSequence(packet, lastAcceptedMoveSeq);
            packet.Write(x);
            packet.Write(y);
            return Seal(packet);
        }

        public static (int RoomId, int RoundId, int PlayerId, long ProcessedMoveSeq, long LastAcceptedMoveSeq,
            float X, float Y) ReadTcpPlayerCorrection(Packet packet)
        {
            ReadHeader(packet, (int)ServerPackets.playerCorrectionV4, CorrectionBytes);
            int roomId = packet.ReadInt();
            int roundId = packet.ReadInt();
            int playerId = packet.ReadInt();
            long processedMoveSeq = ReadPositiveSequence(packet);
            long lastAcceptedMoveSeq = ReadSequence(packet);
            if (lastAcceptedMoveSeq < 0 || lastAcceptedMoveSeq >= processedMoveSeq)
                throw new InvalidDataException("Invalid last accepted movement sequence in correction.");

            return (roomId, roundId, playerId, processedMoveSeq, lastAcceptedMoveSeq,
                packet.ReadFloat(), packet.ReadFloat());
        }

        /// <summary>Opens v4 peer position and existing probe/health server datagram envelopes.</summary>
        public static Packet OpenServerDatagram(byte[] datagram)
        {
            if (datagram.Length != ServerUdpPositionWireBytes)
                return ProtocolV3.OpenServerDatagram(datagram);

            if (BinaryPrimitives.ReadInt32LittleEndian(datagram) != datagram.Length - sizeof(int))
                throw new InvalidDataException("UDP frame length does not match the datagram.");

            var packet = new Packet(datagram);
            try
            {
                packet.DecryptPayload();
                packet.ReadInt();
                return packet;
            }
            catch (Exception ex)
            {
                packet.Dispose();
                throw new InvalidDataException("Invalid encrypted UDP frame.", ex);
            }
        }

        private static Packet CreateTokenPacket(int packetId, byte[] token)
        {
            if (token == null || token.Length != ProtocolV2.SessionTokenBytes)
                throw new ArgumentException("Expected exactly 16 session token bytes.", nameof(token));

            var packet = new Packet(packetId);
            packet.Write(token);
            return packet;
        }

        private static Packet Seal(Packet packet, int? slotId = null)
        {
            packet.WriteLength();
            packet.EncryptPayload();
            if (slotId.HasValue) packet.InsertInt(slotId.Value);
            return packet;
        }

        private static void ReadHeader(Packet packet, int packetId, int fieldBytes)
        {
            if (packet.UnreadLength() != sizeof(int) + fieldBytes || packet.ReadInt() != packetId)
                throw new InvalidDataException("Invalid v4 movement packet ID or payload length.");
        }

        private static void WriteMovement(Packet packet, int roomId, int roundId, long moveSeq, float x, float y)
        {
            packet.Write(roomId);
            packet.Write(roundId);
            WriteSequence(packet, moveSeq);
            packet.Write(x);
            packet.Write(y);
        }

        private static (int RoomId, int RoundId, long MoveSeq, float X, float Y) ReadMovement(Packet packet) =>
            (packet.ReadInt(), packet.ReadInt(), ReadPositiveSequence(packet), packet.ReadFloat(), packet.ReadFloat());

        private static void WritePosition(Packet packet, int roomId, int roundId, int playerId,
            long acceptedMoveSeq, float x, float y)
        {
            packet.Write(roomId);
            packet.Write(roundId);
            packet.Write(playerId);
            WriteSequence(packet, acceptedMoveSeq);
            packet.Write(x);
            packet.Write(y);
        }

        private static (int RoomId, int RoundId, int PlayerId, long AcceptedMoveSeq, float X, float Y)
            ReadPosition(Packet packet) =>
            (packet.ReadInt(), packet.ReadInt(), packet.ReadInt(), ReadPositiveSequence(packet),
                packet.ReadFloat(), packet.ReadFloat());

        private static void RequirePositive(long sequence, string paramName)
        {
            if (sequence <= 0) throw new ArgumentOutOfRangeException(paramName);
        }

        private static void WriteSequence(Packet packet, long sequence)
        {
            var bytes = new byte[sizeof(long)];
            BinaryPrimitives.WriteInt64LittleEndian(bytes, sequence);
            packet.Write(bytes);
        }

        private static long ReadSequence(Packet packet) =>
            BinaryPrimitives.ReadInt64LittleEndian(packet.ReadBytes(sizeof(long)));

        private static long ReadPositiveSequence(Packet packet)
        {
            long sequence = ReadSequence(packet);
            if (sequence <= 0) throw new InvalidDataException("Movement sequence must be positive.");
            return sequence;
        }
    }
}
