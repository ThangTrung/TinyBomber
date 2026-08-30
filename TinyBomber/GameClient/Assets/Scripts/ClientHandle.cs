using UnityEngine;
using GameShared;

public class ClientHandle : MonoBehaviour
{
    public static void Welcome(Packet _packet)
    {
        string _msg = _packet.ReadString();
        int _myId = _packet.ReadInt();

        Debug.Log($"[SERVER NÓI]: {_msg}");

        Client.instance.myId = _myId;

        // welcome received
    }
}