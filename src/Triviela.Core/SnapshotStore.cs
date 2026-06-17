using System.Collections.Concurrent;
using Triviela.Domain;

namespace Triviela.Core;

public sealed class SnapshotStore
{
    private readonly ConcurrentDictionary<string, MatchSnapshot> _latest = new();
    private readonly ConcurrentDictionary<string, List<Action<MatchSnapshot>>> _subscribers = new();
    private readonly List<Action<MatchSnapshot>> _wildcard = [];
    private readonly object _gate = new();

    public MatchSnapshot? Get(string fixtureId) =>
    _latest.TryGetValue(fixtureId, out var s) ? s : null;

    public IReadOnlyCollection<MatchSnapshot> All() => _latest.Values.ToArray();

    public void Retain(IEnumerable<string> liveIds, string? keepId = null)
    {
        var keep = new HashSet<string>(liveIds);
        if (keepId is not null) keep.Add(keepId);
        foreach (var id in _latest.Keys)
        {
            if (!keep.Contains(id))
                _latest.TryRemove(id, out _);
        }
    }

    public void Publish(MatchSnapshot snapshot)
    {
        var id = snapshot.Fixture.Id;
        _latest[id] = snapshot;

        Action<MatchSnapshot>[] handlers;
        lock (_gate)
        {
            var perFixture = _subscribers.TryGetValue(id, out var list) ? list : [];
            handlers = perFixture.Concat(_wildcard).ToArray();
        }
        foreach (var h in handlers)
        {
            try { h(snapshot); } catch { }
        }
    }

    public IDisposable Subscribe(Action<MatchSnapshot> handler)
    {
        lock (_gate) { _wildcard.Add(handler); }
        return new Unsubscriber(this, null, handler);
    }

    public IDisposable Subscribe(string fixtureId, Action<MatchSnapshot> handler)
    {
        lock (_gate)
        {
            var list = _subscribers.GetOrAdd(fixtureId, _ => []);
            list.Add(handler);
        }
        return new Unsubscriber(this, fixtureId, handler);
    }

    private void Unsubscribe(string? fixtureId, Action<MatchSnapshot> handler)
    {
        lock (_gate)
        {
            if (fixtureId is null)
                _wildcard.Remove(handler);
            else if (_subscribers.TryGetValue(fixtureId, out var list))
                list.Remove(handler);
        }
    }

    private sealed class Unsubscriber(SnapshotStore store, string? fixtureId, Action<MatchSnapshot> handler) : IDisposable
    {
        public void Dispose() => store.Unsubscribe(fixtureId, handler);
    }
}
