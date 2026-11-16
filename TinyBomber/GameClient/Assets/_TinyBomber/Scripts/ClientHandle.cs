using System.Net;
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

        int _localPort = ((IPEndPoint)Client.instance.tcp.socket.Client.LocalEndPoint).Port;
        Client.instance.udp.Connect(_localPort);
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
            if (_id == Client.instance.myId)
            {
                GameManager.players[_id].transform.position = new Vector2(_x, _y);
            }
            else
            {
                RemotePlayerController remoteControl = GameManager.players[_id].GetComponent<RemotePlayerController>();
                if (remoteControl != null)
                {
                    remoteControl.UpdatePosition(new Vector2(_x, _y));
                }
            }
        }
    }

    public static void PlayerDisconnected(Packet packet)
    {
        GameManager.RemovePlayer(packet.ReadInt());
    }

    public static void BombPlaced(Packet packet)
    {
        GameManager.instance.ShowBomb(ReadCell(packet));
    }

    public static void BombExploded(Packet packet)
    {
        var origin = ReadCell(packet);
        var affected = ReadCells(packet);
        var destroyed = ReadCells(packet);
        GameManager.instance.ShowExplosion(origin, affected, destroyed);
    }

    private static Vector2Int ReadCell(Packet packet)
    {
        int col = packet.ReadInt(), row = packet.ReadInt();
        if (col < 0 || col >= Constants.MAP_WIDTH || row < 0 || row >= Constants.MAP_HEIGHT)
            throw new System.IO.InvalidDataException("Invalid bomb cell");
        return new Vector2Int(col, row);
    }

    private static Vector2Int[] ReadCells(Packet packet)
    {
        int count = packet.ReadInt();
        if (count < 0 || count > Constants.MAP_WIDTH * Constants.MAP_HEIGHT || count > packet.UnreadLength() / 8)
            throw new System.IO.InvalidDataException("Invalid explosion cell count");
        var cells = new Vector2Int[count];
        for (int i = 0; i < count; i++) cells[i] = ReadCell(packet);
        return cells;
    }

    public static void MapData(Packet _packet)
    {
        int[,] _map = new int[Constants.MAP_WIDTH, Constants.MAP_HEIGHT];
        for (int col = 0; col < Constants.MAP_WIDTH; col++)
        {
            for (int row = 0; row < Constants.MAP_HEIGHT; row++)
            {
                _map[col, row] = _packet.ReadInt();
            }
        }
        GameManager.instance.BuildMap(_map);
    }
}
