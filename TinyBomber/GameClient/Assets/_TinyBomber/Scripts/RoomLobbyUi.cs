using System;
using UnityEngine;
using UnityEngine.UI;

internal sealed class RoomLobbyUi
{
    private readonly GameObject connectionControls;
    private readonly InputField addressInput;
    private readonly Button connectButton;
    private readonly GameObject lobbyControls;
    private readonly GameObject roomControls;
    private readonly Button createButton;
    private readonly Button changeServer;
    private readonly Button joinButton;
    private readonly Button leaveButton;
    private readonly InputField codeInput;
    private readonly Text roomCode;

    public RoomLobbyUi(Transform canvas, Client client, Action create, Action<string> join, Action leave)
    {
        connectionControls = new GameObject("Server Selection", typeof(RectTransform));
        Place(connectionControls, canvas, 16, -68, 320, 90);
        addressInput = AddInput("Địa chỉ server", connectionControls.transform, 0, 0, 320);
        addressInput.characterLimit = 253;
        addressInput.text = client.ip;
        connectButton = AddButton("Kết nối", connectionControls.transform, 0, -48, 160);
        connectButton.onClick.AddListener(() => client.ConnectToServer(addressInput.text));

        lobbyControls = new GameObject("Lobby Controls", typeof(RectTransform));
        Place(lobbyControls, canvas, 16, -68, 272, 100);
        createButton = AddButton("Tạo phòng", lobbyControls.transform, 0, 0, 160);
        createButton.onClick.AddListener(() => create());

        changeServer = AddButton("Đổi server", lobbyControls.transform, 172, 0, 128);
        changeServer.onClick.AddListener(client.ReturnToServerSelection);
        codeInput = AddInput("Mã phòng", lobbyControls.transform, 0, -48, 136);
        codeInput.contentType = InputField.ContentType.IntegerNumber;
        codeInput.characterLimit = 10;

        joinButton = AddButton("Vào phòng", lobbyControls.transform, 144, -48, 128);
        joinButton.onClick.AddListener(() => join(codeInput.text));

        roomControls = new GameObject("Room Controls", typeof(RectTransform));
        Place(roomControls, canvas, 16, -68, 320, 90);
        leaveButton = AddButton("Rời phòng", roomControls.transform, 172, 0, 128);
        leaveButton.onClick.AddListener(() => leave());

        var label = DefaultControls.CreateText(default);
        label.name = "Room Code";
        Place(label, roomControls.transform, 0, -48, 320, 36);
        roomCode = label.GetComponent<Text>();
        roomCode.color = Color.white;
        roomCode.fontSize = 20;
        roomCode.alignment = TextAnchor.MiddleLeft;
        roomCode.raycastTarget = false;
    }

    public void Refresh(bool connected, int roomId, bool requestPending, bool connecting)
    {
        bool inRoom = roomId > 0;
        connectionControls.SetActive(!connected);
        addressInput.interactable = !connecting;
        connectButton.interactable = !connecting;
        lobbyControls.SetActive(connected && !inRoom);
        roomControls.SetActive(connected && inRoom);
        createButton.interactable = !requestPending;
        changeServer.interactable = !requestPending;
        joinButton.interactable = createButton.interactable;
        codeInput.interactable = createButton.interactable;
        leaveButton.interactable = !requestPending;
        roomCode.text = $"Mã phòng: {roomId}";
    }

    public void Destroy()
    {
        UnityEngine.Object.Destroy(connectionControls);
        UnityEngine.Object.Destroy(lobbyControls);
        UnityEngine.Object.Destroy(roomControls);
    }

    private static InputField AddInput(string hint, Transform parent, float x, float y, float width)
    {
        var control = DefaultControls.CreateInputField(default);
        control.name = hint;
        Place(control, parent, x, y, width, 40);
        var input = control.GetComponent<InputField>();
        input.textComponent.fontSize = 18;
        var placeholder = (Text)input.placeholder;
        placeholder.font = input.textComponent.font;
        placeholder.text = hint;
        placeholder.fontSize = 18;
        return input;
    }

    private static Button AddButton(string caption, Transform parent, float x, float y, float width)
    {
        var control = DefaultControls.CreateButton(default);
        control.name = caption;
        Place(control, parent, x, y, width, 40);
        var label = control.GetComponentInChildren<Text>();
        label.text = caption;
        label.fontSize = 18;
        return control.GetComponent<Button>();
    }

    private static void Place(GameObject control, Transform parent, float x, float y, float width, float height)
    {
        var rect = (RectTransform)control.transform;
        rect.SetParent(parent, false);
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1);
        rect.anchoredPosition = new Vector2(x, y);
        rect.sizeDelta = new Vector2(width, height);
    }
}
