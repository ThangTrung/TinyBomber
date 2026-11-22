using System;
using System.Diagnostics;

namespace GameShared
{
    /// <summary>UDP health state for one session. The owner serializes calls and supplies monotonic time.</summary>
    public sealed class UdpHealthMonitor
    {
        private static readonly long ProbeInterval = 5 * Stopwatch.Frequency;
        private static readonly long ProbeTimeout = 5 * Stopwatch.Frequency;
        private static readonly long HealthTimeout = 15 * Stopwatch.Frequency;
        private long? lastProof;
        private long nextProbeAt;
        private long sequence;

        public long? PendingSequence { get; private set; }
        public long? ProbeDeadline { get; private set; }
        public bool IsDegraded { get; private set; }
        public bool SocketUnavailable { get; private set; }
        public long ProbesSent { get; private set; }
        public long EchoesAccepted { get; private set; }
        public long ProbesExpired { get; private set; }
        public long Degradations { get; private set; }
        public long Recoveries { get; private set; }

        public long? NextCheckAt
        {
            get
            {
                if (!lastProof.HasValue || SocketUnavailable) return null;
                long next = ProbeDeadline ?? nextProbeAt;
                return IsDegraded ? next : Math.Min(next, lastProof.Value + HealthTimeout);
            }
        }

        public void Start(long now)
        {
            if (lastProof.HasValue) return;
            lastProof = now;
            nextProbeAt = now + ProbeInterval;
        }

        public void Advance(long now)
        {
            if (ProbeDeadline is long deadline && now >= deadline)
            {
                ClearProbe();
                ProbesExpired++;
            }
            if (lastProof is long proof && now >= proof + HealthTimeout) SetDegraded();
        }

        public long? BeginProbe(long now, bool scheduled)
        {
            Advance(now);
            if (!lastProof.HasValue || SocketUnavailable || PendingSequence.HasValue || (scheduled && now < nextProbeAt))
                return null;

            nextProbeAt = now + ProbeInterval;
            ProbeDeadline = now + ProbeTimeout;
            PendingSequence = checked(++sequence);
            return PendingSequence;
        }

        public void RecordProbeSent() => ProbesSent++;

        public bool AcceptEcho(long receivedSequence, long now)
        {
            if (SocketUnavailable || PendingSequence != receivedSequence ||
                !ProbeDeadline.HasValue || now >= ProbeDeadline.Value) return false;

            ClearProbe();
            EchoesAccepted++;
            lastProof = now;
            if (IsDegraded)
            {
                IsDegraded = false;
                Recoveries++;
            }
            return true;
        }

        public bool AbandonProbe(long abandonedSequence)
        {
            if (PendingSequence != abandonedSequence) return false;
            ClearProbe();
            return true;
        }

        public void MarkSocketUnavailable()
        {
            SocketUnavailable = true;
            ClearProbe();
            SetDegraded();
        }

        private void ClearProbe()
        {
            PendingSequence = null;
            ProbeDeadline = null;
        }

        private void SetDegraded()
        {
            if (IsDegraded) return;
            IsDegraded = true;
            Degradations++;
        }
    }
}
