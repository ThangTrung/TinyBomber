using UnityEngine;
using GameShared;

public class ClientSend : MonoBehaviour
{
    private static void SendTCPData(Packet _packet)
    {
        _packet.WriteLength();
        _packet.EncryptPayload();
        Client.instance.tcp.SendData(_packet);
    }

    public static void PlaceBomb()
    {
        using (var packet = new Packet((int)ClientPackets.placeBomb))
        {
            packet.Write(GameManager.instance.RoomId);
            packet.Write(GameManager.instance.ActiveRoundId);
            SendTCPData(packet);
        }
    }

    public static void ConfirmUdp(byte[] token, byte[] nonce)
    {
        using var packet = ProtocolV2.CreateUdpConfirm(token, nonce);
        Client.instance.tcp.SendData(packet);
    }

    public static void RequestStart(int roomId, int roundId)
    {
        using (var packet = new Packet((int)ClientPackets.requestStart))
        {
            packet.Write(roomId);
            packet.Write(roundId);
            SendTCPData(packet);
        }
    }

    public static void CreateRoom()
    {
        using var packet = new Packet((int)ClientPackets.createRoom);
        SendTCPData(packet);
    }

    public static void JoinRoom(int roomId)
    {
        using var packet = new Packet((int)ClientPackets.joinRoom);
        packet.Write(roomId);
        SendTCPData(packet);
    }

    public static void LeaveRoom(int roomId)
    {
        using var packet = new Packet((int)ClientPackets.leaveRoom);
        packet.Write(roomId);
        SendTCPData(packet);
    }

    public static void SendClientHello()
    {
        int slotId = Client.instance.myId;
        using var packet = ProtocolV3.CreateClientHello(slotId, "Player_" + slotId);
        Client.instance.tcp.SendData(packet);
    }

    public static void SendPlayerMovement(Vector2 _position)
    {
        Client client = Client.instance;
        if (client == null || client.IsConnecting || client.tcp?.SessionToken == null || client.udp == null) return;

#if TINYBOMBER_PROTOCOL_V4
        // Compile with a v4 GameShared reference for validation. Hello still selects v3 in C3-C1.
        if (client.tcp.UsesMovementOrdering)
        {
            GameManager game = GameManager.instance;
            game.TrySendOrderedMovement(sequence =>
            {
                using var ordered = ProtocolV4.CreateUdpPlayerMovement(client.myId, client.tcp.SessionToken,
                    game.RoomId, game.ActiveRoundId, sequence, _position.x, _position.y);
                return client.udp.TrySendData(ordered);
            });
            return;
        }
#endif
        using var packet = ProtocolV2.CreateUdpPlayerMovement(client.myId, client.tcp.SessionToken,
            GameManager.instance.RoomId, GameManager.instance.ActiveRoundId, _position.x, _position.y);
        client.udp.SendData(packet);
    }
}
