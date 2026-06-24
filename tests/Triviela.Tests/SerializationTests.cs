using System.Text.Json;
using Triviela.Domain;

namespace Triviela.Tests;

public class SerializationTests
{
    private static MatchSnapshot FullyPopulated()
    {
        var home = new Team("br", "Brazil", "BRA", "Brazil", "https://logo/br.png");
        var away = new Team("ar", "Argentina", "ARG", "Argentina", "https://logo/ar.png");
        var fixture = new Fixture(
            "12345", home, away, new Score(2, 1), MatchStatus.Live, 67,
            new DateTimeOffset(2026, 6, 24, 19, 0, 0, TimeSpan.Zero),
            "FIFA World Cup", "Szymon Marciniak");

        var xi = new List<Player>
        {
            new("br1", "Alisson", "GK", 1, 33, "Brazil", "Liverpool", null, 25_000_000m),
            new("br7", "Vinícius Jr", "LW", 7),
        };
        var lineup = new Lineup("4-3-3", xi, [new Player("br19", "Endrick", "ST", 19)], "br1");

        return new MatchSnapshot
        {
            Fixture = fixture,
            Venue = new Venue("MetLife Stadium", "East Rutherford", "USA", 82500, 40.81, -74.07, "Grass"),
            HomeManager = new Manager("m1", "Dorival", "Brazil", 61, "4-3-3"),
            AwayManager = new Manager("m2", "Scaloni", "Argentina", 47, "4-4-2"),
            HomeLineup = lineup,
            AwayLineup = lineup,
            HomeStats = new TeamStatistics(54, 16, 6, 7, 9, 2, 87, 1.84),
            AwayStats = new TeamStatistics(46, 11, 4, 4, 12, 3, 81, 1.20),
            Events =
            [
                new MatchEvent(12, null, MatchEventType.Goal, Side.Home, "Vinícius Jr", "Raphinha"),
                new MatchEvent(41, null, MatchEventType.PenaltyGoal, Side.Away, "Lionel Messi", null, "Penalty"),
                new MatchEvent(74, null, MatchEventType.RedCard, Side.Away, "Cristian Romero", null, "Second yellow"),
            ],
            Weather = new Weather(24.0, 11.0, 0.0, 55, "Clear"),
            Narrative = new MatchNarrative("Brazil edge a classic", 30, -15, "We control it", "Backs to the wall"),
            Odds = new MatchOdds(2.10, 3.30, 3.60, "Demo Bookmaker", new DateTimeOffset(2026, 6, 24, 19, 5, 0, TimeSpan.Zero)),
            Facts = [new MatchFact("Brazil unbeaten in 10", "form")],
            HomeRatings = [new RatedPlayer("Vinícius Jr", "LW", 8.4, 1, 0, 7)],
            AwayRatings = [new RatedPlayer("Lionel Messi", "ST", 7.9, 1, 0, 10)],
            HeadToHead = new HeadToHead("Brazil", "Argentina", 5, 2, 1, 2, 7, 6,
                new Meeting(new DateTimeOffset(2021, 7, 10, 0, 0, 0, TimeSpan.Zero), "Brazil", "Argentina", 0, 1, "Copa America"),
                [new Meeting(new DateTimeOffset(2023, 11, 21, 0, 0, 0, TimeSpan.Zero), "Argentina", "Brazil", 1, 0, "WCQ")]),
            Social = [new SocialPost("What a goal!", "r/soccer", new DateTimeOffset(2026, 6, 24, 19, 12, 0, TimeSpan.Zero), "https://reddit/x", 42)],
            Intel = [new MatchIntelItem("Messi has scored in his last 3 finals", "Lionel Messi", "https://news/x", new DateTimeOffset(2026, 6, 24, 19, 10, 0, TimeSpan.Zero))],
            UpdatedAtUtc = new DateTimeOffset(2026, 6, 24, 19, 12, 30, TimeSpan.Zero),
        };
    }

    [Fact]
    public void MatchSnapshot_round_trips_stably()
    {
        var snap = FullyPopulated();

        var json1 = JsonSerializer.Serialize(snap, TrivielaJson.Options);
        var back = JsonSerializer.Deserialize<MatchSnapshot>(json1, TrivielaJson.Options)!;
        var json2 = JsonSerializer.Serialize(back, TrivielaJson.Options);

        Assert.Equal(json1, json2);
    }

    [Fact]
    public void MatchSnapshot_preserves_field_values_and_enum_strings()
    {
        var snap = FullyPopulated();
        var json = JsonSerializer.Serialize(snap, TrivielaJson.Options);
        var back = JsonSerializer.Deserialize<MatchSnapshot>(json, TrivielaJson.Options)!;

        // enums written as readable strings, not ordinals
        Assert.Contains("\"status\":\"Live\"", json);
        Assert.Contains("\"type\":\"PenaltyGoal\"", json);
        Assert.Contains("\"side\":\"Away\"", json);
        Assert.DoesNotContain("\"status\":1", json);

        // nested values survive
        Assert.Equal(2, back.Fixture.Score.Home);
        Assert.Equal(MatchStatus.Live, back.Fixture.Status);
        Assert.Equal("Szymon Marciniak", back.Fixture.Referee);
        Assert.Equal(3, back.Events.Count);
        Assert.Equal(MatchEventType.RedCard, back.Events[2].Type);
        Assert.Equal(1.84, back.HomeStats!.ExpectedGoals);
        Assert.Equal(8.4, back.HomeRatings[0].Rating);
        Assert.Equal("Argentina", back.HeadToHead!.TeamB);
        Assert.Single(back.Intel);
        Assert.Equal(82500, back.Venue!.Capacity);
    }

    [Fact]
    public void Fixture_list_round_trips()
    {
        var list = new List<Fixture>
        {
            new("1", new Team("a", "A", "AAA"), new Team("b", "B", "BBB"), new Score(0, 0), MatchStatus.Scheduled, 0, DateTimeOffset.UnixEpoch),
            new("2", new Team("c", "C", "CCC"), new Team("d", "D", "DDD"), new Score(3, 2), MatchStatus.Finished, 90, DateTimeOffset.UnixEpoch, "Friendly", null),
        };
        var json = JsonSerializer.Serialize(list, TrivielaJson.Options);
        var back = JsonSerializer.Deserialize<List<Fixture>>(json, TrivielaJson.Options)!;
        Assert.Equal(2, back.Count);
        Assert.Equal(MatchStatus.Finished, back[1].Status);
        Assert.Equal("Friendly", back[1].Competition);
    }
}
