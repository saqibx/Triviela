using Microsoft.Extensions.Options;
using Triviela.Domain;
using Triviela.Providers;

namespace Triviela.Core;

public sealed class CompositeFootballDataSource : IFootballDataSource
{
    public const string DemoPrefix = "demo-";

    private readonly DemoDataSource _demo;
    private readonly IFootballDataSource _backing;
    private readonly bool _demoOnly;

    // One-time source selection (NOT a per-tick fallback — mixing demo/real once caused a bug):
    // API-Football if a key is configured → else keyless ESPN (real free data) → else demo.
    public CompositeFootballDataSource(
        ApiFootballDataSource apiFootball,
        EspnFootballDataSource espn,
        DemoDataSource demo,
        IOptions<ApiFootballOptions> apiOptions,
        IOptions<EspnOptions> espnOptions)
    {
        _demo = demo;
        if (apiOptions.Value.IsConfigured) { _backing = apiFootball; _demoOnly = false; }
        else if (espnOptions.Value.IsConfigured) { _backing = espn; _demoOnly = false; }
        else { _backing = demo; _demoOnly = true; }
    }

    public string Name => $"composite:{_backing.Name}";

    private IFootballDataSource Route(string fixtureId) =>
        _demoOnly || fixtureId.StartsWith(DemoPrefix, StringComparison.Ordinal) ? _demo : _backing;

    public Task<IReadOnlyList<Fixture>> GetLiveFixturesAsync(CancellationToken ct) =>
        _demoOnly ? _demo.GetLiveFixturesAsync(ct) : _backing.GetLiveFixturesAsync(ct);

    public Task<Fixture?> GetFixtureAsync(string fixtureId, CancellationToken ct) =>
        Route(fixtureId).GetFixtureAsync(fixtureId, ct);

    public Task<IReadOnlyList<MatchEvent>> GetEventsAsync(string fixtureId, string? homeTeamId, CancellationToken ct) =>
        Route(fixtureId).GetEventsAsync(fixtureId, homeTeamId, ct);

    public Task<(Lineup? Home, Lineup? Away)> GetLineupsAsync(string fixtureId, CancellationToken ct) =>
        Route(fixtureId).GetLineupsAsync(fixtureId, ct);

    public Task<(TeamStatistics? Home, TeamStatistics? Away)> GetStatisticsAsync(string fixtureId, CancellationToken ct) =>
        Route(fixtureId).GetStatisticsAsync(fixtureId, ct);

    public Task<Venue?> GetVenueAsync(string fixtureId, CancellationToken ct) =>
        Route(fixtureId).GetVenueAsync(fixtureId, ct);

    public Task<(IReadOnlyList<RatedPlayer> Home, IReadOnlyList<RatedPlayer> Away)> GetPlayerRatingsAsync(string fixtureId, CancellationToken ct) =>
        Route(fixtureId).GetPlayerRatingsAsync(fixtureId, ct);

    public Task<MatchOdds?> GetOddsAsync(string fixtureId, CancellationToken ct) =>
        Route(fixtureId).GetOddsAsync(fixtureId, ct);
}

public sealed class CompositeWeatherSource(
    OpenMeteoWeatherSource primary,
    DemoDataSource demo) : IWeatherSource
{
    public async Task<Weather?> GetWeatherAsync(double latitude, double longitude, CancellationToken ct) =>
        await primary.GetWeatherAsync(latitude, longitude, ct)
        ?? await demo.GetWeatherAsync(latitude, longitude, ct);

    public Task<(double Latitude, double Longitude)?> GeocodeAsync(string place, CancellationToken ct) =>
        primary.GeocodeAsync(place, ct);
}
