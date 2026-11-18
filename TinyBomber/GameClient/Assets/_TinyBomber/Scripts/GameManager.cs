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
    public GameObject bombPrefab;
    private readonly Dictionary<Vector2Int, GameObject> bombs = new Dictionary<Vector2Int, GameObject>();
    private Sprite blastSprite;
    private int[,] terrain;

    private static Vector2 CellCenter(Vector2Int cell) => new Vector2(cell.x - 8.5f, cell.y - 7.5f);

    public Vector2 ResolveMovement(Vector2 start, Vector2 step)
    {
        if (terrain == null || step == Vector2.zero) return start;
        Vector2 target = start + step;
        if (CanStep(start, target)) return target;
        if (step.x == 0f || step.y == 0f) return start;

        Vector2 alongX = new Vector2(target.x, start.y);
        Vector2 alongY = new Vector2(start.x, target.y);
        bool preferX = Mathf.Abs(step.x) >= Mathf.Abs(step.y);
        Vector2 first = preferX ? alongX : alongY;
        Vector2 second = preferX ? alongY : alongX;
        if (CanStep(start, first)) return first;
        return CanStep(start, second) ? second : start;
    }

    private bool CanStep(Vector2 start, Vector2 target)
    {
        return TerrainCollision.CanStandAt(terrain, target.x, target.y) && !BombBlocksStep(start, target);
    }

    public bool BombBlocksStep(Vector2 start, Vector2 target)
    {
        foreach (var cell in bombs.Keys)
            if (BombCollision.BlocksStep(cell.x, cell.y, start.x, start.y, target.x, target.y)) return true;
        return false;
    }

    public void ShowBomb(Vector2Int cell)
    {
        if (bombs.ContainsKey(cell)) return;
        if (!bombPrefab) Debug.LogWarning("Chưa gán Bomb Prefab trong GameManager.");
        bombs.Add(cell, bombPrefab ? Instantiate(bombPrefab, CellCenter(cell), Quaternion.identity) : null);
    }

    public void ShowExplosion(Vector2Int origin, Vector2Int[] affected, Vector2Int[] destroyed)
    {
        if (bombs.TryGetValue(origin, out var bomb))
        {
            bombs.Remove(origin);
            if (bomb) Destroy(bomb);
        }
        foreach (var cell in destroyed)
        {
            if (terrain != null) terrain[cell.x, cell.y] = 0;
            if (!softBlocks.TryGetValue(cell, out var block)) continue;
            softBlocks.Remove(cell);
            Destroy(block);
        }
        if (!blastSprite)
            blastSprite = Sprite.Create(Texture2D.whiteTexture, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1f);
        foreach (var cell in affected)
        {
            var flash = new GameObject("Blast");
            flash.transform.SetParent(transform, true);
            flash.transform.position = CellCenter(cell);
            var sprite = flash.AddComponent<SpriteRenderer>();
            sprite.sprite = blastSprite;
            sprite.color = new Color(1f, 0.55f, 0.12f, 0.75f);
            sprite.sortingOrder = 100;
            Destroy(flash, 0.2f);
        }
    }

    private void OnDestroy()
    {
        if (blastSprite) Destroy(blastSprite);
    }

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
        if (instance)
        {
            instance.terrain = null;
            foreach (var bomb in instance.bombs.Values)
                if (bomb) Destroy(bomb);
            instance.bombs.Clear();
        }
        foreach (var player in players.Values)
            if (player != null) Destroy(player);
        players.Clear();
        foreach (var block in softBlocks.Values)
            if (block != null) Destroy(block);
        softBlocks.Clear();
    }

    public void BuildMap(int[,] _mapData)
    {
        terrain = _mapData;
        foreach (var block in softBlocks.Values) Destroy(block);
        softBlocks.Clear();

        for (int col = 0; col < Constants.MAP_WIDTH; col++)
        {
            for (int row = 0; row < Constants.MAP_HEIGHT; row++)
            {
                if (_mapData[col, row] == 2 && softBlockPrefab != null)
                {
                    Vector2 worldPos = CellCenter(new Vector2Int(col, row));
                    GameObject block = Instantiate(softBlockPrefab, worldPos, Quaternion.identity);
                    softBlocks.Add(new Vector2Int(col, row), block);
                }
            }
        }
        Debug.Log($"[MAP] Đã sinh {softBlocks.Count} khối gạch mềm từ Server!");
    }
}
