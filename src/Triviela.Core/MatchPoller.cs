using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Triviela.Domain;

namespace Triviela.Core;

public sealed class TriviaelaOptions
{
    public const string SectionName = "Triviela";

    public int LivePollSeconds { get; set; } = 12;

    public int WeatherPollMinutes { get; set; } = 30;

    public int NarrativePollMinutes { get; set; } = 3;

    public int IntelPollSeconds { get; set; } = 300;

    public int OddsPollMinutes { get; set; } = 5;

    public int ApiFootballDailyBudget { get; set; } = 100;

    /// <summary>Cap on how many distinct fixtures the poller will enrich per cycle. The most-subscribed
    /// fixtures win; the rest serve whatever is cached. Bounds LLM/API spend on a shared key.</summary>
    public int MaxConcurrentFixtures { get; set; } = 8;
}

public sealed class MatchPoller(
    IFootballDataSource football,
    IFootballReference reference,
    IWeatherSource weather,
    INarrativeSource narrative,
    IMatchIntel intel,
    IMatchFactbook factbook,
    ISocialPulse social,
    Providers.LlmCostMeter costMeter,
    SnapshotStore store,
    FocusState focus,
    RateLimitGovernor governor,
    FreeModeState free,
    SubscriptionRegistry registry,
    Microsoft.Extensions.Options.IOptions<TriviaelaOptions> options,
    Microsoft.Extensions.Options.IOptions<Providers.ApiFootballOptions> apiFootballOptions,
    ILogger<MatchPoller> logger) : BackgroundService
{
    private readonly TriviaelaOptions _opts = options.Value;
    private readonly bool _apiFootballConfigured = apiFootballOptions.Value.IsConfigured;

    private CancellationToken _stopping;

    // Per-fixture poll caches, keyed by fixtureId. Touched only by the single-threaded poll loop
    // (plus the volatile Facts field, written by the background fact-book task).
    private readonly ConcurrentDictionary<string, FixturePollState> _states = new();

    private int _lastConfiguredBudget = -1;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;

        logger.LogInformation("MatchPoller started (live cadence {Seconds}s)", _opts.LivePollSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            if (_apiFootballConfigured)
            {
                var budget = free.CapDailyBudget(_opts.ApiFootballDailyBudget);
                if (budget != _lastConfiguredBudget)
                {
                    governor.Configure("api-football", budget, budget / 86_400.0);
                    _lastConfiguredBudget = budget;
                    logger.LogInformation("api-football budget set to {Budget}/day{Free}",
                        budget, free.Enabled ? " (FREE mode)" : "");
                }
            }

            try
            {
                await PollOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Poll cycle failed; retrying next tick");
            }

            try
            {
                var cadence = free.Enabled ? free.LivePollSeconds : _opts.LivePollSeconds;
                if (free.Enabled)
                    logger.LogDebug("FREE mode active — essentials only, next poll in {Seconds}s", cadence);
                await Task.Delay(TimeSpan.FromSeconds(cadence), stoppingToken);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        var live = await GovernedAsync("api-football", () => football.GetLiveFixturesAsync(ct), [], "live-fixtures");
        focus.SetLive(live);
        store.Retain(live.Select(f => f.Id), focus.SelectedId);

        var liveById = live.ToDictionary(f => f.Id);

        // Enrich only fixtures that are both subscribed and live, most-subscribed first, capped.
        var targets = registry.Active()
            .Where(liveById.ContainsKey)
            .Take(Math.Max(1, _opts.MaxConcurrentFixtures))
            .ToList();

        if (targets.Count == 0)
        {
            logger.LogDebug("No subscribed/live fixtures this cycle");
            EvictStatesNotIn(targets);
            return;
        }

        foreach (var id in targets)
        {
            var fixture = liveById[id];
            var state = _states.GetOrAdd(id, _ =>
            {
                // The cost meter is single-match; starting it per new fixture keeps local mode exact.
                // In the multi-match relay it is approximate — the relay adds its own global LLM guard.
                costMeter.StartMatch(id);
                return new FixturePollState(id);
            });

            try
            {
                await EnrichFixtureAsync(state, fixture, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Enrichment failed for fixture {Fixture}", id);
            }
        }

        EvictStatesNotIn(targets);
    }

    private void EvictStatesNotIn(IReadOnlyCollection<string> keep)
    {
        foreach (var id in _states.Keys)
            if (!keep.Contains(id))
                _states.TryRemove(id, out _);
    }

    private async Task EnrichFixtureAsync(FixturePollState state, Fixture fixture, CancellationToken ct)
    {
        var id = fixture.Id;

        if (!state.StaticFetched)
        {
            state.Venue = await GovernedAsync("api-football", () => football.GetVenueAsync(id, ct), state.Venue, "venue");
            (state.HomeLineup, state.AwayLineup) = await GovernedAsync("api-football", () => football.GetLineupsAsync(id, ct), (state.HomeLineup, state.AwayLineup), "lineups");

            if (reference.IsLive)
                state.HeadToHead = await GovernedAsync("api-football", () => reference.GetHeadToHeadByIdsAsync(
                    fixture.Home.Id, fixture.Home.Name, fixture.Away.Id, fixture.Away.Name, ct), state.HeadToHead, "h2h");

            if (state.Venue is not null)
                state.StaticFetched = true;
        }

        state.LastEvents = await GovernedAsync("api-football", () => football.GetEventsAsync(id, fixture.Home.Id, ct), state.LastEvents, "events");

        if (!free.Enabled)
        {
            (state.HomeStats, state.AwayStats) = await GovernedAsync("api-football", () => football.GetStatisticsAsync(id, ct), (state.HomeStats, state.AwayStats), "stats");
            (state.HomeRatings, state.AwayRatings) = await GovernedAsync("api-football", () => football.GetPlayerRatingsAsync(id, ct), (state.HomeRatings, state.AwayRatings), "ratings");
        }

        var snapshot = state.BuildSnapshot(fixture);
        store.Publish(snapshot);

        var enriched = false;
        if (state.Venue is not null &&
            DateTimeOffset.UtcNow - state.WeatherFetchedUtc > TimeSpan.FromMinutes(_opts.WeatherPollMinutes))
        {
            if (state.Venue is { Latitude: null } && !string.IsNullOrWhiteSpace(state.Venue.City))
            {
                var place = state.Venue.Country is { } country ? $"{state.Venue.City}, {country}" : state.Venue.City!;
                var coords = await GuardedAsync(() => weather.GeocodeAsync(place, ct), null, "geocode");
                if (coords is { } g)
                    state.Venue = state.Venue with { Latitude = g.Latitude, Longitude = g.Longitude };
            }

            if (state.Venue is { Latitude: not null, Longitude: not null })
            {
                state.Weather = await GuardedAsync(() => weather.GetWeatherAsync(state.Venue.Latitude.Value, state.Venue.Longitude.Value, ct), state.Weather, "weather");
                state.WeatherFetchedUtc = DateTimeOffset.UtcNow;
                enriched = true;
            }
        }

        if (!free.Enabled && DateTimeOffset.UtcNow - state.OddsFetchedUtc > TimeSpan.FromMinutes(_opts.OddsPollMinutes))
        {
            state.Odds = await GovernedAsync("api-football", () => football.GetOddsAsync(id, ct), state.Odds, "odds");
            state.OddsFetchedUtc = DateTimeOffset.UtcNow;
            enriched = true;
        }

        if (DateTimeOffset.UtcNow - state.SocialFetchedUtc > TimeSpan.FromMinutes(2))
        {
            state.Social = await GuardedAsync(() => social.GetMatchChatterAsync(fixture.Home.Name, fixture.Away.Name, ct), state.Social, "social");
            state.SocialFetchedUtc = DateTimeOffset.UtcNow;
            enriched = true;
        }

        if (enriched)
        {
            snapshot = state.BuildSnapshot(fixture);
            store.Publish(snapshot);
        }

        if (factbook.IsEnabled && !state.FactsRequested)
        {
            state.FactsRequested = true;
            var forFixture = fixture;
            _ = Task.Run(async () =>
            {
                try
                {
                    var facts = await factbook.BuildAsync(forFixture, _stopping);
                    if (facts is { Count: > 0 } && _states.ContainsKey(forFixture.Id))
                        state.Facts = facts;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Factbook build failed");
                }
            }, _stopping);
        }

        if (DateTimeOffset.UtcNow - state.NarrativeFetchedUtc > TimeSpan.FromMinutes(_opts.NarrativePollMinutes))
        {
            var fresh = await GuardedAsync(() => narrative.SummarizeAsync(snapshot, ct), null, "narrative");
            if (fresh is not null)
            {
                state.Narrative = fresh;
                snapshot = state.BuildSnapshot(fixture);
                store.Publish(snapshot);
            }
            state.NarrativeFetchedUtc = DateTimeOffset.UtcNow;
        }

        // News intel is a ONE-SHOT batch (web search costs money) — generated once per match, after
        // lineups are known so we research the actual people, then cached for the whole match.
        if (intel.IsEnabled && !state.IntelRequested && state.HomeLineup is not null)
        {
            state.IntelRequested = true;
            var forFixture = fixture;
            var forSnapshot = snapshot;
            _ = Task.Run(async () =>
            {
                try
                {
                    var items = await intel.BuildAsync(forSnapshot, 6, _stopping);
                    if (items.Count > 0 && _states.ContainsKey(forFixture.Id))
                        state.Intel = items;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Intel build failed");
                }
            }, _stopping);
        }
    }

    private async Task<T> GuardedAsync<T>(Func<Task<T>> call, T fallback, string label)
    {
        try
        {
            return await call();
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Slice '{Label}' failed; serving last-known/empty", label);
            return fallback;
        }
    }

    private async Task<T> GovernedAsync<T>(string provider, Func<Task<T>> call, T fallback, string label)
    {
        if (!governor.TryAcquire(provider))
        {
            logger.LogDebug("Rate limit reached for '{Provider}'; serving cache for '{Label}'", provider, label);
            return fallback;
        }
        return await GuardedAsync(call, fallback, label);
    }
}
