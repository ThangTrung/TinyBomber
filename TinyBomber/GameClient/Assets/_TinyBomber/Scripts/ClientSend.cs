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

    public static void WelcomeReceived()
    {
        using (Packet _packet = new Packet((int)ClientPackets.welcomeReceived))
        {
            _packet.Write(Client.instance.myId);
            _packet.Write("Người chơi số " + Client.instance.myId);

            SendTCPData(_packet);
        }
    }

    public static void SendPlayerMovement(Vector2 _position)
    {
        using (Packet _packet = new Packet((int)ClientPackets.playerMovement))
        {
            _packet.Write(_position.x);
            _packet.Write(_position.y);
            SendTCPData(_packet);
        }
    }
}