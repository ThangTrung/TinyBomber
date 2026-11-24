using System.Diagnostics;

namespace GameServer.LoadClient;

// Each key is an inclusive upper bound in ms; 2001 is the overflow bucket.
internal sealed class LatencyHistogram
{
    private readonly int[] counts = new int[2002];
    private double total, maximum;
    internal void Record(long start, long end)
    {
        double ms = Stopwatch.GetElapsedTime(start, end).TotalMilliseconds;
        int bucket = (int)Math.Min(2001, Math.Ceiling(Math.Max(0, ms)));
        counts[bucket]++;
        total += ms;
        maximum = Math.Max(ms, maximum);
    }

    internal object Report()
    {
        int n = counts.Sum();
        int? Percentile(double p)
        {
            if (n == 0) return null;
            int rank = (int)Math.Ceiling(n * p), seen = 0;
            for (int i = 0; i < counts.Length; i++)
                if ((seen += counts[i]) >= rank) return i;
            return null;
        }
        return new { Samples = n, MeanMs = n == 0 ? (double?)null : total / n, MaxMs = n == 0 ? (double?)null : maximum,
            P50Ms = Percentile(.5), P95Ms = Percentile(.95), P99Ms = Percentile(.99),
            UpperBoundMs = Enumerable.Range(0, counts.Length).Where(i => counts[i] != 0).ToDictionary(i => i, i => counts[i]) };
    }
}

