using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Triviela.Domain;

namespace Triviela.Providers;

/// <summary>
/// Keyless data source backed by ESPN's public (undocumented) site API. Covers the core live
/// loop — fixtures, score/clock/referee/venue, events, lineups, team stats — but NOT xG,
/// per-player ratings, or odds (genuinely absent from this endpoint; those return empty/null).
/// Parsing is deliberately defensive (per-element try/catch, TryGetProperty everywhere) so ESPN
/// schema drift degrades to empty data rather than throwing — the same house style as
/// <see cref="ApiFootballDataSource"/>.
/// </summary>
public sealed class EspnFootballDataSource(
    HttpClient http,
    IOptions<EspnOptions> options,
    ILogger<EspnFootballDataSource> logger) : IFootballDataSource
{
    private readonly EspnOptions _opts = options.Value;

    // One match summary is sliced for venue/lineups/stats/events; memo it briefly so the poller's
    // separate slice calls for a single fixture collapse to one HTTP round-trip per tick.
    private static readonly TimeSpan SummaryTtl = TimeSpan.FromSeconds(5);
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, JsonElement Root)> _summaryCache = new();

    public string Name => "espn";

    private async Task<JsonElement?> GetJsonAsync(string path, CancellationToken ct)
    {
        if (!_opts.IsConfigured) return null;
        try
        {
            using var resp = await http.GetAsync(path, ct);
            if (!resp.IsSuccessStatusCode)
            {
                logger.LogWarning("ESPN {Path} returned {Status}", path, (int)resp.StatusCode);
                return null;
            }
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return doc.RootElement.Clone();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "ESPN {Path} failed", path);
            return null;
        }
    }

    private async Task<JsonElement?> GetSummaryAsync(string fixtureId, CancellationToken ct)
    {
        if (_summaryCache.TryGetValue(fixtureId, out var cached) && DateTimeOffset.UtcNow - cached.At < SummaryTtl)
            return cached.Root;

        // ESPN summary is not league-scoped by path content, but the route requires a league slug.
        // Try the configured leagues until one returns a body for this event id.
        foreach (var league in _opts.Leagues)
        {
            var root = await GetJsonAsync($"apis/site/v2/sports/soccer/{league}/summary?event={Uri.EscapeDataString(fixtureId)}", ct);
            if (root is { } r && r.ValueKind == JsonValueKind.Object && (r.TryGetProperty("rosters", out _) || r.TryGetProperty("boxscore", out _) || r.TryGetProperty("gameInfo", out _)))
            {
                _summaryCache[fixtureId] = (DateTimeOffset.UtcNow, r);
                return r;
            }
        }
        return null;
    }

    public async Task<IReadOnlyList<Fixture>> GetLiveFixturesAsync(CancellationToken ct)
    {
        var seen = new Dictionary<string, Fixture>();
        foreach (var league in _opts.Leagues)
        {
            var root = await GetJsonAsync($"apis/site/v2/sports/soccer/{league}/scoreboard", ct);
            if (root is not { } r || r.ValueKind != JsonValueKind.Object) continue;

            var leagueName = r.TryGetProperty("leagues", out var lgs) && lgs.ValueKind == JsonValueKind.Array && lgs.GetArrayLength() > 0
                && lgs[0].TryGetProperty("name", out var ln) ? ln.GetString() : league;

            if (!r.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array) continue;
            foreach (var ev in events.EnumerateArray())
            {
                try
                {
                    if (ParseScoreboardEvent(ev, leagueName) is { } fx && IsLiveStatus(fx.Status))
                        seen[fx.Id] = fx;
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Skipping unparseable ESPN scoreboard event");
                }
            }
        }
        return seen.Values.ToList();
    }

    public async Task<Fixture?> GetFixtureAsync(string fixtureId, CancellationToken ct)
    {
        var summary = await GetSummaryAsync(fixtureId, ct);
        if (summary is { } s && s.TryGetProperty("header", out var header) && header.TryGetProperty("competitions", out var comps)
            && comps.ValueKind == JsonValueKind.Array && comps.GetArrayLength() > 0)
        {
            var leagueName = s.TryGetProperty("header", out var h) && h.TryGetProperty("league", out var lg) && lg.TryGetProperty("name", out var ln) ? ln.GetString() : null;
            try { return ParseCompetition(fixtureId, comps[0], leagueName); }
            catch (Exception ex) { logger.LogDebug(ex, "ESPN summary header parse failed for {Id}", fixtureId); }
        }

        // Fallback: find it in the live scoreboards.
        var live = await GetLiveFixturesAsync(ct);
        return live.FirstOrDefault(f => f.Id == fixtureId);
    }

    public async Task<IReadOnlyList<MatchEvent>> GetEventsAsync(string fixtureId, string? homeTeamId, CancellationToken ct)
    {
        var summary = await GetSummaryAsync(fixtureId, ct);
        if (summary is not { } s) return [];
        if (!s.TryGetProperty("keyEvents", out var keyEvents) || keyEvents.ValueKind != JsonValueKind.Array)
            return [];

        var events = new List<MatchEvent>();
        foreach (var e in keyEvents.EnumerateArray())
        {
            try
            {
                int minute = 0;
                if (e.TryGetProperty("clock", out var clock) && clock.TryGetProperty("displayValue", out var dv))
                    minute = ParseClockMinute(dv.GetString());

                var teamId = e.TryGetProperty("team", out var tm) && tm.TryGetProperty("id", out var tid) ? tid.GetString() : null;
                var side = homeTeamId is not null && teamId == homeTeamId ? Side.Home : Side.Away;

                var (player, assist) = ParseParticipants(e);
                var text = e.TryGetProperty("text", out var tx) ? tx.GetString() : null;
                var typeText = e.TryGetProperty("type", out var ty) && ty.TryGetProperty("text", out var tt) ? tt.GetString() : null;

                var type = MapEventType(e, typeText, text);
                // Skip non-actionable markers (Kickoff, period start/end) — they have no player and
                // would render as an empty "0' • —" row.
                if (type == MatchEventType.Other && string.IsNullOrEmpty(player)) continue;

                events.Add(new MatchEvent(minute, null, type, side, player, assist, text));
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Skipping malformed ESPN key event for {Id}", fixtureId);
            }
        }
        return events;
    }

    public async Task<(Lineup? Home, Lineup? Away)> GetLineupsAsync(string fixtureId, CancellationToken ct)
    {
        var summary = await GetSummaryAsync(fixtureId, ct);
        if (summary is not { } s || !s.TryGetProperty("rosters", out var rosters) || rosters.ValueKind != JsonValueKind.Array)
            return (null, null);

        Lineup? home = null, away = null;
        foreach (var team in rosters.EnumerateArray())
        {
            var homeAway = team.TryGetProperty("homeAway", out var ha) ? ha.GetString() : null;
            var lineup = ParseRoster(team);
            if (homeAway == "home") home = lineup;
            else if (homeAway == "away") away = lineup;
        }
        return (home, away);
    }

    public async Task<(TeamStatistics? Home, TeamStatistics? Away)> GetStatisticsAsync(string fixtureId, CancellationToken ct)
    {
        var summary = await GetSummaryAsync(fixtureId, ct);
        if (summary is not { } s || !s.TryGetProperty("boxscore", out var box) || !box.TryGetProperty("teams", out var teams) || teams.ValueKind != JsonValueKind.Array)
            return (null, null);

        TeamStatistics? home = null, away = null;
        foreach (var team in teams.EnumerateArray())
        {
            var homeAway = team.TryGetProperty("team", out var tm) && tm.TryGetProperty("homeAway", out var ha) ? ha.GetString()
                : team.TryGetProperty("homeAway", out var ha2) ? ha2.GetString() : null;
            var stats = ParseTeamStats(team);
            if (homeAway == "home") home = stats;
            else if (homeAway == "away") away = stats;
        }
        // ESPN boxscore order is home, away when homeAway is absent.
        if (home is null && away is null)
        {
            var arr = teams.EnumerateArray().Select(ParseTeamStats).ToArray();
            return (arr.ElementAtOrDefault(0), arr.ElementAtOrDefault(1));
        }
        return (home, away);
    }

    public async Task<Venue?> GetVenueAsync(string fixtureId, CancellationToken ct)
    {
        var summary = await GetSummaryAsync(fixtureId, ct);
        if (summary is not { } s) return null;

        JsonElement venue = default;
        bool found = s.TryGetProperty("gameInfo", out var gi) && gi.TryGetProperty("venue", out venue);
        if (!found) return null;

        var name = venue.TryGetProperty("fullName", out var fn) ? fn.GetString() : null;
        if (string.IsNullOrWhiteSpace(name)) return null;

        string? city = null, country = null;
        if (venue.TryGetProperty("address", out var addr))
        {
            city = addr.TryGetProperty("city", out var c) ? c.GetString() : null;
            country = addr.TryGetProperty("country", out var co) ? co.GetString() : null;
        }
        int? capacity = venue.TryGetProperty("capacity", out var cap) && cap.ValueKind == JsonValueKind.Number ? cap.GetInt32() : null;
        var grass = venue.TryGetProperty("grass", out var g) && g.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? (g.GetBoolean() ? "Grass" : null) : null;

        return new Venue(name!, city, country, capacity, null, null, grass);
    }

    // ESPN does not expose per-player match ratings or pre-match 1X2 odds on these endpoints.
    public Task<(IReadOnlyList<RatedPlayer> Home, IReadOnlyList<RatedPlayer> Away)> GetPlayerRatingsAsync(string fixtureId, CancellationToken ct) =>
        Task.FromResult<(IReadOnlyList<RatedPlayer>, IReadOnlyList<RatedPlayer>)>(([], []));

    public Task<MatchOdds?> GetOddsAsync(string fixtureId, CancellationToken ct) =>
        Task.FromResult<MatchOdds?>(null);

    // ---- parsing helpers ----

    private static Fixture? ParseScoreboardEvent(JsonElement ev, string? leagueName)
    {
        var id = ev.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        if (id is null) return null;
        if (!ev.TryGetProperty("competitions", out var comps) || comps.ValueKind != JsonValueKind.Array || comps.GetArrayLength() == 0)
            return null;
        return ParseCompetition(id, comps[0], leagueName);
    }

    private static Fixture ParseCompetition(string id, JsonElement comp, string? leagueName)
    {
        Team home = new("?", "?", "?"), away = new("?", "?", "?");
        int homeGoals = 0, awayGoals = 0;
        if (comp.TryGetProperty("competitors", out var competitors) && competitors.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in competitors.EnumerateArray())
            {
                var homeAway = c.TryGetProperty("homeAway", out var ha) ? ha.GetString() : null;
                var team = ParseTeam(c);
                var score = c.TryGetProperty("score", out var sc) ? ParseIntLoose(sc) ?? 0 : 0;
                if (homeAway == "home") { home = team; homeGoals = score; }
                else { away = team; awayGoals = score; }
            }
        }

        var status = comp.TryGetProperty("status", out var st) ? st : default;
        var (matchStatus, minute) = ParseStatus(status);

        var kickoff = comp.TryGetProperty("date", out var dt) && dt.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(dt.GetString(), out var k)
            ? k : DateTimeOffset.UtcNow;

        string? referee = null;
        if (comp.TryGetProperty("officials", out var officials) && officials.ValueKind == JsonValueKind.Array && officials.GetArrayLength() > 0)
            referee = officials[0].TryGetProperty("displayName", out var rn) ? rn.GetString() : null;

        return new Fixture(id, home, away, new Score(homeGoals, awayGoals), matchStatus, minute, kickoff, leagueName, referee);
    }

    private static Team ParseTeam(JsonElement competitor)
    {
        if (!competitor.TryGetProperty("team", out var t)) return new Team("?", "?", "?");
        var id = t.TryGetProperty("id", out var i) ? i.GetString() ?? "?" : "?";
        var name = t.TryGetProperty("displayName", out var n) ? n.GetString() ?? "?"
            : t.TryGetProperty("name", out var n2) ? n2.GetString() ?? "?" : "?";
        var shortName = t.TryGetProperty("abbreviation", out var ab) ? ab.GetString() ?? Short(name) : Short(name);
        var logo = t.TryGetProperty("logo", out var l) ? l.GetString() : null;
        return new Team(id, name, shortName!, name, logo);
    }

    private static string Short(string name) => name.Length >= 3 ? name[..3].ToUpperInvariant() : name.ToUpperInvariant();

    private static Lineup ParseRoster(JsonElement team)
    {
        var formation = team.TryGetProperty("formation", out var f) ? f.GetString() : null;
        var xi = new List<Player>();
        var bench = new List<Player>();
        if (team.TryGetProperty("roster", out var roster) && roster.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in roster.EnumerateArray())
            {
                if (!entry.TryGetProperty("athlete", out var ath)) continue;
                var id = ath.TryGetProperty("id", out var pid) ? pid.GetString() ?? Guid.NewGuid().ToString() : Guid.NewGuid().ToString();
                var name = ath.TryGetProperty("displayName", out var pn) ? pn.GetString() ?? "?" : "?";
                string? pos = null;
                if (ath.TryGetProperty("position", out var po))
                    pos = po.TryGetProperty("abbreviation", out var pa) ? pa.GetString() : po.TryGetProperty("name", out var pnm) ? pnm.GetString() : null;
                int? number = entry.TryGetProperty("jersey", out var je) ? ParseIntLoose(je) : null;
                var starter = entry.TryGetProperty("starter", out var s) && s.ValueKind == JsonValueKind.True;
                var player = new Player(id, name, pos, number);
                (starter ? xi : bench).Add(player);
            }
        }
        return new Lineup(formation, xi, bench);
    }

    private static TeamStatistics ParseTeamStats(JsonElement team)
    {
        // Note: ESPN mixes scales — possessionPct is 0–100, but the *Pct completion stats
        // (passPct etc.) are 0–1 fractions. So derive pass accuracy from the raw counts when present.
        int? possession = null, shots = null, sot = null, corners = null, fouls = null, offsides = null;
        int? accuratePasses = null, totalPasses = null;
        double? passFraction = null;
        if (team.TryGetProperty("statistics", out var stats) && stats.ValueKind == JsonValueKind.Array)
        {
            foreach (var stat in stats.EnumerateArray())
            {
                var name = stat.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (!stat.TryGetProperty("displayValue", out var dv)) continue;
                switch (name)
                {
                    case "possessionPct": possession = ParsePercentLoose(dv); break;
                    case "totalShots": shots = ParseIntLoose(dv); break;
                    case "shotsOnTarget": sot = ParseIntLoose(dv); break;
                    case "wonCorners": corners = ParseIntLoose(dv); break;
                    case "foulsCommitted": fouls = ParseIntLoose(dv); break;
                    case "offsides": offsides = ParseIntLoose(dv); break;
                    case "accuratePasses": accuratePasses = ParseIntLoose(dv); break;
                    case "totalPasses": totalPasses = ParseIntLoose(dv); break;
                    case "passPct": case "accuratePassesPct": passFraction = ParseDoubleLoose(dv); break;
                }
            }
        }

        int? passAcc = totalPasses is > 0 && accuratePasses is not null
            ? (int)Math.Round(100.0 * accuratePasses.Value / totalPasses.Value)
            : passFraction is { } pf
                ? (int)Math.Round(pf <= 1.5 ? pf * 100 : pf)   // 0–1 fraction → %, else already %
                : null;

        return new TeamStatistics(possession, shots, sot, corners, fouls, offsides, passAcc, null);
    }

    private static (string? Player, string? Assist) ParseParticipants(JsonElement e)
    {
        if (!e.TryGetProperty("participants", out var parts) || parts.ValueKind != JsonValueKind.Array)
            return (null, null);
        string? player = null, assist = null;
        var i = 0;
        foreach (var p in parts.EnumerateArray())
        {
            var name = p.TryGetProperty("athlete", out var ath) && ath.TryGetProperty("displayName", out var dn) ? dn.GetString() : null;
            if (i == 0) player = name;
            else if (i == 1) assist = name;
            i++;
        }
        return (player, assist);
    }

    private static (MatchStatus, int) ParseStatus(JsonElement status)
    {
        if (status.ValueKind != JsonValueKind.Object) return (MatchStatus.Unknown, 0);
        var state = status.TryGetProperty("type", out var type) && type.TryGetProperty("state", out var st) ? st.GetString() : null;
        var desc = type.ValueKind == JsonValueKind.Object && type.TryGetProperty("description", out var d) ? d.GetString() : null;

        int minute = 0;
        if (status.TryGetProperty("displayClock", out var dc))
            minute = ParseClockMinute(dc.GetString());
        if (minute == 0 && status.TryGetProperty("clock", out var c) && c.ValueKind == JsonValueKind.Number)
            minute = (int)(c.GetDouble() / 60.0);

        var matchStatus = state switch
        {
            "pre" => MatchStatus.Scheduled,
            "post" => MatchStatus.Finished,
            "in" when desc is not null && desc.Contains("Half", StringComparison.OrdinalIgnoreCase) && desc.Contains("Time", StringComparison.OrdinalIgnoreCase) => MatchStatus.HalfTime,
            "in" => MatchStatus.Live,
            _ => MatchStatus.Unknown
        };
        return (matchStatus, minute);
    }

    private static bool IsLiveStatus(MatchStatus s) => s is MatchStatus.Live or MatchStatus.HalfTime;

    private static MatchEventType MapEventType(JsonElement e, string? typeText, string? text)
    {
        bool Flag(string prop) => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.True;
        var scoring = e.TryGetProperty("scoringPlay", out var sp) && sp.ValueKind == JsonValueKind.True;

        if (Flag("ownGoal")) return MatchEventType.OwnGoal;
        if (scoring && Flag("penaltyKick")) return MatchEventType.PenaltyGoal;
        if (scoring) return MatchEventType.Goal;
        if (Flag("redCard")) return MatchEventType.RedCard;
        if (Flag("yellowCard")) return MatchEventType.YellowCard;

        var hay = $"{typeText} {text}".ToLowerInvariant();
        if (hay.Contains("substitution")) return MatchEventType.Substitution;
        if (hay.Contains("penalty") && hay.Contains("miss")) return MatchEventType.MissedPenalty;
        if (hay.Contains("var")) return MatchEventType.Var;
        if (hay.Contains("yellow")) return hay.Contains("second") ? MatchEventType.SecondYellow : MatchEventType.YellowCard;
        if (hay.Contains("red")) return MatchEventType.RedCard;
        if (hay.Contains("goal")) return MatchEventType.Goal;
        return MatchEventType.Other;
    }

    private static int ParseClockMinute(string? clock)
    {
        if (string.IsNullOrWhiteSpace(clock)) return 0;
        var digits = new string(clock.TakeWhile(ch => char.IsDigit(ch)).ToArray());
        return int.TryParse(digits, out var m) ? m : 0;
    }

    private static int? ParseIntLoose(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Number => v.GetInt32(),
        JsonValueKind.String when int.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var n) => n,
        JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) => (int)Math.Round(d),
        _ => null
    };

    private static double? ParseDoubleLoose(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Number => v.GetDouble(),
        JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) => d,
        _ => null
    };

    private static int? ParsePercentLoose(JsonElement v)
    {
        if (v.ValueKind == JsonValueKind.String)
        {
            var s = v.GetString()?.TrimEnd('%');
            if (double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d)) return (int)Math.Round(d);
        }
        return ParseIntLoose(v);
    }
}
