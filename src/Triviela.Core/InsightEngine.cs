using Triviela.Domain;

namespace Triviela.Core;

public static class InsightEngine
{
    public static IReadOnlyList<string> Generate(MatchSnapshot s)
    {
        var f = s.Fixture;
        var home = f.Home.Name;
        var away = f.Away.Name;
        var lines = new List<string>();

        if (f.Score.Home == f.Score.Away)
            lines.Add(f.Score.Home == 0
                ? $"Still goalless between {home} and {away} after {f.Minute}'."
                : $"All square at {f.Score} between {home} and {away}.");
        else
        {
            var leader = f.Score.Home > f.Score.Away ? home : away;
            lines.Add($"{leader} lead {f.Score} after {f.Minute}'.");
        }

        var top = s.HomeRatings.Concat(s.AwayRatings).OrderByDescending(p => p.Rating).FirstOrDefault();
        if (top is not null)
            lines.Add($"{top.Name} is the highest-rated player on the pitch ({top.Rating:0.0}).");

        if (s.HomeStats?.PossessionPercent is { } hp && s.AwayStats?.PossessionPercent is { } ap)
        {
            if (Math.Abs(hp - ap) >= 12)
                lines.Add($"{(hp > ap ? home : away)} are controlling possession {Math.Max(hp, ap)}-{Math.Min(hp, ap)}.");
            else
                lines.Add($"A balanced possession battle, {hp}-{ap}.");
        }

        if (s.HomeStats?.Shots is { } hs && s.AwayStats?.Shots is { } as_)
        {
            var moreShots = hs > as_ ? home : away;
            var trailing = (hs > as_ ? f.Score.Home < f.Score.Away : f.Score.Away < f.Score.Home);
            if (hs != as_ && trailing)
                lines.Add($"{moreShots} have had more shots ({Math.Max(hs, as_)} v {Math.Min(hs, as_)}) but trail.");
        }

        if (s.HomeStats?.ExpectedGoals is { } hxg && s.AwayStats?.ExpectedGoals is { } axg && Math.Abs(hxg - axg) >= 0.4)
            lines.Add($"xG favours {(hxg > axg ? home : away)} ({hxg:0.0} v {axg:0.0}).");

        var reds = s.Events.Count(e => e.Type is MatchEventType.RedCard or MatchEventType.SecondYellow);
        if (reds > 0) lines.Add($"Down to 10 men — {reds} red card(s) shown.");
        var yellows = s.Events.Count(e => e.Type == MatchEventType.YellowCard);
        if (yellows >= 4) lines.Add($"A feisty game — {yellows} yellow cards already.");

        if (s.HeadToHead is { Played: > 0 } h)
        {
            if (h.AWins != h.BWins)
            {
                var (leadName, leadW, otherW) = h.AWins > h.BWins ? (h.TeamA, h.AWins, h.BWins) : (h.TeamB, h.BWins, h.AWins);
                lines.Add($"In {h.Played} meetings, {leadName} lead the head-to-head {leadW}-{otherW} (D{h.Draws}).");
            }
            else
                lines.Add($"Evenly matched historically: {h.AWins}-{h.BWins} across {h.Played} meetings.");

            if (h.Recent.Count > 0)
            {
                var last = h.Recent[0];
                lines.Add($"Last time out: {last.Home} {last.Score} {last.Away} ({last.Date:yyyy}).");
            }
            if (h.Biggest is { } big)
                lines.Add($"Biggest result between them: {big.Home} {big.Score} {big.Away}.");
        }

        return lines;
    }
}
