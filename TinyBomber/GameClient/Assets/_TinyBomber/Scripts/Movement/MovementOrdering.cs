using System;
using System.Collections.Generic;
using UnityEngine;

// One instance per local round. Unity's network callbacks apply movement on the main thread.
public sealed class MovementOrdering
{
    private readonly Dictionary<int, (long Sequence, Vector2 Target, bool Reliable)> peers =
        new Dictionary<int, (long, Vector2, bool)>();
    private ((object Session, int Room, int Round) Scope, long Sequence, Vector2 Position, double SentAt)? pendingUdp;
    private double nextTcpAttemptAt = double.NegativeInfinity;
    private long lastSent;
    private long lastOwnerProcessed;
    private (long Processed, bool Correction, Vector2 Position)? pendingOwnerState;
    private bool recoveringFromCorrection;

    public bool TrySend((object Session, int Room, int Round) scope, Vector2 position, double now,
        double recoveryDelay, Func<long, Vector2, bool> sendUdp, Func<long, Vector2, bool> sendTcp)
    {
        if (lastSent == long.MaxValue) return false;
        long sequence = lastSent + 1;
        if (sendUdp(sequence, position))
        {
            lastSent = sequence;
            pendingUdp = (scope, sequence, position, now);
            return true;
        }

        if (now < nextTcpAttemptAt) return false;
        nextTcpAttemptAt = now + recoveryDelay;
        if (!sendTcp(sequence, position)) return false;
        lastSent = sequence;
        pendingUdp = null;
        return true;
    }

    public bool TryRetry((object Session, int Room, int Round) scope, double now, double recoveryDelay,
        Func<long, Vector2, bool> sendTcp)
    {
        if (!pendingUdp.HasValue || now < nextTcpAttemptAt) return false;
        var pending = pendingUdp.Value;
        if (!pending.Scope.Equals(scope) || now - pending.SentAt < recoveryDelay) return false;

        // One attempt, including when the TCP queue is already unavailable.
        pendingUdp = null;
        nextTcpAttemptAt = now + recoveryDelay;
        return sendTcp(pending.Sequence, pending.Position);
    }

    public bool TryRecordPeer(int id, long sequence, Vector2 target, bool reliable = false)
    {
        if (sequence <= 0) return false;
        if (peers.TryGetValue(id, out var previous) && (sequence < previous.Sequence ||
            (sequence == previous.Sequence && (!reliable || previous.Reliable || !target.Equals(previous.Target)))))
            return false;

        peers[id] = (sequence, target, reliable);
        return true;
    }

    public bool TryGetPeerTarget(int id, out Vector2 target, out bool reliable)
    {
        bool found = peers.TryGetValue(id, out var snapshot);
        target = snapshot.Target;
        reliable = snapshot.Reliable;
        return found;
    }

    public bool QueueOwnerState(long processed, long accepted, Vector2 position)
    {
        if (processed <= lastOwnerProcessed || processed > lastSent || accepted < 0 || accepted > processed)
            return false;

        lastOwnerProcessed = processed;
        if (pendingUdp.HasValue && processed >= pendingUdp.Value.Sequence) pendingUdp = null;
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
        pendingUdp = null;
        nextTcpAttemptAt = double.NegativeInfinity;
        pendingOwnerState = null;
        recoveringFromCorrection = false;
    }

    public void Reset()
    {
        ResetLocal();
        peers.Clear();
    }
}
