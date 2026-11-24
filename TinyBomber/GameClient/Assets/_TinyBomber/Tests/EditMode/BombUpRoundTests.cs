using GameShared;
using NUnit.Framework;

public class BombUpRoundTests
{
    [Test]
    public void DuplicateEventsAndDespawnCannotReviveItemOrRollBackCapacity()
    {
        var state = new BombUpRoundState();
        state.SpawnPlayer(7);
        Assert.IsTrue(state.Spawn(2, 1));
        Assert.IsFalse(state.Spawn(2, 1));
        Assert.IsTrue(state.Collect(2, 1, 7, 2));
        Assert.IsTrue(state.Spawn(3, 1));
        Assert.IsTrue(state.Collect(3, 1, 7, 3));
        Assert.IsFalse(state.Collect(2, 1, 7, 2));
        Assert.IsFalse(state.Spawn(2, 1));
        state.RemovePlayer(7);
        Assert.IsTrue(state.Spawn(4, 1));
        Assert.IsFalse(state.Collect(4, 1, 7, 2));
        state.Clear();
        state.SpawnPlayer(7);
        Assert.IsTrue(state.Spawn(2, 1));
        Assert.IsTrue(state.Collect(2, 1, 7, 2));
    }
}
