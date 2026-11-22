using System.Diagnostics;

namespace GameShared
{
    /// <summary>Client liveness state. The connection owner serializes calls and supplies monotonic timestamps.</summary>
    public sealed class HeartbeatWatchdog
    {
        private static readonly long Timeout = 25 * Stopwatch.Frequency;
        private long lastSequence;

        public long? Deadline { get; private set; }

        public void Start(long now) => Deadline ??= now + Timeout;

        public bool HasExpired(long now) => Deadline is long deadline && now >= deadline;

        public bool ShouldReply(long sequence, long now)
        {
            if (Deadline == null || HasExpired(now) || sequence <= 0 || sequence < lastSequence) return false;
            if (sequence > lastSequence)
            {
                lastSequence = sequence;
                Deadline = now + Timeout;
            }
            return true;
        }
    }
}
