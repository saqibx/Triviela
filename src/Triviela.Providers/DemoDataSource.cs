using Triviela.Domain;

namespace Triviela.Providers;

public sealed class DemoDataSource : IFootballDataSource, IWeatherSource
{
    public string Name => "demo";

    private static readonly Team Brazil = new("br", "Brazil", "BRA", "Brazil");
    private static readonly Team Argentina = new("ar", "Argentina", "ARG", "Argentina");

    private static readonly DateTimeOffset KickoffUtc = DateTimeOffset.UtcNow.AddMinutes(-37);

    private const string FixtureId = "demo-bra-arg";

    private static readonly MatchEvent[] Script =
    [
        new(12, null, MatchEventType.Goal, Side.Home, "Vinícius Jr", "Raphinha"),
        new(23, null, MatchEventType.YellowCard, Side.Away, "Rodrigo De Paul"),
        new(41, null, MatchEventType.Goal, Side.Away, "Lionel Messi", "Julián Álvarez", "Penalty"),
        new(58, null, MatchEventType.Substitution, Side.Home, "Endrick", "Richarlison off"),
        new(67, null, MatchEventType.Goal, Side.Home, "Endrick"),
        new(74, null, MatchEventType.RedCard, Side.Away, "Cristian Romero", null, "Second yellow"),
        new(88, 2, MatchEventType.Var, Side.Away, null, null, "Goal disallowed — offside"),
    ];

    private static int CurrentMinute()
    {
        var elapsed = (DateTimeOffset.UtcNow - KickoffUtc).TotalMinutes;

        return (int)Math.Clamp(elapsed % 100, 0, 95);
    }

    private static MatchEvent[] RevealedEvents()
    {
        var minute = CurrentMinute();
        return Script.Where(e => e.Minute <= minute).ToArray();
    }

    private static Score CurrentScore()
    {
        var revealed = RevealedEvents();
        int home = revealed.Count(e => e.Side == Side.Home &&
            e.Type is MatchEventType.Goal or MatchEventType.PenaltyGoal);
        int away = revealed.Count(e => e.Side == Side.Away &&
            e.Type is MatchEventType.Goal or MatchEventType.PenaltyGoal);
        return new Score(home, away);
    }

    private static Fixture BuildFixture()
    {
        var minute = CurrentMinute();
        var status = minute >= 90 ? MatchStatus.Finished
            : minute == 45 ? MatchStatus.HalfTime
            : MatchStatus.Live;
        return new Fixture(FixtureId, Brazil, Argentina, CurrentScore(), status, minute, KickoffUtc, "FIFA World Cup — Final", "Szymon Marciniak");
    }

    public Task<IReadOnlyList<Fixture>> GetLiveFixturesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Fixture>>([BuildFixture()]);

    public Task<Fixture?> GetFixtureAsync(string fixtureId, CancellationToken ct) =>
        Task.FromResult<Fixture?>(fixtureId == FixtureId ? BuildFixture() : null);

    public Task<IReadOnlyList<MatchEvent>> GetEventsAsync(string fixtureId, string? homeTeamId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<MatchEvent>>(RevealedEvents());

    public Task<(Lineup? Home, Lineup? Away)> GetLineupsAsync(string fixtureId, CancellationToken ct)
    {
        Lineup home = new("4-3-3",
            [
                new("br1", "Alisson", "GK", 1), new("br2", "Danilo", "RB", 2),
                new("br3", "Marquinhos", "CB", 4), new("br4", "Gabriel Magalhães", "CB", 3),
                new("br5", "Wendell", "LB", 6), new("br6", "Bruno Guimarães", "CM", 5),
                new("br7", "Lucas Paquetá", "CM", 17), new("br8", "Raphinha", "RW", 11),
                new("br9", "Vinícius Jr", "LW", 7), new("br10", "Rodrygo", "AM", 10),
                new("br11", "Richarlison", "ST", 9),
            ],
            [new("br12", "Endrick", "ST", 19), new("br13", "Éderson", "GK", 23)],
            "br3");
        Lineup away = new("4-4-2",
            [
                new("ar1", "Emiliano Martínez", "GK", 23), new("ar2", "Nahuel Molina", "RB", 26),
                new("ar3", "Cristian Romero", "CB", 13), new("ar4", "Nicolás Otamendi", "CB", 19),
                new("ar5", "Nicolás Tagliafico", "LB", 3), new("ar6", "Rodrigo De Paul", "RM", 7),
                new("ar7", "Enzo Fernández", "CM", 24), new("ar8", "Alexis Mac Allister", "CM", 20),
                new("ar9", "Ángel Di María", "LM", 11), new("ar10", "Lionel Messi", "ST", 10),
                new("ar11", "Julián Álvarez", "ST", 9),
            ],
            [new("ar12", "Lautaro Martínez", "ST", 22), new("ar13", "Gerónimo Rulli", "GK", 12)],
            "ar10");
        return Task.FromResult<(Lineup?, Lineup?)>((home, away));
    }

    public Task<(TeamStatistics? Home, TeamStatistics? Away)> GetStatisticsAsync(string fixtureId, CancellationToken ct)
    {
        var minute = Math.Max(CurrentMinute(), 1);

        double f = minute / 90.0;
        var home = new TeamStatistics(54, (int)(16 * f) + 2, (int)(6 * f) + 1, (int)(7 * f), (int)(9 * f), (int)(2 * f), 87, Math.Round(1.8 * f + 0.4, 2));
        var away = new TeamStatistics(46, (int)(11 * f) + 1, (int)(4 * f) + 1, (int)(4 * f), (int)(12 * f), (int)(3 * f), 81, Math.Round(1.3 * f + 0.2, 2));
        return Task.FromResult<(TeamStatistics?, TeamStatistics?)>((home, away));
    }

    public Task<Venue?> GetVenueAsync(string fixtureId, CancellationToken ct) =>

        Task.FromResult<Venue?>(new Venue("MetLife Stadium", "East Rutherford", "USA", 82500, 40.8135, -74.0745, "Grass"));

    public async Task<(IReadOnlyList<RatedPlayer> Home, IReadOnlyList<RatedPlayer> Away)> GetPlayerRatingsAsync(string fixtureId, CancellationToken ct)
    {
        var (home, away) = await GetLineupsAsync(fixtureId, ct);
        var revealed = RevealedEvents();
        return (RateLineup(home, revealed, Side.Home), RateLineup(away, revealed, Side.Away));
    }

    private static IReadOnlyList<RatedPlayer> RateLineup(Lineup? lineup, MatchEvent[] events, Side side)
    {
        if (lineup is null) return [];
        var list = new List<RatedPlayer>();
        foreach (var p in lineup.StartingXI)
        {
            int goals = events.Count(e => e.Side == side && e.PlayerName == p.Name && e.Type is MatchEventType.Goal or MatchEventType.PenaltyGoal);
            int assists = events.Count(e => e.Side == side && e.AssistName == p.Name);
            double rating = Math.Round(6.4 + goals * 1.1 + assists * 0.6 + ((p.Number ?? 10) % 5) * 0.12, 1);
            list.Add(new RatedPlayer(p.Name, p.Position, rating, goals, assists, p.Number));
        }
        return list.OrderByDescending(p => p.Rating).ToList();
    }

    public Task<MatchOdds?> GetOddsAsync(string fixtureId, CancellationToken ct) =>

        Task.FromResult<MatchOdds?>(new MatchOdds(2.10, 3.30, 3.60, "Demo Bookmaker", DateTimeOffset.UtcNow));

    public Task<Weather?> GetWeatherAsync(double latitude, double longitude, CancellationToken ct) =>
        Task.FromResult<Weather?>(new Weather(24.0, 11.0, 0.0, 55, "Clear"));

    public Task<(double Latitude, double Longitude)?> GeocodeAsync(string place, CancellationToken ct) =>

        Task.FromResult<(double, double)?>(null);
}
