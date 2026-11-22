using System;
using System.Buffers.Binary;
using System.IO;

namespace GameShared
{
    /// <summary>
    /// V3 bootstrap and health packets. Create methods seal the full wire frame.
    /// Read methods consume authenticated plaintext positioned at the packet ID.
    /// </summary>
    public static class ProtocolV3
    {
        public const int Version = 3;
        private const int UdpHealthEchoWireBytes = 84;

        public static Packet CreateServerHello(int slotId) => ProtocolV2.CreateServerHello(slotId, Version);

        public static Packet CreateClientHello(int slotId, string username) =>
            ProtocolV2.CreateClientHello(slotId, username, Version);

        public static Packet CreateHeartbeatPing(long sequence) =>
            CreateSequenceFrame((int)ServerPackets.heartbeatPing, sequence);

        public static long ReadHeartbeatPing(Packet packet) => ReadHeartbeat(packet, (int)ServerPackets.heartbeatPing);

        public static Packet CreateHeartbeatPong(long sequence) =>
            CreateSequenceFrame((int)ClientPackets.heartbeatPong, sequence);

        public static long ReadHeartbeatPong(Packet packet) => ReadHeartbeat(packet, (int)ClientPackets.heartbeatPong);

        public static Packet CreateUdpHealthProbe(int slotId, byte[] token, long sequence) =>
            CreateUdpHealthFrame((int)ClientPackets.udpHealthProbe, token, sequence, slotId);

        public static (byte[] Token, long Sequence) ReadUdpHealthProbe(Packet packet) =>
            ReadUdpHealth(packet, (int)ClientPackets.udpHealthProbe);

        public static Packet CreateUdpHealthEcho(byte[] token, long sequence) =>
            CreateUdpHealthFrame((int)ServerPackets.udpHealthEcho, token, sequence);

        public static (byte[] Token, long Sequence) ReadUdpHealthEcho(Packet packet) =>
            ReadUdpHealth(packet, (int)ServerPackets.udpHealthEcho);

        /// <summary>
        /// Opens a server datagram for v3 dispatch. Client datagrams use the same envelope sizes as v2.
        /// </summary>
        public static Packet OpenServerDatagram(byte[] datagram)
        {
            if (datagram.Length != UdpHealthEchoWireBytes)
                return ProtocolV2.OpenServerDatagram(datagram);

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

        private static Packet CreateUdpHealthFrame(int packetId, byte[] token, long sequence, int? slotId = null)
        {
            if (token == null || token.Length != ProtocolV2.SessionTokenBytes)
                throw new ArgumentException("Expected exactly 16 session token bytes.", nameof(token));

            return CreateSequenceFrame(packetId, sequence, token, slotId);
        }

        private static Packet CreateSequenceFrame(int packetId, long sequence, byte[]? token = null, int? slotId = null)
        {
            if (sequence <= 0)
                throw new ArgumentOutOfRangeException(nameof(sequence), "Health sequence must be positive.");

            var sequenceBytes = new byte[sizeof(long)];
            BinaryPrimitives.WriteInt64LittleEndian(sequenceBytes, sequence);
            var packet = new Packet(packetId);
            if (token != null) packet.Write(token);
            packet.Write(sequenceBytes);
            packet.WriteLength();
            packet.EncryptPayload();
            if (slotId.HasValue) packet.InsertInt(slotId.Value);
            return packet;
        }

        private static long ReadHeartbeat(Packet packet, int packetId)
        {
            ReadHeader(packet, packetId, sizeof(long));
            return ReadSequence(packet);
        }

        private static (byte[] Token, long Sequence) ReadUdpHealth(Packet packet, int packetId)
        {
            ReadHeader(packet, packetId, ProtocolV2.SessionTokenBytes + sizeof(long));
            return (packet.ReadBytes(ProtocolV2.SessionTokenBytes), ReadSequence(packet));
        }

        private static void ReadHeader(Packet packet, int packetId, int fieldBytes)
        {
            if (packet.UnreadLength() != sizeof(int) + fieldBytes || packet.ReadInt() != packetId)
                throw new InvalidDataException("Invalid health packet ID or payload length.");
        }

        private static long ReadSequence(Packet packet)
        {
            long sequence = BinaryPrimitives.ReadInt64LittleEndian(packet.ReadBytes(sizeof(long)));
            if (sequence <= 0)
                throw new InvalidDataException("Health sequence must be positive.");

            return sequence;
        }
    }
}
