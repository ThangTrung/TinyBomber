using System.Collections.Generic;
using UnityEngine;

public class GameManager : MonoBehaviour
{
    public static GameManager instance;
    public static Dictionary<int, GameObject> players = new Dictionary<int, GameObject>();

    public GameObject localPlayerPrefab; 
    public GameObject remotePlayerPrefab; 

    private void Awake()
    {
        if (instance == null) instance = this;
    }

    public void SpawnPlayer(int _id, Vector2 _position)
    {
        GameObject _player;
        if (_id == Client.instance.myId)
        {
            // Bản thân mình
            _player = Instantiate(localPlayerPrefab, _position, Quaternion.identity);
        }
        else
        {
            // Người chơi khác
            _player = Instantiate(remotePlayerPrefab, _position, Quaternion.identity);
        }

        players.Add(_id, _player);
    }
}