using Triviela.Core;
using Triviela.Domain;
using Xunit;

namespace Triviela.Tests;

public class FormAnalyzerTests
{
    private static FormResult R(int gf, int ga, int daysAgo = 0) =>
        new(DateTimeOffset.UtcNow.AddDays(-daysAgo), "Opp", true, gf, ga, "League");

    [Fact]
    public void Empty_form_yields_no_insights()
    {
        Assert.Empty(FormAnalyzer.Analyze([]));
    }

    [Fact]
    public void Win_streak_is_reported()
    {
        var insights = FormAnalyzer.Analyze([R(2, 0), R(1, 0), R(3, 1), R(0, 2)]);
        Assert.Contains("Won 3 in a row", insights);
    }

    [Fact]
    public void Unbeaten_run_reported_when_no_current_win_streak_break()
    {
        var insights = FormAnalyzer.Analyze([R(1, 0), R(1, 1), R(2, 2), R(0, 1)]);
        Assert.Contains("Unbeaten in 3 matches", insights);
    }

    [Fact]
    public void Conceding_run_and_clean_sheets_counted()
    {
        var insights = FormAnalyzer.Analyze([R(1, 1), R(2, 1), R(0, 3), R(1, 0), R(2, 0)]);
        Assert.Contains("Conceded in 3 consecutive matches", insights);
        Assert.Contains(insights, i => i.Contains("clean sheet"));
    }

    [Fact]
    public void Wdl_summary_always_present()
    {
        var insights = FormAnalyzer.Analyze([R(1, 0), R(0, 1), R(1, 1)]);
        Assert.Contains("1W 1D 1L in last 3", insights);
    }
}
