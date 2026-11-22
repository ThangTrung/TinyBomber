using System;
using System.Collections.Generic;
using UnityEngine;

// One instance per local round. Unity's network callbacks apply movement on the main thread.
public sealed class MovementOrdering
{
    private readonly Dictionary<int, (long Sequence, Vector2 Target)> peers =
        new Dictionary<int, (long, Vector2)>();
    private long lastSent;
    private long lastCorrection;

    public bool TrySend(Func<long, bool> send)
    {
        // Match ProtocolV4's sequence range while the Unity plugin still exposes only v3.
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

    public bool TryAcceptCorrection(long processed, long accepted)
    {
        if (processed <= lastCorrection || accepted < 0 || accepted >= processed) return false;
        lastCorrection = processed;
        return true;
    }

    public void RemovePeer(int id) => peers.Remove(id);

    public void ResetLocal()
    {
        lastSent = 0;
        lastCorrection = 0;
    }

    public void Reset()
    {
        ResetLocal();
        peers.Clear();
    }
}
