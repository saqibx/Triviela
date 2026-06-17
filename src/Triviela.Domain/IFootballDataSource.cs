namespace Triviela.Domain;

public interface IFootballDataSource
{
    string Name { get; }

    Task<IReadOnlyList<Fixture>> GetLiveFixturesAsync(CancellationToken ct);

    Task<Fixture?> GetFixtureAsync(string fixtureId, CancellationToken ct);

    Task<IReadOnlyList<MatchEvent>> GetEventsAsync(string fixtureId, string? homeTeamId, CancellationToken ct);

    Task<(Lineup? Home, Lineup? Away)> GetLineupsAsync(string fixtureId, CancellationToken ct);

    Task<(TeamStatistics? Home, TeamStatistics? Away)> GetStatisticsAsync(string fixtureId, CancellationToken ct);

    Task<Venue?> GetVenueAsync(string fixtureId, CancellationToken ct);

    Task<(IReadOnlyList<RatedPlayer> Home, IReadOnlyList<RatedPlayer> Away)> GetPlayerRatingsAsync(string fixtureId, CancellationToken ct);

    Task<MatchOdds?> GetOddsAsync(string fixtureId, CancellationToken ct);
}

public interface IWeatherSource
{
    Task<Weather?> GetWeatherAsync(double latitude, double longitude, CancellationToken ct);

    Task<(double Latitude, double Longitude)?> GeocodeAsync(string place, CancellationToken ct);
}

public interface IMatchFactbook
{
    bool IsEnabled { get; }

    Task<IReadOnlyList<MatchFact>?> BuildAsync(Fixture fixture, CancellationToken ct);
}

public interface INarrativeSource
{
    Task<MatchNarrative?> SummarizeAsync(MatchSnapshot snapshot, CancellationToken ct);
}

public interface IFootballReference
{
    bool IsLive { get; }

    Task<TeamProfile?> GetTeamProfileAsync(string teamQuery, CancellationToken ct);

    Task<PlayerProfile?> GetPlayerProfileAsync(string playerQuery, string? fixtureId, CancellationToken ct);

    Task<HeadToHead?> GetHeadToHeadAsync(string teamAQuery, string teamBQuery, CancellationToken ct);

    Task<HeadToHead?> GetHeadToHeadByIdsAsync(string idA, string nameA, string idB, string nameB, CancellationToken ct);
}

public interface IMatchAnalyst
{
    bool IsEnabled { get; }
    Task<string?> AskAsync(MatchSnapshot snapshot, string question, CancellationToken ct);
}

public interface IMatchIntel
{
    bool IsEnabled { get; }

    Task<MatchIntelItem?> NextAsync(MatchSnapshot snapshot, IReadOnlyCollection<string> avoidSubjects, CancellationToken ct);
}
