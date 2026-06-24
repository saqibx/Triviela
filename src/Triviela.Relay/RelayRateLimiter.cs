using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace Triviela.Relay;

/// <summary>
/// Per-connection sliding-window limiter for lookups and ASK, plus a global daily ASK cap.
/// In-memory and single-instance — fine because the relay runs as a single machine (the poller
/// can't be horizontally scaled without leader election).
/// </summary>
public sealed class RelayRateLimiter(IOptions<RelayLimitsOptions> options)
{
    private readonly RelayLimitsOptions _opts = options.Value;
    private readonly ConcurrentDictionary<string, Window> _lookups = new();
    private readonly ConcurrentDictionary<string, Window> _asks = new();

    private readonly object _gate = new();
    private int _asksToday;
    private DateTime _asksDayUtc = DateTime.MinValue;

    public int MaxQuestionLength => _opts.MaxQuestionLength;

    public bool TryLookup(string connectionId) =>
        _lookups.GetOrAdd(connectionId, _ => new Window()).TryTake(_opts.LookupsPerConnectionPerMinute);

    public bool TryAsk(string connectionId)
    {
        if (!_asks.GetOrAdd(connectionId, _ => new Window()).TryTake(_opts.AsksPerConnectionPerMinute))
            return false;

        lock (_gate)
        {
            var today = DateTime.UtcNow.Date;
            if (today != _asksDayUtc) { _asksDayUtc = today; _asksToday = 0; }
            if (_asksToday >= _opts.GlobalAsksPerDay) return false;
            _asksToday++;
        }
        return true;
    }

    public void Drop(string connectionId)
    {
        _lookups.TryRemove(connectionId, out _);
        _asks.TryRemove(connectionId, out _);
    }

    private sealed class Window
    {
        private readonly object _g = new();
        private readonly Queue<DateTime> _hits = new();

        public bool TryTake(int perMinute)
        {
            lock (_g)
            {
                var now = DateTime.UtcNow;
                while (_hits.Count > 0 && now - _hits.Peek() > TimeSpan.FromMinutes(1)) _hits.Dequeue();
                if (_hits.Count >= perMinute) return false;
                _hits.Enqueue(now);
                return true;
            }
        }
    }
}
