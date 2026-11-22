using System;
using System.Collections.Generic;
using UnityEngine;

// One instance per local round. Unity's network callbacks apply movement on the main thread.
public sealed class MovementOrdering
{
    private readonly Dictionary<int, (long Sequence, Vector2 Target)> peers =
        new Dictionary<int, (long, Vector2)>();
    private long lastSent;
    private long lastOwnerProcessed;
    private (long Processed, bool Correction, Vector2 Position)? pendingOwnerState;
    private bool recoveringFromCorrection;

    public bool TrySend(Func<long, bool> send)
    {
        if (lastSent == long.MaxValue) return false;
        long sequence = lastSent + 1;
        if (!send(sequence)) return false;
        lastSent = sequence;
        return true;
    }

    public bool TryRecordPeer(int id, long sequence, Vector2 target)
    {
        if (sequence <= 0 || (peers.TryGetValue(id, out var previous) && sequence <= previous.Sequence))
            return false;

        peers[id] = (sequence, target);
        return true;
    }

    public bool TryGetPeerTarget(int id, out Vector2 target)
    {
        bool found = peers.TryGetValue(id, out var snapshot);
        target = snapshot.Target;
        return found;
    }

    public bool QueueOwnerState(long processed, long accepted, Vector2 position)
    {
        if (processed <= lastOwnerProcessed || processed > lastSent || accepted < 0 || accepted > processed)
            return false;

        lastOwnerProcessed = processed;
        pendingOwnerState = (processed, processed != accepted, position);
        return true;
    }

    public bool TryTakeOwnerPosition(out Vector2 position)
    {
        position = default;
        if (!pendingOwnerState.HasValue) return false;
        var state = pendingOwnerState.Value;
        pendingOwnerState = null;

        if (state.Correction)
            recoveringFromCorrection = true;
        else
        {
            // Keep newer local prediction. Once caught up, repair a snap with the final accept.
            if (!recoveringFromCorrection || state.Processed != lastSent) return false;
            recoveringFromCorrection = false;
        }
        position = state.Position;
        return true;
    }

    public void RemovePeer(int id) => peers.Remove(id);

    public void ResetLocal()
    {
        lastSent = 0;
        lastOwnerProcessed = 0;
        pendingOwnerState = null;
        recoveringFromCorrection = false;
    }

    public void Reset()
    {
        ResetLocal();
        peers.Clear();
    }
}
