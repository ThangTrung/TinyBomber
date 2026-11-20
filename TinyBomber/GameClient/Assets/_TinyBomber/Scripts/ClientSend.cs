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

    private static void SendUDPData(Packet _packet)
    {
        _packet.WriteLength();
        _packet.EncryptPayload();
        Client.instance.udp.SendData(_packet);
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

    public static void ConfirmUdp(int challenge)
    {
        using (var packet = new Packet((int)ClientPackets.udpReady))
        {
            packet.Write(challenge);
            SendTCPData(packet);
        }
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

    public static void WelcomeReceived()
    {
        using (Packet _packet = new Packet((int)ClientPackets.welcomeReceived))
        {
            _packet.Write(Client.instance.myId);
            _packet.Write("Player_" + Client.instance.myId);
            SendTCPData(_packet);
        }
    }

    public static void SendPlayerMovement(Vector2 _position)
    {
        using (Packet _packet = new Packet((int)ClientPackets.playerMovement))
        {
            _packet.Write(GameManager.instance.RoomId);
            _packet.Write(GameManager.instance.ActiveRoundId);
            _packet.Write(_position.x);
            _packet.Write(_position.y);
            SendUDPData(_packet);
        }
    }
}
