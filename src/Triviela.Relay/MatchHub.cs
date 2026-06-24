using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Triviela.Core;
using Triviela.Domain;

namespace Triviela.Relay;

/// <summary>
/// The relay's SignalR surface. Clients watch fixtures (enrichment is demand-driven) and receive
/// pushed snapshots; lookups and ASK run server-side on the operator's keys, rate-limited so a
/// single client can't drain them.
/// </summary>
public sealed class MatchHub(
    SnapshotStore store,
    SubscriptionRegistry registry,
    FocusState focus,
    IRelaySnapshotCache cache,
    IFootballReference reference,
    IMatchAnalyst analyst,
    RelayRateLimiter limiter,
    ILogger<MatchHub> logger) : Hub
{
    private static string Group(string fixtureId) => $"fixture:{fixtureId}";

    public async Task<IReadOnlyList<Fixture>> GetLiveFixtures()
    {
        var live = focus.Live;
        if (live.Count == 0 && await cache.GetLiveAsync() is { } cached) return cached;
        return live;
    }

    public async Task WatchFixture(string fixtureId)
    {
        registry.Watch(Context.ConnectionId, fixtureId);
        await Groups.AddToGroupAsync(Context.ConnectionId, Group(fixtureId));

        var snapshot = store.Get(fixtureId) ?? await cache.GetSnapshotAsync(fixtureId);
        if (snapshot is not null)
            await Clients.Caller.SendAsync("Snapshot", snapshot);
    }

    public async Task UnwatchFixture(string fixtureId)
    {
        registry.Unwatch(Context.ConnectionId, fixtureId);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(fixtureId));
    }

    public Task<TeamProfile?> LookupTeam(string query) =>
        GuardedLookup(() => reference.GetTeamProfileAsync(query, Context.ConnectionAborted));

    public Task<PlayerProfile?> LookupPlayer(string query, string? fixtureId) =>
        GuardedLookup(() => reference.GetPlayerProfileAsync(query, fixtureId, Context.ConnectionAborted));

    public Task<HeadToHead?> LookupHeadToHead(string teamA, string teamB) =>
        GuardedLookup(() => reference.GetHeadToHeadAsync(teamA, teamB, Context.ConnectionAborted));

    public async Task<string?> Ask(string fixtureId, string question)
    {
        if (!limiter.TryAsk(Context.ConnectionId))
            return "Rate limit reached — please wait a moment before asking again.";
        if (string.IsNullOrWhiteSpace(question)) return null;
        if (question.Length > limiter.MaxQuestionLength) question = question[..limiter.MaxQuestionLength];

        var snapshot = store.Get(fixtureId);
        if (snapshot is null) return "That match isn't being tracked right now.";

        try
        {
            return await analyst.AskAsync(snapshot, question, Context.ConnectionAborted);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Ask failed");
            return null;
        }
    }

    public override async Task OnConnectedAsync()
    {
        await Clients.Caller.SendAsync("LiveFixtures", await GetLiveFixtures());
        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        registry.DropConnection(Context.ConnectionId);
        limiter.Drop(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    private async Task<T?> GuardedLookup<T>(Func<Task<T?>> call) where T : class
    {
        if (!limiter.TryLookup(Context.ConnectionId)) return null;
        try { return await call(); }
        catch (Exception ex) { logger.LogWarning(ex, "Lookup failed"); return null; }
    }
}
