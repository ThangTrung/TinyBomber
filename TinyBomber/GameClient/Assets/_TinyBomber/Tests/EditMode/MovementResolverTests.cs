using System;
using System.Reflection;
using GameShared;
using NUnit.Framework;
using UnityEngine;

public sealed class MovementResolverTests
{
    private int[,] terrain;
    private Vector2Int? bomb;
    private MovementResolver resolver;

    [SetUp]
    public void SetUp()
    {
        terrain = new int[17, 15];
        bomb = null;
        resolver = new MovementResolver((start, end) => bomb is Vector2Int cell &&
            BombCollision.BlocksStep(cell.x, cell.y, start.x, start.y, end.x, end.y));
    }

    [TestCase(false, 1, 0.14f, 0.1f)]
    [TestCase(false, -1, 0.86f, 0.1f)]
    [TestCase(true, 1, 0.86f, 0.1f)]
    [TestCase(true, -1, 0.14f, 0.1f)]
    [TestCase(false, 1, 0.14f, 0.08f)]
    [TestCase(false, -1, 0.86f, 0.08f)]
    [TestCase(true, 1, 0.86f, 0.08f)]
    [TestCase(true, -1, 0.14f, 0.08f)]
    public void EntersCorridorAfterThreeLateralTicks(bool vertical, int direction, float lateral, float stepLength)
    {
        Corridor(vertical);
        Vector2 start = Oriented(vertical, direction > 0 ? -0.32f : 2.32f, lateral);
        Vector2 step = Oriented(vertical, direction * stepLength, 0f);
        Vector2 position = start;
        for (int i = 0; i < 3; i++)
        {
            Vector2 next = resolver.Resolve(terrain, position, step);
            Assert.That(vertical ? next.y : next.x, Is.EqualTo(vertical ? start.y : start.x));
            double delta = vertical ? (double)next.x - position.x : (double)next.y - position.y;
            Assert.That(Math.Abs(delta), Is.GreaterThan(0).And.LessThanOrEqualTo((double)stepLength * 0.75));
            Assert.That(Math.Sign(delta), Is.EqualTo(lateral < 0.5f ? 1 : -1));
            Assert.That(TerrainCollision.CanSweep(terrain, position.x, position.y, next.x, next.y), Is.True);
            position = next;
        }

        float aligned = vertical ? position.x : position.y;
        Assert.That(Math.Abs((double)aligned - lateral), Is.LessThanOrEqualTo(0.18));
        Assert.That(aligned, Is.InRange(lateral < 0.5f ? 0.30f : 0.698f, lateral < 0.5f ? 0.302f : 0.70f));
        Assert.That(resolver.Resolve(terrain, position, step), Is.EqualTo(position + step));
    }

    [Test]
    public void PlayerControllerAppliesAndSendsTheLastRepresentableStepAtTheLaneEdge()
    {
        Corridor();
        Vector2 position = new Vector2(-0.32f, 0.12000001f);
        Vector2 step = new Vector2(4f * 0.02f, 0f);
        var tryApply = GetPlayerMovementApplication();
        int applied = 0, sent = 0;
        Vector2 sentTarget = default;
        Action<Vector2, Vector2> apply = (start, target) =>
        {
            Assert.That(start, Is.EqualTo(position));
            Assert.That(target, Is.EqualTo(sentTarget));
            position = target;
            applied++;
        };
        Func<Vector2, bool> send = target =>
        {
            sentTarget = target;
            sent++;
            Assert.That(sent, Is.EqualTo(applied + 1));
            return true;
        };

        for (int i = 0; i < 3; i++)
            Assert.That(tryApply(position, resolver.Resolve(terrain, position, step), apply, send), Is.True);

        Assert.That(position.y, Is.EqualTo(0.29999998f));
        Vector2 edge = resolver.Resolve(terrain, position, step);
        Assert.That(edge.y, Is.EqualTo(0.3f));
        Assert.That(edge == position, Is.True);
        Assert.That(edge.Equals(position), Is.False);
        Assert.That(tryApply(position, edge, apply, send), Is.True);
        Assert.That(position.y, Is.EqualTo(0.3f));
        Assert.That(sent, Is.EqualTo(4));

        Vector2 forward = resolver.Resolve(terrain, position, step);
        Assert.That(forward, Is.EqualTo(position + step));
        Assert.That(tryApply(position, forward, apply, send), Is.True);
        Assert.That(position, Is.EqualTo(forward));
        Assert.That(sent, Is.EqualTo(5));
    }

    [Test]
    public void PlayerControllerDoesNotApplyOrSendAnUnchangedPosition()
    {
        Vector2 start = new Vector2(-0.32f, 0.3f);
        var tryApply = GetPlayerMovementApplication();

        Vector2 target = resolver.Resolve(terrain, start, Vector2.zero);
        Assert.That(tryApply(start, target, (_, __) => Assert.Fail("Unexpected position update"),
            _ => throw new AssertionException("Unexpected movement send")), Is.False);
    }

    [Test]
    public void MovementRejectedByBothTransportsDoesNotMoveTheLocalTransform()
    {
        Vector2 start = new Vector2(-0.32f, 0.3f);
        Vector2 position = start;
        int attempts = 0;
        var tryApply = GetPlayerMovementApplication();
        Assert.That(tryApply(start, start + new Vector2(0.08f, 0), (_, target) => position = target,
            _ => { attempts++; return false; }), Is.False);
        Assert.That(position, Is.EqualTo(start));
        Assert.That(attempts, Is.EqualTo(1));
    }

    [Test]
    public void KeepsValidStraightInputAndHelpsSmallOffsetsInOneTick()
    {
        Corridor();
        Vector2 step = new Vector2(0.1f, 0f);
        Vector2 clear = new Vector2(-0.32f, 0.35f);
        Assert.That(resolver.Resolve(terrain, clear, step), Is.EqualTo(clear + step));

        Vector2 start = new Vector2(-0.32f, 0.26f);
        Vector2 aligned = resolver.Resolve(terrain, start, step);
        Assert.That(aligned.x, Is.EqualTo(start.x));
        Assert.That(aligned.y, Is.InRange(0.30f, 0.302f));
        Assert.That(resolver.Resolve(terrain, aligned, step), Is.EqualTo(aligned + step));
    }

    [TestCase(0.1199f, false)]
    [TestCase(0.1201f, true)]
    [TestCase(0.8801f, false)]
    [TestCase(0.8799f, true)]
    public void RespectsTheOverallOffsetAtBothFloatBoundaries(float y, bool helps)
    {
        Corridor();
        Vector2 start = new Vector2(-0.32f, y);
        Vector2 step = new Vector2(0.1f, 0f);
        Vector2 position = start;
        for (int i = 0; i < 3; i++)
            position = resolver.Resolve(terrain, position, step);

        Assert.That(position.Equals(start), Is.EqualTo(!helps));
        Assert.That(Math.Abs((double)position.y - start.y), Is.LessThanOrEqualTo(0.18));
        Assert.That(resolver.Resolve(terrain, position, step), Is.EqualTo(helps ? position + step : start));
    }

    [TestCase(1)]
    [TestCase(2)]
    public void StopsIfTheDoorIsBlocked(int block)
    {
        Corridor();
        terrain[9, 8] = block;
        AssertStays(new Vector2(-0.32f, 0.14f), new Vector2(0.1f, 0f));
    }

    [Test]
    public void StopsAtAOneCellDeadEnd()
    {
        Corridor();
        terrain[10, 8] = 1;
        AssertStays(new Vector2(-0.32f, 0.14f), new Vector2(0.1f, 0f));
    }

    [Test]
    public void SupportsAnEntranceWithAnExitAroundTheNextTurn()
    {
        Corridor();
        terrain[10, 8] = 1;
        terrain[9, 9] = 0;
        Vector2 start = new Vector2(-0.32f, 0.14f);
        Assert.That(resolver.Resolve(terrain, start, new Vector2(0.1f, 0f)).y, Is.GreaterThan(start.y));
    }

    [TestCase(9, 8)]
    [TestCase(9, 7)]
    public void DoesNotAssistAroundABombAhead(int col, int row)
    {
        Corridor();
        AssertStays(new Vector2(-0.32f, 0.14f), new Vector2(0.1f, 0f), new Vector2Int(col, row));
    }

    [TestCase(0.1f)]
    [TestCase(0.08f)]
    public void StopsAligningWhenABombAppearsInTheDoor(float stepLength)
    {
        Corridor();
        Vector2 start = new Vector2(-0.32f, 0.86f);
        Vector2 step = new Vector2(stepLength, 0f);
        Vector2 partiallyAligned = resolver.Resolve(terrain, start, step);
        Assert.That(partiallyAligned.y, Is.LessThan(start.y));
        AssertStays(partiallyAligned, step, new Vector2Int(9, 8));
    }

    [Test]
    public void ChecksBombsAlongTheLateralPath()
    {
        Corridor();
        resolver = new MovementResolver((start, end) => start.x == end.x && start.y != end.y);
        AssertStays(new Vector2(-0.32f, 0.14f), new Vector2(0.1f, 0f));
    }

    [TestCase(0.1f, 7)]
    [TestCase(0.08f, 8)]
    public void BombUnderfootAllowsEscapeAndBlocksReentry(float stepLength, int escapeTicks)
    {
        Corridor();
        bomb = new Vector2Int(8, 8);
        Vector2 position = new Vector2(-0.32f, 0.5f);
        Vector2 step = new Vector2(stepLength, 0f);
        for (int i = 0; i < escapeTicks; i++)
            position = resolver.Resolve(terrain, position, step);

        Assert.That(position.x, Is.GreaterThan(0.30f));
        Assert.That(resolver.Resolve(terrain, position, -step), Is.EqualTo(position));
    }

    [TestCase(0.1f)]
    [TestCase(0.08f)]
    public void RechecksDoorAndInputBetweenTicks(float stepLength)
    {
        Corridor();
        Vector2 start = new Vector2(-0.32f, 0.14f);
        Vector2 step = new Vector2(stepLength, 0f);
        Vector2 partial = resolver.Resolve(terrain, start, step);
        Assert.That(partial.y, Is.GreaterThan(start.y));

        terrain[9, 8] = 2;
        Assert.That(resolver.Resolve(terrain, partial, step), Is.EqualTo(partial));
        Assert.That(resolver.Resolve(terrain, partial, Vector2.zero), Is.EqualTo(partial));
        Assert.That(resolver.Resolve(terrain, partial, -step), Is.EqualTo(partial - step));

        terrain[9, 8] = 0;
        Vector2 resumed = resolver.Resolve(terrain, partial, step);
        Assert.That(resumed.y, Is.GreaterThan(partial.y));
        terrain[9, 7] = 0;
        Assert.That(resolver.Resolve(terrain, resumed, step), Is.EqualTo(resumed + step));
    }

    [Test]
    public void AuthoritativeSnapCannotRenewTheBudgetWhileInputIsHeld()
    {
        Corridor();
        Vector2 start = new Vector2(-0.32f, 0.14f);
        Vector2 step = new Vector2(0.1f, 0f);
        Assert.That(resolver.Resolve(terrain, start, step).y, Is.GreaterThan(start.y));
        AssertStays(start, step);
        Assert.That(resolver.Resolve(terrain, start, Vector2.zero), Is.EqualTo(start));
        Assert.That(resolver.Resolve(terrain, start, step).y, Is.GreaterThan(start.y));
    }

    [TestCase(0.07071068f)]
    [TestCase(0.05656854f)]
    public void DiagonalSlideStillTriesTheOtherAxisAtTheCorner(float component)
    {
        terrain[2, 2] = 1;
        Vector2 start = new Vector2(-7.32f, -6.25f);
        Vector2 step = new Vector2(component, -component);
        Assert.That(resolver.Resolve(terrain, start, step), Is.EqualTo(new Vector2(start.x, start.y + step.y)));
    }

    private void AssertStays(Vector2 start, Vector2 step, Vector2Int? activeBomb = null)
    {
        bomb = activeBomb;
        for (int i = 0; i < 10; i++)
            Assert.That(resolver.Resolve(terrain, start, step), Is.EqualTo(start));
    }

    private void Corridor(bool vertical = false)
    {
        for (int i = 0; i < 2; i++)
        {
            if (vertical)
            {
                terrain[8, i + 8] = 1;
                terrain[10, i + 8] = 1;
            }
            else
            {
                terrain[i + 9, 7] = 1;
                terrain[i + 9, 9] = 1;
            }
        }
    }

    private static Vector2 Oriented(bool vertical, float forward, float lateral) =>
        vertical ? new Vector2(lateral, forward) : new Vector2(forward, lateral);

    private delegate bool TryApplyMovement(Vector2 start, Vector2 target,
        Action<Vector2, Vector2> apply, Func<Vector2, bool> send);

    private static TryApplyMovement GetPlayerMovementApplication()
    {
        // An asmdef cannot reference the predefined Assembly-CSharp assembly directly.
        Type controller = Type.GetType("PlayerController, Assembly-CSharp", throwOnError: true);
        MethodInfo method = controller.GetMethod("TryApplyMovement", BindingFlags.NonPublic | BindingFlags.Static);
        return (TryApplyMovement)method.CreateDelegate(typeof(TryApplyMovement));
    }
}
