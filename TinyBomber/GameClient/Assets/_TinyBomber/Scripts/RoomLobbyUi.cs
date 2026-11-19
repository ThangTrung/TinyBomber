using System;
using UnityEngine;
using UnityEngine.UI;

internal sealed class RoomLobbyUi
{
    private readonly GameObject lobbyControls;
    private readonly GameObject roomControls;
    private readonly Button createButton;
    private readonly Button joinButton;
    private readonly Button leaveButton;
    private readonly InputField codeInput;
    private readonly Text roomCode;

    public RoomLobbyUi(Transform canvas, Action create, Action<string> join, Action leave)
    {
        lobbyControls = new GameObject("Lobby Controls", typeof(RectTransform));
        Place(lobbyControls, canvas, 16, -68, 272, 100);
        createButton = AddButton("Tạo phòng", lobbyControls.transform, 0, 0, 160);
        createButton.onClick.AddListener(() => create());

        var input = DefaultControls.CreateInputField(default);
        input.name = "Room Code Input";
        Place(input, lobbyControls.transform, 0, -48, 136, 40);
        codeInput = input.GetComponent<InputField>();
        codeInput.contentType = InputField.ContentType.IntegerNumber;
        codeInput.characterLimit = 10;
        codeInput.textComponent.fontSize = 18;
        var placeholder = (Text)codeInput.placeholder;
        placeholder.font = codeInput.textComponent.font;
        placeholder.text = "Mã phòng";
        placeholder.fontSize = 18;

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

    public void Refresh(bool connected, int roomId, bool requestPending)
    {
        bool inRoom = roomId > 0;
        lobbyControls.SetActive(!inRoom);
        roomControls.SetActive(inRoom);
        createButton.interactable = connected && !requestPending;
        joinButton.interactable = createButton.interactable;
        codeInput.interactable = createButton.interactable;
        leaveButton.interactable = !requestPending;
        roomCode.text = $"Mã phòng: {roomId}";
    }

    public void Destroy()
    {
        UnityEngine.Object.Destroy(lobbyControls);
        UnityEngine.Object.Destroy(roomControls);
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
