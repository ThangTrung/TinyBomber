using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace GameShared
{
    /// <summary>
    /// V2 wire contract. Create methods return complete encrypted frames, with a slot prefix on client UDP.
    /// Read methods consume authenticated plaintext positioned at the packet ID, after the frame length.
    /// </summary>
    public static class ProtocolV2
    {
        public const int Version = 2;
        public const int MaxUsernameBytes = 64;
        public const int SessionTokenBytes = 16;
        public const int ProbeNonceBytes = 16;

        private const int UdpRegistrationWireBytes = 88;
        private const int UdpMovementWireBytes = 104;
        // Probe and position have different payload sizes but occupy the same number of AES blocks.
        private const int ServerUdpWireBytes = 100;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        public static byte[] NewSessionToken() => RandomBytes(SessionTokenBytes);

        public static byte[] NewProbeNonce() => RandomBytes(ProbeNonceBytes);

        public static Packet CreateServerHello(int slotId, int version = Version)
        {
            var packet = new Packet((int)ServerPackets.serverHello);
            packet.Write(version);
            packet.Write(slotId);
            return Seal(packet);
        }

        // Bootstrap readers return the advertised version. Negotiation belongs to the caller.
        public static (int Version, int SlotId) ReadServerHello(Packet packet)
        {
            ReadHeader(packet, (int)ServerPackets.serverHello, 2 * sizeof(int));
            return (packet.ReadInt(), packet.ReadInt());
        }

        public static Packet CreateClientHello(int slotId, string username, int version = Version)
        {
            if (string.IsNullOrWhiteSpace(username) || StrictUtf8.GetByteCount(username) > MaxUsernameBytes)
                throw new ArgumentException("Username must contain a non-whitespace character and fit in 1–64 UTF-8 bytes.", nameof(username));

            var packet = new Packet((int)ClientPackets.clientHello);
            packet.Write(version);
            packet.Write(slotId);
            packet.Write(username);
            return Seal(packet);
        }

        public static (int Version, int SlotId, string Username) ReadClientHello(Packet packet)
        {
            ReadHeader(packet, (int)ClientPackets.clientHello, 3 * sizeof(int), variableLength: true);
            int version = packet.ReadInt();
            int slotId = packet.ReadInt();
            int byteCount = packet.ReadInt(false);
            if (byteCount < 1 || byteCount > MaxUsernameBytes || byteCount != packet.UnreadLength() - sizeof(int))
                throw new InvalidDataException("Invalid ClientHello username length.");

            string username = packet.ReadString();
            if (string.IsNullOrWhiteSpace(username))
                throw new InvalidDataException("Username must contain a non-whitespace character.");

            return (version, slotId, username);
        }

        public static Packet CreateSessionGranted(byte[] token) =>
            Seal(CreateTokenPacket((int)ServerPackets.sessionGranted, token));

        public static byte[] ReadSessionGranted(Packet packet) =>
            ReadTokenPacket(packet, (int)ServerPackets.sessionGranted);

        public static Packet CreateUdpRegistration(int slotId, byte[] token) =>
            Seal(CreateTokenPacket((int)ClientPackets.udpRegistration, token), slotId);

        public static byte[] ReadUdpRegistration(Packet packet) =>
            ReadTokenPacket(packet, (int)ClientPackets.udpRegistration);

        public static Packet CreateUdpProbe(byte[] token, byte[] nonce) =>
            CreateProbePacket((int)ServerPackets.udpProbeV2, token, nonce);

        public static (byte[] Token, byte[] Nonce) ReadUdpProbe(Packet packet) =>
            ReadProbePacket(packet, (int)ServerPackets.udpProbeV2);

        public static Packet CreateUdpConfirm(byte[] token, byte[] nonce) =>
            CreateProbePacket((int)ClientPackets.udpConfirm, token, nonce);

        public static (byte[] Token, byte[] Nonce) ReadUdpConfirm(Packet packet) =>
            ReadProbePacket(packet, (int)ClientPackets.udpConfirm);

        public static Packet CreateLobbyReady() => Seal(new Packet((int)ServerPackets.lobbyReady));

        public static void ReadLobbyReady(Packet packet) => ReadHeader(packet, (int)ServerPackets.lobbyReady, 0);

        public static Packet CreateUdpPlayerMovement(int slotId, byte[] token, int roomId, int roundId, float x, float y)
        {
            Packet packet = CreateTokenPacket((int)ClientPackets.udpPlayerMovement, token);
            packet.Write(roomId);
            packet.Write(roundId);
            packet.Write(x);
            packet.Write(y);
            return Seal(packet, slotId);
        }

        public static (byte[] Token, int RoomId, int RoundId, float X, float Y) ReadUdpPlayerMovement(Packet packet)
        {
            ReadHeader(packet, (int)ClientPackets.udpPlayerMovement, SessionTokenBytes + 2 * sizeof(int) + 2 * sizeof(float));
            return (packet.ReadBytes(SessionTokenBytes), packet.ReadInt(), packet.ReadInt(), packet.ReadFloat(), packet.ReadFloat());
        }

        public static Packet CreateUdpPlayerPosition(byte[] token, int roomId, int roundId, int playerId, float x, float y)
        {
            Packet packet = CreateTokenPacket((int)ServerPackets.udpPlayerPosition, token);
            packet.Write(roomId);
            packet.Write(roundId);
            packet.Write(playerId);
            packet.Write(x);
            packet.Write(y);
            return Seal(packet);
        }

        public static (byte[] Token, int RoomId, int RoundId, int PlayerId, float X, float Y) ReadUdpPlayerPosition(Packet packet)
        {
            ReadHeader(packet, (int)ServerPackets.udpPlayerPosition, SessionTokenBytes + 3 * sizeof(int) + 2 * sizeof(float));
            return (packet.ReadBytes(SessionTokenBytes), packet.ReadInt(), packet.ReadInt(), packet.ReadInt(), packet.ReadFloat(), packet.ReadFloat());
        }

        /// <summary>Opens a client UDP frame. The slot prefix only selects a candidate connection.</summary>
        public static Packet OpenClientDatagram(byte[] datagram, out int slotId)
        {
            if (datagram.Length != UdpRegistrationWireBytes && datagram.Length != UdpMovementWireBytes)
                throw new InvalidDataException("Invalid client datagram size.");

            Packet packet = OpenFrame(datagram.AsSpan(sizeof(int)).ToArray());
            slotId = BinaryPrimitives.ReadInt32LittleEndian(datagram);
            return packet;
        }

        /// <summary>Opens a server UDP frame and positions the reader at the packet ID.</summary>
        public static Packet OpenServerDatagram(byte[] datagram)
        {
            if (datagram.Length != ServerUdpWireBytes)
                throw new InvalidDataException("Invalid server datagram size.");

            return OpenFrame(datagram);
        }

        private static Packet Seal(Packet packet, int? slotId = null)
        {
            packet.WriteLength();
            packet.EncryptPayload();
            if (slotId.HasValue) packet.InsertInt(slotId.Value);
            return packet;
        }

        private static Packet OpenFrame(byte[] frame)
        {
            if (BinaryPrimitives.ReadInt32LittleEndian(frame) != frame.Length - sizeof(int))
                throw new InvalidDataException("UDP frame length does not match the datagram.");

            var packet = new Packet(frame);
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

        private static void ReadHeader(Packet packet, int id, int fieldBytes, bool variableLength = false)
        {
            int expectedBytes = sizeof(int) + fieldBytes;
            int available = packet.UnreadLength();
            if (available < expectedBytes || (!variableLength && available != expectedBytes))
                throw new InvalidDataException("Invalid v2 packet payload length.");
            if (packet.ReadInt() != id)
                throw new InvalidDataException("Unexpected v2 packet ID.");
        }

        private static Packet CreateTokenPacket(int id, byte[] token)
        {
            RequireSize(token, SessionTokenBytes, nameof(token));
            var packet = new Packet(id);
            packet.Write(token);
            return packet;
        }

        private static byte[] ReadTokenPacket(Packet packet, int id)
        {
            ReadHeader(packet, id, SessionTokenBytes);
            return packet.ReadBytes(SessionTokenBytes);
        }

        private static Packet CreateProbePacket(int id, byte[] token, byte[] nonce)
        {
            RequireSize(nonce, ProbeNonceBytes, nameof(nonce));
            Packet packet = CreateTokenPacket(id, token);
            packet.Write(nonce);
            return Seal(packet);
        }

        private static (byte[] Token, byte[] Nonce) ReadProbePacket(Packet packet, int id)
        {
            ReadHeader(packet, id, SessionTokenBytes + ProbeNonceBytes);
            return (packet.ReadBytes(SessionTokenBytes), packet.ReadBytes(ProbeNonceBytes));
        }

        private static void RequireSize(byte[] bytes, int size, string paramName)
        {
            if (bytes == null || bytes.Length != size)
                throw new ArgumentException($"Expected exactly {size} bytes.", paramName);
        }

        private static byte[] RandomBytes(int size)
        {
            var bytes = new byte[size];
            RandomNumberGenerator.Fill(bytes);
            return bytes;
        }
    }
}
