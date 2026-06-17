using Triviela.Domain;
using Triviela.Providers;
using Xunit;

namespace Triviela.Tests;

public class DemoDataSourceTests
{
    private readonly DemoDataSource _sut = new();

    [Fact]
    public async Task GetLiveFixtures_returns_one_ticking_match()
    {
        var fixtures = await _sut.GetLiveFixturesAsync(CancellationToken.None);

        var f = Assert.Single(fixtures);
        Assert.Equal("demo-bra-arg", f.Id);
        Assert.Equal("Brazil", f.Home.Name);
        Assert.Equal("Argentina", f.Away.Name);
        Assert.InRange(f.Minute, 0, 95);
    }

    [Fact]
    public async Task Score_is_consistent_with_revealed_goal_events()
    {
        var fixture = (await _sut.GetLiveFixturesAsync(CancellationToken.None)).Single();
        var events = await _sut.GetEventsAsync(fixture.Id, null, CancellationToken.None);

        var expectedHome = events.Count(e => e.Side == Side.Home &&
            e.Type is MatchEventType.Goal or MatchEventType.PenaltyGoal);
        var expectedAway = events.Count(e => e.Side == Side.Away &&
            e.Type is MatchEventType.Goal or MatchEventType.PenaltyGoal);

        Assert.Equal(expectedHome, fixture.Score.Home);
        Assert.Equal(expectedAway, fixture.Score.Away);
    }

    [Fact]
    public async Task Lineups_have_eleven_starters_each()
    {
        var (home, away) = await _sut.GetLineupsAsync("demo-bra-arg", CancellationToken.None);

        Assert.NotNull(home);
        Assert.NotNull(away);
        Assert.Equal(11, home!.StartingXI.Count);
        Assert.Equal(11, away!.StartingXI.Count);
    }

    [Fact]
    public async Task Events_only_reveal_at_or_before_current_minute()
    {
        var fixture = (await _sut.GetLiveFixturesAsync(CancellationToken.None)).Single();
        var events = await _sut.GetEventsAsync(fixture.Id, null, CancellationToken.None);

        Assert.All(events, e => Assert.True(e.Minute <= fixture.Minute));
    }
}
