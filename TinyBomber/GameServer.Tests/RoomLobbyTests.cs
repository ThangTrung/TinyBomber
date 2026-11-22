using GameShared;
using Xunit;

namespace GameServer.Tests;

public sealed class RoomLobbyTests
{
    [Fact]
    public void LobbyRequiresHandshakeAndRejectsMissingOrFullRooms()
    {
        var host = new Client(1) { Username = "Host" };
        ServerHandle.CreateRoom(host);
        Assert.Null(host.Room);

        host.UdpConfirmed = true;
        ServerHandle.CreateRoom(host);
        var room = Assert.IsType<GameLogic>(host.Room);
        var guests = Enumerable.Range(2, Constants.MAX_PLAYERS)
            .Select(id => new Client(id) { Username = $"Guest {id}", UdpConfirmed = true })
            .ToArray();

        try
        {
            ServerHandle.JoinRoom(guests[0], 0);
            Assert.Null(guests[0].Room);
            for (int i = 0; i < guests.Length - 1; i++)
                ServerHandle.JoinRoom(guests[i], room.Id);

            Assert.Equal(Constants.MAX_PLAYERS, room.Members.Count);
            ServerHandle.JoinRoom(guests[^1], room.Id);
            Assert.Null(guests[^1].Room);
            ServerHandle.CreateRoom(host);
            Assert.Same(room, host.Room);

            ServerHandle.LeaveRoom(host, 0);
            Assert.Same(room, host.Room);
        }
        finally
        {
            foreach (var guest in guests) Server.RemoveFromRoom(guest);
            Server.RemoveFromRoom(host);
        }

        Assert.Null(Server.FindRoom(room.Id));
        ServerHandle.JoinRoom(host, room.Id);
        Assert.Null(host.Room);
        Assert.True(host.IsReady);
    }

    [Fact]
    public void PlayerCanChangeRoomsAndOldPacketsCannotAffectRoundWithSameNumber()
    {
        var mover = new Client(1) { Username = "Mover", UdpConfirmed = true };
        var leftBehind = new Client(2) { Username = "Left", UdpConfirmed = true };
        var newHost = new Client(3) { Username = "Host", UdpConfirmed = true };
        var newGuest = new Client(4) { Username = "Guest", UdpConfirmed = true };

        try
        {
            ServerHandle.CreateRoom(mover);
            GameLogic oldRoom = mover.Room!;
            ServerHandle.JoinRoom(leftBehind, oldRoom.Id);
            oldRoom.StartRound(mover, oldRoom.Id, 0);
            int oldRoundId = oldRoom.RoundId;

            ServerHandle.CreateRoom(newHost);
            GameLogic newRoom = newHost.Room!;
            ServerHandle.LeaveRoom(mover, oldRoom.Id);
            ServerHandle.JoinRoom(newGuest, newRoom.Id);
            ServerHandle.JoinRoom(mover, newRoom.Id);
            newRoom.StartRound(newHost, newRoom.Id, 0, now: 0);

            Assert.Equal(MatchPhase.Playing, oldRoom.Phase);
            oldRoom.UpdateRound(long.MaxValue);
            Assert.Equal(leftBehind.id, oldRoom.WinnerId);
            Assert.Equal(oldRoundId, newRoom.RoundId);
            Player player = mover.player!;
            var initial = (player.x, player.y);
            var (x, y) = (player.x + 0.0625f, player.y);
            ServerHandle.PlayerMovement(mover, oldRoom.Id, oldRoundId, x, y, now: 0);
            newRoom.PlaceBomb(mover, oldRoom.Id, oldRoundId);
            Assert.Equal(initial, (player.x, player.y));
            newRoom.UpdateRound(long.MaxValue);
            Assert.True(player.isAlive);

            ServerHandle.PlayerMovement(mover, newRoom.Id, newRoom.RoundId, x, y, now: 0);
            Assert.Equal((x, y), (player.x, player.y));
        }
        finally
        {
            foreach (var slot in new[] { mover, leftBehind, newHost, newGuest })
                Server.RemoveFromRoom(slot);
        }
    }
}
