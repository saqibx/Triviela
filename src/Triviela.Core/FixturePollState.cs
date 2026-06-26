using Triviela.Domain;

namespace Triviela.Core;

/// <summary>
/// Per-fixture poll cache. This is a 1:1 lift of the single-fixture mutable state that
/// <see cref="MatchPoller"/> used to hold in instance fields, now keyed per fixture so the
/// poller can enrich the *set* of fixtures users are watching (the relay) while local mode
/// just holds one. Not thread-safe beyond the volatile <see cref="Facts"/> field, which the
/// background fact-book task writes from another thread; everything else is touched only by the
/// single-threaded poll loop.
/// </summary>
public sealed class FixturePollState(string fixtureId)
{
    public string FixtureId { get; } = fixtureId;

    public bool StaticFetched;
    public Venue? Venue;
    public Lineup? HomeLineup;
    public Lineup? AwayLineup;
    public HeadToHead? HeadToHead;
    public Weather? Weather;
    public MatchOdds? Odds;

    public IReadOnlyList<MatchEvent> LastEvents = [];
    public TeamStatistics? HomeStats;
    public TeamStatistics? AwayStats;
    public IReadOnlyList<RatedPlayer> HomeRatings = [];
    public IReadOnlyList<RatedPlayer> AwayRatings = [];
    public MatchNarrative? Narrative;
    public IReadOnlyList<SocialPost> Social = [];

    // Intel and Facts are one-shot, generated once per match by a background task (hence volatile).
    public volatile IReadOnlyList<MatchIntelItem> Intel = [];
    public bool IntelRequested;
    public volatile IReadOnlyList<MatchFact> Facts = [];
    public bool FactsRequested;

    public DateTimeOffset WeatherFetchedUtc = DateTimeOffset.MinValue;
    public DateTimeOffset OddsFetchedUtc = DateTimeOffset.MinValue;
    public DateTimeOffset NarrativeFetchedUtc = DateTimeOffset.MinValue;
    public DateTimeOffset SocialFetchedUtc = DateTimeOffset.MinValue;

    public MatchSnapshot BuildSnapshot(Fixture fixture) => new()
    {
        Fixture = fixture,
        Venue = Venue,
        HomeLineup = HomeLineup,
        AwayLineup = AwayLineup,
        HomeStats = HomeStats,
        AwayStats = AwayStats,
        Events = LastEvents,
        Weather = Weather,
        Odds = Odds,
        Narrative = Narrative,
        HomeRatings = HomeRatings,
        AwayRatings = AwayRatings,
        HeadToHead = HeadToHead,
        Social = Social,
        Intel = Intel,
        Facts = Facts,
        UpdatedAtUtc = DateTimeOffset.UtcNow
    };
}
