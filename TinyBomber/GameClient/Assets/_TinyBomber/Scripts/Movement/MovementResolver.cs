using System;
using GameShared;
using UnityEngine;

public sealed class MovementResolver
{
    private const double MaxOffset = 0.18;
    private const double AssistStepFraction = 0.75;
    private const double TerrainClearance = 0.001;

    private readonly Func<Vector2, Vector2, bool> bombBlocksStep;
    private Vector2 approachDirection;
    private Vector2 expectedPosition;
    private double distanceSpent;
    private bool interrupted;

    public MovementResolver(Func<Vector2, Vector2, bool> bombBlocksStep)
    {
        this.bombBlocksStep = bombBlocksStep;
    }

    public void Reset()
    {
        approachDirection = Vector2.zero;
        distanceSpent = 0;
        interrupted = false;
    }

    public Vector2 Resolve(int[,] terrain, Vector2 start, Vector2 step)
    {
        if (terrain == null || step == Vector2.zero)
        {
            Reset();
            return start;
        }

        Vector2 target = start + step;
        if (CanStep(terrain, start, target))
        {
            Reset();
            return target;
        }

        if (step.x == 0f || step.y == 0f)
            return Assist(terrain, start, step, target);

        Reset();
        Vector2 alongX = new Vector2(target.x, start.y);
        Vector2 alongY = new Vector2(start.x, target.y);
        bool preferX = Mathf.Abs(step.x) >= Mathf.Abs(step.y);
        Vector2 first = preferX ? alongX : alongY;
        Vector2 second = preferX ? alongY : alongX;
        if (CanStep(terrain, start, first)) return first;
        return CanStep(terrain, start, second) ? second : start;
    }

    private Vector2 Assist(int[,] terrain, Vector2 start, Vector2 step, Vector2 target)
    {
        bool horizontal = step.y == 0f;
        float forwardStep = horizontal ? step.x : step.y;
        float sign = Mathf.Sign(forwardStep);
        Vector2 direction = horizontal ? new Vector2(sign, 0f) : new Vector2(0f, sign);

        if (!direction.Equals(approachDirection))
        {
            Reset();
            approachDirection = direction;
        }
        // A snap during an approach cancels it until input changes or normal movement resumes.
        if (distanceSpent > 0 && !start.Equals(expectedPosition)) interrupted = true;
        if (interrupted || distanceSpent >= MaxOffset || bombBlocksStep(start, target)) return start;

        float lateral = horizontal ? start.y : start.x;
        Vector2 cellCenter = new Vector2(Mathf.Floor(start.x) + 0.5f, Mathf.Floor(start.y) + 0.5f);
        Vector2 doorCenter = cellCenter + direction;
        float laneCenter = horizontal ? cellCenter.y : cellCenter.x;
        float forward = horizontal ? doorCenter.x : doorCenter.y;
        Vector2 originalProbe = horizontal ? new Vector2(forward, lateral) : new Vector2(lateral, forward);

        if (bombBlocksStep(start, originalProbe) || !HasExit(terrain, doorCenter, direction))
            return start;

        double remaining = MaxOffset - distanceSpent;
        float limit = MoveWithin(lateral, laneCenter, remaining);
        float goal = limit;
        if (!CanEnter(goal)) return start;

        // For a terrain-only straight probe, moving toward the lane center only removes side-tile overlap.
        float blocked = lateral;
        for (int i = 0; i < 16; i++)
        {
            float middle = (float)(((double)blocked + goal) * 0.5);
            if (middle == blocked || middle == goal) break;
            if (CanEnter(middle)) goal = middle;
            else blocked = middle;
        }

        // Stay slightly inside the valid lane when the remaining budget allows it.
        goal = MoveWithin(goal, limit, TerrainClearance);
        Vector2 aligned = WithLateral(goal);
        Vector2 next = WithLateral(MoveWithin(lateral, goal, Math.Abs(forwardStep) * AssistStepFraction));
        Vector2 entry = horizontal ? new Vector2(forward, goal) : new Vector2(goal, forward);
        if (next.Equals(start) || !CanStep(terrain, start, aligned) || !CanStep(terrain, aligned, entry) ||
            !CanStep(terrain, start, next))
            return start;

        double traveled = Math.Abs((double)(horizontal ? next.y : next.x) - lateral);
        distanceSpent += traveled;
        expectedPosition = next;
        return next;

        Vector2 WithLateral(float across) => horizontal ? new Vector2(start.x, across) : new Vector2(across, start.y);

        bool CanEnter(float across)
        {
            Vector2 entry = horizontal ? new Vector2(forward, across) : new Vector2(across, forward);
            return CanSweep(terrain, WithLateral(across), entry);
        }
    }

    private bool HasExit(int[,] terrain, Vector2 doorCenter, Vector2 direction)
    {
        Vector2 side = new Vector2(-direction.y, direction.x);
        return CanStep(terrain, doorCenter, doorCenter + direction)
            || CanStep(terrain, doorCenter, doorCenter + side)
            || CanStep(terrain, doorCenter, doorCenter - side);
    }

    private static float MoveWithin(float start, float target, double limit)
    {
        double delta = (double)target - start;
        if (Math.Abs(delta) <= limit) return target;

        float next = (float)(start + Math.Sign(delta) * limit);
        if (Math.Abs((double)next - start) <= limit) return next;

        // Round back toward start if the nearest float exceeds the distance limit.
        int bits = BitConverter.SingleToInt32Bits(next);
        bool towardZero = (next > start) == (next > 0f);
        return BitConverter.Int32BitsToSingle(bits + (towardZero ? -1 : 1));
    }

    private bool CanStep(int[,] terrain, Vector2 start, Vector2 target) =>
        CanSweep(terrain, start, target) && !bombBlocksStep(start, target);

    private static bool CanSweep(int[,] terrain, Vector2 start, Vector2 target) =>
        TerrainCollision.CanSweep(terrain, start.x, start.y, target.x, target.y);
}
