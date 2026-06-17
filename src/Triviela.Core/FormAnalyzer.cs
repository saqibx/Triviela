using Triviela.Domain;

namespace Triviela.Core;

public static class FormAnalyzer
{
    public static IReadOnlyList<string> Analyze(IReadOnlyList<FormResult> recentFirst)
    {
        if (recentFirst.Count == 0) return [];
        var insights = new List<string>();
        int n = recentFirst.Count;

        int unbeaten = LeadingCount(recentFirst, r => r.Outcome != 'L');
        int winStreak = LeadingCount(recentFirst, r => r.Outcome == 'W');
        int lossStreak = LeadingCount(recentFirst, r => r.Outcome == 'L');
        int scoringRun = LeadingCount(recentFirst, r => r.GoalsFor > 0);
        int concedingRun = LeadingCount(recentFirst, r => r.GoalsAgainst > 0);

        if (winStreak >= 2) insights.Add($"Won {winStreak} in a row");
        else if (unbeaten >= 3) insights.Add($"Unbeaten in {unbeaten} matches");
        if (lossStreak >= 2) insights.Add($"Lost {lossStreak} straight");

        if (scoringRun >= 3) insights.Add($"Scored in {scoringRun} consecutive matches");
        if (concedingRun >= 3) insights.Add($"Conceded in {concedingRun} consecutive matches");

        int cleanSheets = recentFirst.Count(r => r.GoalsAgainst == 0);
        if (cleanSheets >= 2) insights.Add($"{cleanSheets} clean sheet(s) in last {n}");

        int failedToScore = recentFirst.Count(r => r.GoalsFor == 0);
        if (failedToScore >= 2) insights.Add($"Failed to score in {failedToScore} of last {n}");

        int w = recentFirst.Count(r => r.Outcome == 'W');
        int d = recentFirst.Count(r => r.Outcome == 'D');
        int l = recentFirst.Count(r => r.Outcome == 'L');
        insights.Add($"{w}W {d}D {l}L in last {n}");

        return insights;
    }

    private static int LeadingCount(IReadOnlyList<FormResult> list, Func<FormResult, bool> pred)
    {
        int c = 0;
        foreach (var r in list)
        {
            if (!pred(r)) break;
            c++;
        }
        return c;
    }
}
