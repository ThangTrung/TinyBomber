using GameShared;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class GameManager : MonoBehaviour
{
    public static GameManager instance;
    public static Dictionary<int, GameObject> players = new Dictionary<int, GameObject>();
    public static Dictionary<Vector2Int, GameObject> softBlocks = new Dictionary<Vector2Int, GameObject>();

    public GameObject localPlayerPrefab; 
    public GameObject playerPrefab;
    public GameObject softBlockPrefab;
    public GameObject bombUpPrefab;
    private readonly BombUpRoundState bombUps = new BombUpRoundState();
    private readonly Dictionary<Vector2Int, GameObject> itemViews = new Dictionary<Vector2Int, GameObject>();

    internal void ApplyBombUp(int room, int round, int col, int row, int actor, int capacity)
    {
        if (!IsPlaying || room != RoomId || round != ActiveRoundId) return;
        var cell = new Vector2Int(col, row);
        if (actor == 0)
        {
            if (bombUps.Spawn(col, row) && bombUpPrefab)
                itemViews.Add(cell, Instantiate(bombUpPrefab, CellCenter(cell), Quaternion.identity));
        }
        else if (bombUps.Collect(col, row, actor, capacity) && itemViews.TryGetValue(cell, out var view))
        {
            itemViews.Remove(cell);
            Destroy(view);
        }
    }

    private void ClearBombUps()
    {
        bombUps.Clear();
        foreach (var view in itemViews.Values) if (view) Destroy(view);
        itemViews.Clear();
    }

    public GameObject bombPrefab;
    private readonly Dictionary<Vector2Int, GameObject> bombs = new Dictionary<Vector2Int, GameObject>();
    [SerializeField] private Text matchStatus;
    [SerializeField] private Button matchButton;
    [SerializeField] private Text matchButtonLabel;

    private readonly List<GameObject> blasts = new List<GameObject>();
    private readonly HashSet<int> pendingSpawns = new HashSet<int>();
    private readonly MovementOrdering movementOrdering = new MovementOrdering();
    [SerializeField, Min(0.05f)] private float movementRecoverySeconds = 0.25f;
    private double MovementRecoveryDelay => Mathf.Max(0.05f, movementRecoverySeconds);
    private Sprite blastSprite;
    private int[,] terrain;
    private MovementResolver movementResolver;
    private MatchPhase phase;
    private int displayedRoundId;
    private int readyCount;
    private int winnerId;
    private RoomLobbyUi roomUi;
    private bool roomRequestPending;
    private string roomError;
    private string connectionStatus = "Nhập địa chỉ server.";
    private string udpHealthWarning;

    public int RoomId { get; private set; }
    public int ActiveRoundId { get; private set; }
    public bool IsReady { get; private set; }
    public bool IsPlaying => phase == MatchPhase.Playing &&
        ActiveRoundId > 0 && ActiveRoundId == displayedRoundId;
    public bool CanControl => IsPlaying && !roomRequestPending && terrain != null && pendingSpawns.Count == 0 &&
        players.ContainsKey(Client.instance.myId);
    private bool CanRequestStart => RoomId > 0 && !roomRequestPending &&
        readyCount >= 2 && phase != MatchPhase.Playing;

    public void SetConnectionStatus(string status)
    {
        connectionStatus = status;
        RefreshMatchUi();
    }

    internal void SetUdpHealth(bool degraded, bool unavailable)
    {
        string warning = unavailable ? "UDP không khả dụng" : degraded ? "Kết nối UDP gián đoạn" : null;
        if (udpHealthWarning == warning) return;
        udpHealthWarning = warning;
        RefreshMatchUi();
    }

    public void ConfirmLobbyReady()
    {
        IsReady = true;
        RefreshMatchUi();
    }

    public void ChangeRoom(int roomId)
    {
        ClearRound();
        RoomId = roomId;
        displayedRoundId = 0;
        phase = MatchPhase.Waiting;
        readyCount = 0;
        winnerId = 0;
        roomRequestPending = false;
        roomError = null;
        RefreshMatchUi();
    }

    public void ApplyRoomError(RoomError error)
    {
        roomRequestPending = false;
        roomError = error switch
        {
            RoomError.NotReady => "Kết nối chưa sẵn sàng.",
            RoomError.AlreadyInRoom => "Bạn đang ở trong một phòng.",
            RoomError.RoomNotFound => "Không tìm thấy phòng.",
            RoomError.RoomFull => "Phòng đã đủ 4 người.",
            _ => "Bạn không ở trong phòng này."
        };
        RefreshMatchUi();
    }

    private void CreateRoom()
    {
        if (!IsReady || RoomId != 0 || roomRequestPending) return;

        BeginRoomRequest();
        ClientSend.CreateRoom();
    }

    private void JoinRoom(string code)
    {
        if (!IsReady || RoomId != 0 || roomRequestPending) return;
        if (!int.TryParse(code, out int roomId) || roomId <= 0)
        {
            roomError = "Nhập mã phòng hợp lệ.";
            RefreshMatchUi();
            return;
        }

        BeginRoomRequest();
        ClientSend.JoinRoom(roomId);
    }

    private void LeaveRoom()
    {
        if (RoomId == 0 || roomRequestPending) return;

        BeginRoomRequest();
        ClientSend.LeaveRoom(RoomId);
    }

    private void BeginRoomRequest()
    {
        roomRequestPending = true;
        roomError = null;
        RefreshMatchUi();
    }

    public void RequestStartOrReplay()
    {
        if (CanRequestStart) ClientSend.RequestStart(RoomId, displayedRoundId);
    }

    public void BeginRound(int roundId, int[] roster)
    {
        if (roundId <= ActiveRoundId || roundId < displayedRoundId) return;

        ClearRound();
        ActiveRoundId = roundId;
        pendingSpawns.UnionWith(roster);
        RefreshMatchUi();
    }

    public void ApplyMatchState(int roundId, MatchPhase matchPhase, int count, int winner)
    {
        if (roundId < displayedRoundId || roundId < ActiveRoundId) return;

        bool wasPlaying = IsPlaying;
        displayedRoundId = roundId;
        phase = matchPhase;
        readyCount = count;
        winnerId = winner;
        if (phase == MatchPhase.Finished)
        {
            ClearBombUps();
            movementOrdering.Reset();
            ClearBombs();
        }
        else if (!wasPlaying && IsPlaying)
        {
            foreach (var player in players)
                ApplyStoredPeerPosition(player.Key, player.Value);
        }
        RefreshMatchUi();
    }

    private void RefreshMatchUi()
    {
        roomUi?.Refresh(IsReady, RoomId, roomRequestPending,
            Client.instance && Client.instance.IsConnecting);
        if (matchButton)
        {
            matchButton.gameObject.SetActive(RoomId > 0 && phase != MatchPhase.Playing);
            matchButton.interactable = CanRequestStart;
        }
        if (matchButtonLabel) matchButtonLabel.text = phase == MatchPhase.Finished ? "Chơi lại" : "Bắt đầu";
        if (!matchStatus) return;

        if (!IsReady) matchStatus.text = connectionStatus;
        else if (roomRequestPending) matchStatus.text = RoomId == 0 ? "Đang vào phòng…" : "Đang rời phòng…";
        else if (roomError != null) matchStatus.text = roomError;
        else if (RoomId == 0) matchStatus.text = "Tạo phòng hoặc nhập mã";
        else if (phase == MatchPhase.Finished)
        {
            if (winnerId == 0) matchStatus.text = "Hòa!";
            else if (ActiveRoundId != displayedRoundId) matchStatus.text = $"Người chơi {winnerId} thắng";
            else if (winnerId == Client.instance.myId) matchStatus.text = "Bạn thắng!";
            else matchStatus.text = $"Bạn thua! Người chơi {winnerId} thắng";
        }
        else if (!IsPlaying && phase == MatchPhase.Playing) matchStatus.text = "Chờ ván sau";
        else if (!IsPlaying) matchStatus.text = $"Sẵn sàng: {readyCount}/{Constants.MAX_PLAYERS}";
        else matchStatus.text = CanControl ? "Đang chơi" : "Bạn đã bị loại";
        if (IsReady && udpHealthWarning != null) matchStatus.text += $"\n{udpHealthWarning}";
    }

    private static Vector2 CellCenter(Vector2Int cell) => new Vector2(cell.x - 8.5f, cell.y - 7.5f);

    public Vector2 ResolveMovement(Vector2 start, Vector2 step)
    {
        return movementResolver.Resolve(terrain, start, step);
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
        blasts.RemoveAll(flash => !flash);
        foreach (var cell in affected)
        {
            var flash = new GameObject("Blast");
            flash.transform.SetParent(transform, true);
            flash.transform.position = CellCenter(cell);
            var sprite = flash.AddComponent<SpriteRenderer>();
            sprite.sprite = blastSprite;
            sprite.color = new Color(1f, 0.55f, 0.12f, 0.75f);
            sprite.sortingOrder = 100;
            blasts.Add(flash);
            Destroy(flash, 0.2f);
        }
    }

    private void OnDestroy()
    {
        if (instance != this) return;

        ClearRound();
        roomUi?.Destroy();
        instance = null;
        if (matchButton) matchButton.onClick.RemoveListener(RequestStartOrReplay);
        if (blastSprite) Destroy(blastSprite);
    }

    private void Awake()
    {
        if (instance != null && instance != this)
        {
            Destroy(this);
            return;
        }

        instance = this;
        movementResolver = new MovementResolver(BombBlocksStep);
        if (matchButton) matchButton.onClick.AddListener(RequestStartOrReplay);
        RefreshMatchUi();
    }

    private void Start()
    {
        if (instance != this || !matchButton || !Client.instance) return;

        var canvas = matchButton.GetComponentInParent<Canvas>();
        if (canvas)
            roomUi = new RoomLobbyUi(canvas.transform, Client.instance, CreateRoom, JoinRoom, LeaveRoom);
        RefreshMatchUi();
    }

    public void SpawnPlayer(int _id, Vector2 _position)
    {
        if (!pendingSpawns.Remove(_id)) return;

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
        bombUps.SpawnPlayer(_id);
        if (IsPlaying && _id != Client.instance.myId) ApplyStoredPeerPosition(_id, _player);
    }

    public static void RemovePlayer(int id)
    {
        if (instance)
        {
            instance.bombUps.RemovePlayer(id);
            instance.pendingSpawns.Remove(id);
            instance.movementOrdering.RemovePeer(id);
            if (Client.instance && id == Client.instance.myId) instance.movementOrdering.ResetLocal();
        }
        if (!players.TryGetValue(id, out var player)) return;
        players.Remove(id);
        if (player != null)
        {
            player.SetActive(false);
            Destroy(player);
        }
        if (instance) instance.RefreshMatchUi();
    }

    public static void ClearSession(string status)
    {
        if (!instance) return;

        instance.IsReady = false;
        instance.udpHealthWarning = null;
        instance.connectionStatus = status;
        instance.ChangeRoom(0);
    }

    private void ClearRound()
    {
        ClearBombUps();
        movementOrdering.Reset();
        movementResolver?.Reset();
        ActiveRoundId = 0;
        pendingSpawns.Clear();
        terrain = null;
        ClearBombs();
        foreach (var flash in blasts)
            if (flash) Destroy(flash);
        blasts.Clear();
        foreach (var player in players.Values)
        {
            if (!player) continue;
            player.SetActive(false);
            Destroy(player);
        }
        players.Clear();
        foreach (var block in softBlocks.Values)
            if (block) Destroy(block);
        softBlocks.Clear();
    }

    internal bool TrySendOrderedMovement(Vector2 position) =>
        Client.instance && Client.instance.tcp != null && CanControl &&
        movementOrdering.TrySend((Client.instance.tcp, RoomId, ActiveRoundId), position,
            Time.realtimeSinceStartupAsDouble, MovementRecoveryDelay,
            ClientSend.TrySendUdpMovement, ClientSend.TrySendTcpMovement);

    internal void ApplyOrderedPeerPosition(int roomId, int roundId, int id, long sequence, Vector2 target, bool reliable)
    {
        if (roomId <= 0 || roomId != RoomId || roundId <= 0 || roundId != ActiveRoundId ||
            displayedRoundId > roundId || (displayedRoundId == roundId && phase == MatchPhase.Finished) ||
            id == Client.instance.myId || !IsFinite(target)) return;

        // Keep the newest snapshot only for a known member of this round. UDP can overtake spawn.
        if (!players.TryGetValue(id, out var player) && !pendingSpawns.Contains(id)) return;
        if (!movementOrdering.TryRecordPeer(id, sequence, target, reliable)) return;

        if (IsPlaying && player) ApplyStoredPeerPosition(id, player);
    }

    private void ApplyStoredPeerPosition(int id, GameObject player)
    {
        if (movementOrdering.TryGetPeerTarget(id, out var target, out var reliable) &&
            player.TryGetComponent(out RemotePlayerController remote))
            remote.UpdatePosition(target, reliable);
    }

    internal void QueueOwnerMovementState(int roomId, int roundId, int id, long processed, long accepted,
        Vector2 position)
    {
        if (!IsPlaying || roomId <= 0 || roomId != RoomId || roundId != ActiveRoundId ||
            id != Client.instance.myId || !IsFinite(position) ||
            !players.TryGetValue(id, out var player) || !player)
            return;

        movementOrdering.QueueOwnerState(processed, accepted, position);
    }

    // ThreadManager drains TCP callbacks in Update. Apply only the latest owner result at frame end.
    private void LateUpdate()
    {
        if (!IsPlaying || !Client.instance || !players.TryGetValue(Client.instance.myId, out var player) || !player) return;

        if (movementOrdering.TryTakeOwnerPosition(out var position) &&
            !((Vector2)player.transform.position).Equals(position)) player.transform.position = position;
        if (Client.instance.tcp != null && CanControl)
            movementOrdering.TryRetry((Client.instance.tcp, RoomId, ActiveRoundId), Time.realtimeSinceStartupAsDouble,
                MovementRecoveryDelay, ClientSend.TrySendTcpMovement);
    }

    private static bool IsFinite(Vector2 position) => !float.IsNaN(position.x) && !float.IsInfinity(position.x) &&
        !float.IsNaN(position.y) && !float.IsInfinity(position.y);

    private void ClearBombs()
    {
        foreach (var bomb in bombs.Values)
            if (bomb) Destroy(bomb);
        bombs.Clear();
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
