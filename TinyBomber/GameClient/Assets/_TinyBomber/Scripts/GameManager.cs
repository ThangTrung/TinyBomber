using GameShared;
using System.Collections.Generic;
using UnityEngine;

public class GameManager : MonoBehaviour
{
    public static GameManager instance;
    public static Dictionary<int, GameObject> players = new Dictionary<int, GameObject>();
    public static Dictionary<Vector2Int, GameObject> softBlocks = new Dictionary<Vector2Int, GameObject>();

    public GameObject localPlayerPrefab; 
    public GameObject playerPrefab;
    public GameObject softBlockPrefab;

    private void Awake()
    {
        if (instance == null) instance = this;
        else if (instance != this) Destroy(this);
    }

    public void SpawnPlayer(int _id, Vector2 _position)
    {
        GameObject _player;
        if (_id == Client.instance.myId)
        {
            _player = Instantiate(localPlayerPrefab, _position, Quaternion.identity);
        }
        else
        {
            _player = Instantiate(playerPrefab, _position, Quaternion.identity);
        }

        players.Add(_id, _player);
    }

    public static void RemovePlayer(int id)
    {
        if (!players.TryGetValue(id, out var player)) return;
        players.Remove(id);
        if (player != null) Destroy(player);
    }

    public static void ClearSession()
    {
        foreach (var player in players.Values)
            if (player != null) Destroy(player);
        players.Clear();
        foreach (var block in softBlocks.Values)
            if (block != null) Destroy(block);
        softBlocks.Clear();
    }

    public void BuildMap(int[,] _mapData)
    {
        foreach (var block in softBlocks.Values) Destroy(block);
        softBlocks.Clear();

        for (int col = 0; col < Constants.MAP_WIDTH; col++)
        {
            for (int row = 0; row < Constants.MAP_HEIGHT; row++)
            {
                if (_mapData[col, row] == 2 && softBlockPrefab != null)
                {
                    Vector2 worldPos = new Vector2(col - 8.5f, row - 7.5f);
                    GameObject block = Instantiate(softBlockPrefab, worldPos, Quaternion.identity);
                    softBlocks.Add(new Vector2Int(col, row), block);
                }
            }
        }
        Debug.Log($"[MAP] Đã sinh {softBlocks.Count} khối gạch mềm từ Server!");
    }
}
