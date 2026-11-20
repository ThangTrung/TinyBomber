using UnityEngine;
using GameShared;

public class ClientHandle : MonoBehaviour
{
    public static void Welcome(Packet _packet)
    {
        string _msg = _packet.ReadString();
        int _myId = _packet.ReadInt();

        Debug.Log($"[SERVER NÓI]: {_msg}");
        Client.instance.BeginUdpHandshake(_myId);
    }

    public static void RoundBegin(Packet packet)
    {
        if (!IsCurrentRoom(packet)) return;
        int roundId = packet.ReadInt();
        int count = packet.ReadInt();
        if (count < 2 || count > Constants.MAX_PLAYERS || count > packet.UnreadLength() / sizeof(int))
            throw new System.IO.InvalidDataException("Invalid round roster");

        var roster = new int[count];
        for (int i = 0; i < count; i++) roster[i] = packet.ReadInt();
        GameManager.instance.BeginRound(roundId, roster);
    }

    public static void MatchState(Packet packet)
    {
        if (!IsCurrentRoom(packet)) return;
        int roundId = packet.ReadInt();
        var phase = (MatchPhase)packet.ReadInt();
        int readyCount = packet.ReadInt();
        int winner = packet.ReadInt();
        GameManager.instance.ApplyMatchState(roundId, phase, readyCount, winner);
    }

    public static void PlayerEliminated(Packet packet)
    {
        if (!IsCurrentRound(packet)) return;
        GameManager.RemovePlayer(packet.ReadInt());
    }

    private static bool IsCurrentRoom(Packet packet)
    {
        int roomId = packet.ReadInt();
        return roomId > 0 && roomId == GameManager.instance.RoomId;
    }

    private static bool IsCurrentRound(Packet packet)
    {
        if (!IsCurrentRoom(packet)) return false;
        int roundId = packet.ReadInt();
        return roundId > 0 && roundId == GameManager.instance.ActiveRoundId;
    }

    public static void SpawnPlayer(Packet _packet)
    {
        if (!IsCurrentRound(_packet)) return;
        int _id = _packet.ReadInt();
        float _x = _packet.ReadFloat();
        float _y = _packet.ReadFloat();

        Debug.Log($"Sinh nhân vật ID: {_id} tại ({_x}, {_y})");
        GameManager.instance.SpawnPlayer(_id, new Vector2(_x, _y));
    }

    public static void PlayerPosition(Packet _packet)
    {
        if (!IsCurrentRound(_packet) || !GameManager.instance.IsPlaying) return;
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
        if (IsCurrentRound(packet)) GameManager.RemovePlayer(packet.ReadInt());
    }

    public static void BombPlaced(Packet packet)
    {
        if (IsCurrentRound(packet)) GameManager.instance.ShowBomb(ReadCell(packet));
    }

    public static void BombExploded(Packet packet)
    {
        if (!IsCurrentRound(packet)) return;
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
        if (!IsCurrentRound(_packet)) return;
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
