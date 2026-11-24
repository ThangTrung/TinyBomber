using GameShared;
using NUnit.Framework;

public class BombUpRoundTests
{
    [Test]
    public void SameCapacityCollectConsumesOnlyAVisibleItemAndBlocksStaleSpawn()
    {
        var state = new BombUpRoundState();
        state.SpawnPlayer(7);
        Assert.IsTrue(state.Spawn(2, 1));
        Assert.IsFalse(state.Collect(2, 1, 7, 1));
        Assert.IsTrue(state.Collect(2, 1, 7, 2));
        Assert.IsTrue(state.Spawn(3, 1));
        Assert.IsFalse(state.Collect(3, 1, 7, 2));
        Assert.IsTrue(state.Collect(3, 1, 7, 3));

        for (int col = 4; col <= 5; col++)
        {
            Assert.IsTrue(state.Spawn(col, 1));
            Assert.IsFalse(state.Collect(col, 1, 7, 2));
            Assert.IsTrue(state.HasItem(col, 1));
            Assert.IsTrue(state.Collect(col, 1, 7, 3));
            Assert.IsFalse(state.HasItem(col, 1));
            Assert.IsFalse(state.Collect(col, 1, 7, 3));
            Assert.IsFalse(state.Spawn(col, 1));
        }

        Assert.IsTrue(state.TryGetCapacity(7, out int capacity));
        Assert.AreEqual(3, capacity);
    }

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
