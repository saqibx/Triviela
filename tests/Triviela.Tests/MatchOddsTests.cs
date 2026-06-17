using Triviela.Domain;
using Triviela.Providers;
using Xunit;

namespace Triviela.Tests;

public class MatchOddsTests
{
    [Fact]
    public void Implied_probabilities_are_the_reciprocal_of_decimal_odds()
    {
        var odds = new MatchOdds(2.00, 4.00, 4.00);

        Assert.Equal(50, odds.HomeImplied);
        Assert.Equal(25, odds.DrawImplied);
        Assert.Equal(25, odds.AwayImplied);
    }

    [Fact]
    public void Implied_probability_is_null_for_missing_or_invalid_prices()
    {
        var odds = new MatchOdds(null, 0, -1);

        Assert.Null(odds.HomeImplied);
        Assert.Null(odds.DrawImplied);
        Assert.Null(odds.AwayImplied);
    }

    [Fact]
    public async Task Demo_source_offers_a_match_winner_market()
    {
        var sut = new DemoDataSource();

        var odds = await sut.GetOddsAsync("demo-bra-arg", CancellationToken.None);

        Assert.NotNull(odds);
        Assert.NotNull(odds!.HomeWin);
        Assert.NotNull(odds.Draw);
        Assert.NotNull(odds.AwayWin);

        Assert.True(odds.HomeWin < odds.AwayWin);
    }
}
