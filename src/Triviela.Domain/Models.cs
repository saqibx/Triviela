namespace Triviela.Domain;

public record Team(
    string Id,
    string Name,
    string ShortName,
    string? Country = null,
    string? LogoUrl = null);

public record Player(
    string Id,
    string Name,
    string? Position = null,
    int? Number = null,
    int? Age = null,
    string? Nationality = null,
    string? Club = null,
    string? PhotoUrl = null,
    decimal? MarketValueEur = null);

public record Manager(
    string Id,
    string Name,
    string? Nationality = null,
    int? Age = null,
    string? PreferredFormation = null);

public record Score(int Home, int Away)
{
    public override string ToString() => $"{Home}-{Away}";
}

public record Venue(
    string Name,
    string? City = null,
    string? Country = null,
    int? Capacity = null,
    double? Latitude = null,
    double? Longitude = null,
    string? Surface = null);

public record MatchEvent(
    int Minute,
    int? MinuteExtra,
    MatchEventType Type,
    Side Side,
    string? PlayerName,
    string? AssistName = null,
    string? Detail = null)
{
    public string Clock => MinuteExtra is > 0 ? $"{Minute}+{MinuteExtra}'" : $"{Minute}'";
}

public record TeamStatistics(
    int? PossessionPercent = null,
    int? Shots = null,
    int? ShotsOnTarget = null,
    int? Corners = null,
    int? Fouls = null,
    int? Offsides = null,
    int? PassingAccuracyPercent = null,
    double? ExpectedGoals = null);

public record Lineup(
    string? Formation,
    IReadOnlyList<Player> StartingXI,
    IReadOnlyList<Player> Bench,
    string? CaptainPlayerId = null);

public record Weather(
    double TemperatureC,
    double WindSpeedKph,
    double PrecipitationMm,
    int? Humidity,
    string Summary);

public record MatchOdds(
    double? HomeWin,
    double? Draw,
    double? AwayWin,
    string? Bookmaker = null,
    DateTimeOffset UpdatedUtc = default)
{
    private static int? Implied(double? odd) => odd is > 0 ? (int)Math.Round(100.0 / odd.Value) : null;

    public int? HomeImplied => Implied(HomeWin);
    public int? DrawImplied => Implied(Draw);
    public int? AwayImplied => Implied(AwayWin);
}

public record MatchFact(string Text, string? Category = null);

public record Fixture(
    string Id,
    Team Home,
    Team Away,
    Score Score,
    MatchStatus Status,
    int Minute,
    DateTimeOffset KickoffUtc,
    string? Competition = null,
    string? Referee = null);

public record MatchSnapshot
{
    public required Fixture Fixture { get; init; }
    public Venue? Venue { get; init; }
    public Manager? HomeManager { get; init; }
    public Manager? AwayManager { get; init; }
    public Lineup? HomeLineup { get; init; }
    public Lineup? AwayLineup { get; init; }
    public TeamStatistics? HomeStats { get; init; }
    public TeamStatistics? AwayStats { get; init; }
    public IReadOnlyList<MatchEvent> Events { get; init; } = [];
    public Weather? Weather { get; init; }
    public MatchNarrative? Narrative { get; init; }

    public MatchOdds? Odds { get; init; }

    public IReadOnlyList<MatchFact> Facts { get; init; } = [];

    public IReadOnlyList<RatedPlayer> HomeRatings { get; init; } = [];
    public IReadOnlyList<RatedPlayer> AwayRatings { get; init; } = [];

    public HeadToHead? HeadToHead { get; init; }

    public IReadOnlyList<SocialPost> Social { get; init; } = [];

    public IReadOnlyList<MatchIntelItem> Intel { get; init; } = [];

    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public record MatchNarrative(
    string OneLiner,
    int HomeSentiment,
    int AwaySentiment,
    string? HomeViewpoint = null,
    string? AwayViewpoint = null);

public record MatchIntelItem(
    string Insight,
    string? Subject = null,
    string? SourceUrl = null,
    DateTimeOffset CreatedUtc = default);
