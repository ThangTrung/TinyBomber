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
        using var packet = ProtocolV5.CreateClientHello(slotId, "Player_" + slotId);
        Client.instance.tcp.SendData(packet);
    }

    public static bool SendPlayerMovement(Vector2 position) =>
        GameManager.instance && GameManager.instance.TrySendOrderedMovement(position);

    internal static bool TrySendUdpMovement(long sequence, Vector2 position)
    {
        Client client = Client.instance;
        if (client == null || client.IsConnecting || client.tcp?.SessionToken == null || client.udp == null ||
            !client.tcp.UsesMovementOrdering) return false;

        GameManager game = GameManager.instance;
        using var movement = ProtocolV4.CreateUdpPlayerMovement(client.myId, client.tcp.SessionToken,
            game.RoomId, game.ActiveRoundId, sequence, position.x, position.y);
        return client.udp.TrySendData(movement);
    }

    internal static bool TrySendTcpMovement(long sequence, Vector2 position)
    {
        Client client = Client.instance;
        if (client == null || client.IsConnecting || client.tcp?.SessionToken == null ||
            !client.tcp.UsesMovementOrdering) return false;

        GameManager game = GameManager.instance;
        using var movement = ProtocolV4.CreateTcpPlayerMovement(game.RoomId, game.ActiveRoundId,
            sequence, position.x, position.y);
        return client.tcp.TrySendData(movement);
    }
}
