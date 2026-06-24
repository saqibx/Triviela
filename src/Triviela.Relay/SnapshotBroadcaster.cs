using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Triviela.Core;
using Triviela.Domain;

namespace Triviela.Relay;

/// <summary>
/// Bridges the poller's <see cref="SnapshotStore"/> to SignalR + Redis. Every published snapshot
/// is pushed to that fixture's group; Redis writes are THROTTLED (the cache only needs to be
/// recent-enough for late-joiners — live updates go over SignalR), which keeps Upstash bandwidth
/// well within the free tier even on a busy slate. Uses the previously-unused
/// <see cref="SnapshotStore.Subscribe"/>.
/// </summary>
public sealed class SnapshotBroadcaster(
    SnapshotStore store,
    FocusState focus,
    IHubContext<MatchHub> hub,
    IRelaySnapshotCache cache,
    ILogger<SnapshotBroadcaster> logger) : IHostedService
{
    // The poller publishes several times per tick per fixture; the cache only needs the latest
    // every so often, so coalesce Redis writes to at most once per fixture per this interval.
    private static readonly TimeSpan CacheWriteInterval = TimeSpan.FromSeconds(20);
    private const string LiveListKey = "__live__";

    private readonly ConcurrentDictionary<string, DateTime> _lastCacheWriteUtc = new();
    private IDisposable? _subscription;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = store.Subscribe(OnSnapshot);
        focus.Changed += OnLiveChanged;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        focus.Changed -= OnLiveChanged;
        return Task.CompletedTask;
    }

    private void OnSnapshot(MatchSnapshot snapshot)
    {
        var id = snapshot.Fixture.Id;
        Forget(hub.Clients.Group($"fixture:{id}").SendAsync("Snapshot", snapshot)); // live: every publish
        if (ShouldWriteCache(id))
            Forget(cache.SetSnapshotAsync(snapshot));                                // durable: throttled
    }

    private void OnLiveChanged()
    {
        var live = focus.Live;
        Forget(hub.Clients.All.SendAsync("LiveFixtures", live));
        if (ShouldWriteCache(LiveListKey))
            Forget(cache.SetLiveAsync(live));
    }

    private bool ShouldWriteCache(string key)
    {
        var now = DateTime.UtcNow;
        if (_lastCacheWriteUtc.TryGetValue(key, out var last) && now - last < CacheWriteInterval)
            return false;
        _lastCacheWriteUtc[key] = now;
        return true;
    }

    private void Forget(Task task) =>
        task.ContinueWith(t => logger.LogDebug(t.Exception, "relay push failed"), TaskContinuationOptions.OnlyOnFaulted);
}
