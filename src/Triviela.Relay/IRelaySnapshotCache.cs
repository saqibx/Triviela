using Triviela.Domain;

namespace Triviela.Relay;

/// <summary>Durable last-known state so late-joiners render instantly and data survives relay restarts.</summary>
public interface IRelaySnapshotCache
{
    Task SetSnapshotAsync(MatchSnapshot snapshot);
    Task<MatchSnapshot?> GetSnapshotAsync(string fixtureId);
    Task SetLiveAsync(IReadOnlyList<Fixture> live);
    Task<IReadOnlyList<Fixture>?> GetLiveAsync();
}

/// <summary>No-op cache for when Redis isn't configured — the relay still runs (memory-only).</summary>
public sealed class NullRelaySnapshotCache : IRelaySnapshotCache
{
    public Task SetSnapshotAsync(MatchSnapshot snapshot) => Task.CompletedTask;
    public Task<MatchSnapshot?> GetSnapshotAsync(string fixtureId) => Task.FromResult<MatchSnapshot?>(null);
    public Task SetLiveAsync(IReadOnlyList<Fixture> live) => Task.CompletedTask;
    public Task<IReadOnlyList<Fixture>?> GetLiveAsync() => Task.FromResult<IReadOnlyList<Fixture>?>(null);
}
