using System.IO;

namespace GameShared
{
    public static class ProtocolV5
    {
        public const int Version = 5;
        public const int InitialCapacity = 1;
        public const int MaximumCapacity = 3;

        public static Packet CreateServerHello(int slot) => ProtocolV2.CreateServerHello(slot, Version);
        public static Packet CreateClientHello(int slot, string name) => ProtocolV2.CreateClientHello(slot, name, Version);

        // Called after TCP authentication, with the reader positioned after the packet ID.
        public static (int Room, int Round, int Col, int Row, int Actor, int Capacity) ReadBombUp(Packet packet, ServerPackets type)
        {
            bool collected = type == ServerPackets.bombUpCollected;
            if ((!collected && type != ServerPackets.bombUpSpawned) ||
                packet.UnreadLength() != (collected ? 6 : 4) * sizeof(int))
                throw new InvalidDataException("Invalid Bomb Up event length or type.");
            int room = packet.ReadInt(), round = packet.ReadInt(), col = packet.ReadInt(), row = packet.ReadInt();
            int actor = collected ? packet.ReadInt() : 0;
            int capacity = collected ? packet.ReadInt() : 0;
            if (room <= 0 || round <= 0 || col < 0 || col >= Constants.MAP_WIDTH || row < 0 ||
                row >= Constants.MAP_HEIGHT || (collected && (actor <= 0 || capacity <= InitialCapacity || capacity > MaximumCapacity)))
                throw new InvalidDataException("Invalid Bomb Up event fields.");
            return (room, round, col, row, actor, capacity);
        }
    }
}
