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
    Microsoft.Extensions.Options.IOptions<TriviaelaOptions> options,
    Microsoft.Extensions.Options.IOptions<Providers.ApiFootballOptions> apiFootballOptions,
    ILogger<MatchPoller> logger) : BackgroundService
{
    private readonly TriviaelaOptions _opts = options.Value;
    private readonly bool _apiFootballConfigured = apiFootballOptions.Value.IsConfigured;

    private CancellationToken _stopping;

    private string? _currentFixtureId;
    private Venue? _venue;
    private Lineup? _homeLineup, _awayLineup;
    private HeadToHead? _h2h;
    private Weather? _weather;
    private MatchOdds? _odds;

    private IReadOnlyList<MatchEvent> _lastEvents = [];
    private TeamStatistics? _homeStats, _awayStats;
    private IReadOnlyList<RatedPlayer> _homeRatings = [], _awayRatings = [];
    private MatchNarrative? _narrative;
    private IReadOnlyList<SocialPost> _social = [];
    private readonly List<MatchIntelItem> _intel = [];

    private volatile IReadOnlyList<MatchFact> _facts = [];
    private bool _factsRequested;
    private DateTimeOffset _weatherFetchedUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _oddsFetchedUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _narrativeFetchedUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _socialFetchedUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _intelFetchedUtc = DateTimeOffset.MinValue;
    private bool _staticFetched;

    private int _lastConfiguredBudget = -1;

    private void ResetFixtureCache(string fixtureId)
    {
        _currentFixtureId = fixtureId;

        costMeter.StartMatch(fixtureId);
        _staticFetched = false;
        _venue = null;
        _homeLineup = _awayLineup = null;
        _h2h = null;
        _weather = null;
        _odds = null;
        _lastEvents = [];
        _homeStats = _awayStats = null;
        _homeRatings = _awayRatings = [];
        _narrative = null;
        _social = [];
        _intel.Clear();
        _facts = [];
        _factsRequested = false;
        _weatherFetchedUtc = DateTimeOffset.MinValue;
        _oddsFetchedUtc = DateTimeOffset.MinValue;
        _narrativeFetchedUtc = DateTimeOffset.MinValue;
        _socialFetchedUtc = DateTimeOffset.MinValue;
        _intelFetchedUtc = DateTimeOffset.MinValue;
    }

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

        var selectedId = focus.SelectedId;
        var fixture = selectedId is null ? null : live.FirstOrDefault(f => f.Id == selectedId);
        if (fixture is null)
        {
            logger.LogDebug("No selected/live fixture this cycle");
            return;
        }

        var id = fixture.Id;

        if (id != _currentFixtureId)
            ResetFixtureCache(id);

        if (!_staticFetched)
        {
            _venue = await GovernedAsync("api-football", () => football.GetVenueAsync(id, ct), _venue, "venue");
            (_homeLineup, _awayLineup) = await GovernedAsync("api-football", () => football.GetLineupsAsync(id, ct), (_homeLineup, _awayLineup), "lineups");

            if (reference.IsLive)
                _h2h = await GovernedAsync("api-football", () => reference.GetHeadToHeadByIdsAsync(
                    fixture.Home.Id, fixture.Home.Name, fixture.Away.Id, fixture.Away.Name, ct), _h2h, "h2h");

            if (_venue is not null)
                _staticFetched = true;
        }

        var events = await GovernedAsync("api-football", () => football.GetEventsAsync(id, fixture.Home.Id, ct), _lastEvents, "events");
        _lastEvents = events;

        var (homeStats, awayStats) = (_homeStats, _awayStats);
        var (homeRatings, awayRatings) = (_homeRatings, _awayRatings);
        if (!free.Enabled)
        {
            (homeStats, awayStats) = await GovernedAsync("api-football", () => football.GetStatisticsAsync(id, ct), (_homeStats, _awayStats), "stats");
            (homeRatings, awayRatings) = await GovernedAsync("api-football", () => football.GetPlayerRatingsAsync(id, ct), (_homeRatings, _awayRatings), "ratings");
            _homeStats = homeStats; _awayStats = awayStats;
            _homeRatings = homeRatings; _awayRatings = awayRatings;
        }

        var snapshot = new MatchSnapshot
        {
            Fixture = fixture,
            Venue = _venue,
            HomeLineup = _homeLineup,
            AwayLineup = _awayLineup,
            HomeStats = homeStats,
            AwayStats = awayStats,
            Events = events,
            Weather = _weather,
            Odds = _odds,
            Narrative = _narrative,
            HomeRatings = homeRatings,
            AwayRatings = awayRatings,
            HeadToHead = _h2h,
            Social = _social,
            Intel = _intel.ToArray(),
            Facts = _facts,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };

        store.Publish(snapshot);

        var enriched = false;
        if (_venue is not null &&
            DateTimeOffset.UtcNow - _weatherFetchedUtc > TimeSpan.FromMinutes(_opts.WeatherPollMinutes))
        {
            if (_venue is { Latitude: null } && !string.IsNullOrWhiteSpace(_venue.City))
            {
                var place = _venue.Country is { } country ? $"{_venue.City}, {country}" : _venue.City!;
                var coords = await GuardedAsync(() => weather.GeocodeAsync(place, ct), null, "geocode");
                if (coords is { } g)
                    _venue = _venue with { Latitude = g.Latitude, Longitude = g.Longitude };
            }

            if (_venue is { Latitude: not null, Longitude: not null })
            {
                _weather = await GuardedAsync(() => weather.GetWeatherAsync(_venue.Latitude.Value, _venue.Longitude.Value, ct), _weather, "weather");
                _weatherFetchedUtc = DateTimeOffset.UtcNow;
                enriched = true;
            }
        }

        if (!free.Enabled && DateTimeOffset.UtcNow - _oddsFetchedUtc > TimeSpan.FromMinutes(_opts.OddsPollMinutes))
        {
            _odds = await GovernedAsync("api-football", () => football.GetOddsAsync(id, ct), _odds, "odds");
            _oddsFetchedUtc = DateTimeOffset.UtcNow;
            enriched = true;
        }

        if (DateTimeOffset.UtcNow - _socialFetchedUtc > TimeSpan.FromMinutes(2))
        {
            _social = await GuardedAsync(() => social.GetMatchChatterAsync(fixture.Home.Name, fixture.Away.Name, ct), _social, "social");
            _socialFetchedUtc = DateTimeOffset.UtcNow;
            enriched = true;
        }

        if (enriched)
        {
            snapshot = snapshot with { Venue = _venue, Weather = _weather, Odds = _odds, Social = _social };
            store.Publish(snapshot);
        }

        if (factbook.IsEnabled && !_factsRequested)
        {
            _factsRequested = true;
            var forFixture = fixture;
            _ = Task.Run(async () =>
            {
                try
                {
                    var facts = await factbook.BuildAsync(forFixture, _stopping);

                    if (facts is { Count: > 0 } && forFixture.Id == _currentFixtureId)
                        _facts = facts;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Factbook build failed");
                }
            }, _stopping);
        }

        if (DateTimeOffset.UtcNow - _narrativeFetchedUtc > TimeSpan.FromMinutes(_opts.NarrativePollMinutes))
        {
            var fresh = await GuardedAsync(() => narrative.SummarizeAsync(snapshot, ct), null, "narrative");
            if (fresh is not null)
            {
                _narrative = fresh;
                snapshot = snapshot with { Narrative = fresh };
                store.Publish(snapshot);
            }
            _narrativeFetchedUtc = DateTimeOffset.UtcNow;
        }

        if (intel.IsEnabled && DateTimeOffset.UtcNow - _intelFetchedUtc > TimeSpan.FromSeconds(_opts.IntelPollSeconds))
        {
            var avoid = _intel.Where(i => i.Subject is not null).Select(i => i.Subject!).Distinct().ToArray();
            var item = await GuardedAsync(() => intel.NextAsync(snapshot, avoid, ct), null, "intel");
            if (item is not null)
            {
                _intel.Insert(0, item);
                if (_intel.Count > 8) _intel.RemoveRange(8, _intel.Count - 8);
                snapshot = snapshot with { Intel = _intel.ToArray() };
                store.Publish(snapshot);
            }
            _intelFetchedUtc = DateTimeOffset.UtcNow;
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
