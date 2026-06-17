using Microsoft.Extensions.Options;
using Triviela.Domain;
using Triviela.Providers;

namespace Triviela.Core;

public sealed class CompositeFootballDataSource(
    ApiFootballDataSource primary,
    DemoDataSource demo,
    IOptions<ApiFootballOptions> options) : IFootballDataSource
{
    public const string DemoPrefix = "demo-";

    private readonly bool _live = options.Value.IsConfigured;

    public string Name => "composite";

    private IFootballDataSource Route(string fixtureId) =>
        !_live || fixtureId.StartsWith(DemoPrefix, StringComparison.Ordinal) ? demo : primary;

    public Task<IReadOnlyList<Fixture>> GetLiveFixturesAsync(CancellationToken ct) =>
        _live ? primary.GetLiveFixturesAsync(ct) : demo.GetLiveFixturesAsync(ct);

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
