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

        ClientSend.WelcomeReceived();
    }

    public static void SpawnPlayer(Packet _packet)
    {
        int _id = _packet.ReadInt();
        float _x = _packet.ReadFloat();
        float _y = _packet.ReadFloat();

        Debug.Log($"Sinh nhân vật ID: {_id} tại ({_x}, {_y})");
        GameManager.instance.SpawnPlayer(_id, new Vector2(_x, _y));
    }

    public static void PlayerPosition(Packet _packet)
    {
        int _id = _packet.ReadInt();
        float _x = _packet.ReadFloat();
        float _y = _packet.ReadFloat();

        if (GameManager.players.ContainsKey(_id))
        {
            RemotePlayerController remoteControl = GameManager.players[_id].GetComponent<RemotePlayerController>();
            if (remoteControl != null)
            {
                remoteControl.UpdatePosition(new Vector2(_x, _y));
            }
        }
    }
}