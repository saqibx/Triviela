using Triviela.Core;
using Xunit;

namespace Triviela.Tests;

public class RateLimitGovernorTests
{
    [Fact]
    public void Unregistered_provider_is_never_throttled()
    {
        var sut = new RateLimitGovernor();
        for (var i = 0; i < 1000; i++)
            Assert.True(sut.TryAcquire("demo"));
    }

    [Fact]
    public void Bucket_drains_to_empty_then_denies()
    {
        var sut = new RateLimitGovernor();
        sut.Configure("p", capacity: 3, refillPerSecond: 0);

        Assert.True(sut.TryAcquire("p"));
        Assert.True(sut.TryAcquire("p"));
        Assert.True(sut.TryAcquire("p"));
        Assert.False(sut.TryAcquire("p"));
    }

    [Fact]
    public void Remaining_reflects_spent_tokens()
    {
        var sut = new RateLimitGovernor();
        sut.Configure("p", capacity: 5, refillPerSecond: 0);

        sut.TryAcquire("p");
        sut.TryAcquire("p");

        Assert.Equal(3, sut.Remaining("p"));
    }

    [Fact]
    public void Remaining_is_negative_for_unknown_provider()
    {
        var sut = new RateLimitGovernor();
        Assert.Equal(-1, sut.Remaining("nope"));
    }
}
