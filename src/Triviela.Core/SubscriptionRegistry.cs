namespace Triviela.Core;

/// <summary>
/// The union of fixtures that at least one connection is currently watching, ref-counted by
/// connection. The poller enriches only what's in here (demand-bounded — the right cost model
/// for a single shared API key). In the relay it's driven by SignalR connections; in local mode
/// a single pseudo-connection tracks the focused fixture (see <see cref="FocusSubscriptionBridge"/>).
/// </summary>
public sealed class SubscriptionRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, HashSet<string>> _byConnection = new();
    private readonly Dictionary<string, int> _refcount = new();

    public void Watch(string connectionId, string fixtureId)
    {
        lock (_gate)
        {
            if (!_byConnection.TryGetValue(connectionId, out var set))
                _byConnection[connectionId] = set = [];
            if (set.Add(fixtureId))
                _refcount[fixtureId] = _refcount.GetValueOrDefault(fixtureId) + 1;
        }
    }

    public void Unwatch(string connectionId, string fixtureId)
    {
        lock (_gate)
        {
            if (_byConnection.TryGetValue(connectionId, out var set) && set.Remove(fixtureId))
                Decrement(fixtureId);
        }
    }

    public void DropConnection(string connectionId)
    {
        lock (_gate)
        {
            if (_byConnection.Remove(connectionId, out var set))
                foreach (var f in set) Decrement(f);
        }
    }

    private void Decrement(string fixtureId)
    {
        if (!_refcount.TryGetValue(fixtureId, out var c)) return;
        if (c <= 1) _refcount.Remove(fixtureId);
        else _refcount[fixtureId] = c - 1;
    }

    /// <summary>Watched fixtures, most-subscribed first (so a cap keeps the most-wanted matches).</summary>
    public IReadOnlyList<string> Active()
    {
        lock (_gate)
            return _refcount.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToList();
    }

    public int Count { get { lock (_gate) return _refcount.Count; } }
}
