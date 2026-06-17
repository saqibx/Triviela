using System.Collections.Concurrent;

namespace Triviela.Core;

public sealed class RateLimitGovernor
{
    private sealed class Bucket
    {
        public double Tokens;
        public DateTimeOffset LastRefill;
        public required double Capacity;
        public required double RefillPerSecond;
    }

    private readonly ConcurrentDictionary<string, Bucket> _buckets = new();

    public void Configure(string provider, double capacity, double refillPerSecond) =>
    _buckets[provider] = new Bucket
    {
        Tokens = capacity,
        Capacity = capacity,
        RefillPerSecond = refillPerSecond,
        LastRefill = DateTimeOffset.UtcNow
    };

    public bool TryAcquire(string provider)
    {
        if (!_buckets.TryGetValue(provider, out var bucket)) return true;

        lock (bucket)
        {
            var now = DateTimeOffset.UtcNow;
            var elapsed = (now - bucket.LastRefill).TotalSeconds;
            bucket.Tokens = Math.Min(bucket.Capacity, bucket.Tokens + elapsed * bucket.RefillPerSecond);
            bucket.LastRefill = now;

            if (bucket.Tokens < 1) return false;
            bucket.Tokens -= 1;
            return true;
        }
    }

    public int Remaining(string provider) =>
    _buckets.TryGetValue(provider, out var b) ? (int)Math.Floor(b.Tokens) : -1;
}
