using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Triviela.Domain;

namespace Triviela.Providers;

public sealed class ApiFootballDataSource(
    HttpClient http,
    IOptions<ApiFootballOptions> options,
    ILogger<ApiFootballDataSource> logger) : IFootballDataSource
{
    private readonly ApiFootballOptions _opts = options.Value;

    public string Name => "api-football";

    private bool Enabled => _opts.IsConfigured;

    private async Task<JsonElement?> GetResponseAsync(string path, CancellationToken ct)
    {
        if (!Enabled) return null;
        try
        {
            using var resp = await http.GetAsync(path, ct);
            if (!resp.IsSuccessStatusCode)
            {
                logger.LogWarning("API-Football {Path} returned {Status}", path, (int)resp.StatusCode);
                return null;
            }
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            return doc.RootElement.TryGetProperty("response", out var r) ? r.Clone() : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "API-Football {Path} failed", path);
            return null;
        }
    }

    public async Task<IReadOnlyList<Fixture>> GetLiveFixturesAsync(CancellationToken ct)
    {
        var response = await GetResponseAsync("fixtures?live=all", ct);
        if (response is not { ValueKind: JsonValueKind.Array }) return [];

        var list = new List<Fixture>();
        foreach (var el in response.Value.EnumerateArray())
        {
            try
            {
                if (ParseFixture(el) is { } fx) list.Add(fx);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Skipping unparseable fixture in live list");
            }
        }
        return list;
    }

    public async Task<Fixture?> GetFixtureAsync(string fixtureId, CancellationToken ct)
    {
        var response = await GetResponseAsync($"fixtures?id={Uri.EscapeDataString(fixtureId)}", ct);
        if (response is not { ValueKind: JsonValueKind.Array }) return null;
        return response.Value.EnumerateArray().Select(ParseFixture).OfType<Fixture>().FirstOrDefault();
    }

    public async Task<IReadOnlyList<MatchEvent>> GetEventsAsync(string fixtureId, string? homeTeamId, CancellationToken ct)
    {
        var response = await GetResponseAsync($"fixtures/events?fixture={Uri.EscapeDataString(fixtureId)}", ct);
        if (response is not { ValueKind: JsonValueKind.Array }) return [];

        var homeId = homeTeamId ?? (await GetFixtureRawAsync(fixtureId, ct))?.HomeId;

        var events = new List<MatchEvent>();
        foreach (var e in response.Value.EnumerateArray())
        {
            try
            {
                var minute = e.GetProperty("time").TryGetProperty("elapsed", out var el) && el.ValueKind == JsonValueKind.Number ? el.GetInt32() : 0;
                int? extra = e.GetProperty("time").TryGetProperty("extra", out var ex) && ex.ValueKind == JsonValueKind.Number ? ex.GetInt32() : null;
                var teamId = e.GetProperty("team").TryGetProperty("id", out var tid) ? tid.GetRawText() : null;

                var side = homeId is not null && teamId == homeId ? Side.Home : Side.Away;
                if (homeId is null) side = Side.Home;
                var type = (e.TryGetProperty("type", out var t) ? t.GetString() : null) ?? "";
                var detail = e.TryGetProperty("detail", out var d) ? d.GetString() : null;
                var player = e.TryGetProperty("player", out var p) && p.TryGetProperty("name", out var pn) ? pn.GetString() : null;
                var assist = e.TryGetProperty("assist", out var a) && a.TryGetProperty("name", out var an) ? an.GetString() : null;
                events.Add(new MatchEvent(minute, extra, MapEventType(type, detail), side, player, assist, detail));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Skipping malformed event for fixture {Fixture}", fixtureId);
            }
        }
        return events;
    }

    public async Task<(Lineup? Home, Lineup? Away)> GetLineupsAsync(string fixtureId, CancellationToken ct)
    {
        var response = await GetResponseAsync($"fixtures/lineups?fixture={Uri.EscapeDataString(fixtureId)}", ct);
        if (response is not { ValueKind: JsonValueKind.Array }) return (null, null);
        var teams = response.Value.EnumerateArray().Select(ParseLineup).ToArray();
        return (teams.ElementAtOrDefault(0), teams.ElementAtOrDefault(1));
    }

    public async Task<(TeamStatistics? Home, TeamStatistics? Away)> GetStatisticsAsync(string fixtureId, CancellationToken ct)
    {
        var response = await GetResponseAsync($"fixtures/statistics?fixture={Uri.EscapeDataString(fixtureId)}", ct);
        if (response is not { ValueKind: JsonValueKind.Array }) return (null, null);
        var teams = response.Value.EnumerateArray().Select(ParseStatistics).ToArray();
        return (teams.ElementAtOrDefault(0), teams.ElementAtOrDefault(1));
    }

    public async Task<(IReadOnlyList<RatedPlayer> Home, IReadOnlyList<RatedPlayer> Away)> GetPlayerRatingsAsync(string fixtureId, CancellationToken ct)
    {
        var response = await GetResponseAsync($"fixtures/players?fixture={Uri.EscapeDataString(fixtureId)}", ct);
        if (response is not { ValueKind: JsonValueKind.Array }) return ([], []);

        var teams = response.Value.EnumerateArray().Select(ParseTeamRatings).ToArray();
        return (teams.ElementAtOrDefault(0) ?? [], teams.ElementAtOrDefault(1) ?? []);
    }

    private static IReadOnlyList<RatedPlayer> ParseTeamRatings(JsonElement teamEl)
    {
        if (!teamEl.TryGetProperty("players", out var players) || players.ValueKind != JsonValueKind.Array) return [];
        var list = new List<RatedPlayer>();
        foreach (var p in players.EnumerateArray())
        {
            try
            {
                var name = p.GetProperty("player").GetProperty("name").GetString() ?? "?";
                var stat = p.TryGetProperty("statistics", out var st) && st.ValueKind == JsonValueKind.Array && st.GetArrayLength() > 0
                    ? st[0] : default;
                if (stat.ValueKind != JsonValueKind.Object) continue;
                var games = stat.TryGetProperty("games", out var g) ? g : default;
                var ratingStr = games.ValueKind == JsonValueKind.Object && games.TryGetProperty("rating", out var r) ? r.GetString() : null;
                if (ratingStr is null || !double.TryParse(ratingStr, System.Globalization.CultureInfo.InvariantCulture, out var rating)) continue;
                var pos = games.TryGetProperty("position", out var po) ? po.GetString() : null;
                int? num = games.TryGetProperty("number", out var nu) && nu.ValueKind == JsonValueKind.Number ? nu.GetInt32() : null;
                int goals = stat.TryGetProperty("goals", out var go) && go.TryGetProperty("total", out var gt) && gt.ValueKind == JsonValueKind.Number ? gt.GetInt32() : 0;
                int assists = go.ValueKind == JsonValueKind.Object && go.TryGetProperty("assists", out var asn) && asn.ValueKind == JsonValueKind.Number ? asn.GetInt32() : 0;
                list.Add(new RatedPlayer(name, pos, rating, goals, assists, num));
            }
            catch { }
        }
        return list.OrderByDescending(p => p.Rating).ToList();
    }

    public async Task<Venue?> GetVenueAsync(string fixtureId, CancellationToken ct)
    {
        var response = await GetResponseAsync($"fixtures?id={Uri.EscapeDataString(fixtureId)}", ct);
        if (response is not { ValueKind: JsonValueKind.Array }) return null;
        var first = response.Value.EnumerateArray().FirstOrDefault();
        if (first.ValueKind != JsonValueKind.Object) return null;
        if (!first.TryGetProperty("fixture", out var fx) || !fx.TryGetProperty("venue", out var v)) return null;
        var name = v.TryGetProperty("name", out var n) ? n.GetString() : null;
        if (string.IsNullOrWhiteSpace(name)) return null;
        var city = v.TryGetProperty("city", out var c) ? c.GetString() : null;
        var venueId = v.TryGetProperty("id", out var vid) && vid.ValueKind == JsonValueKind.Number ? vid.GetRawText() : null;

        if (venueId is not null && await GetResponseAsync($"venues?id={venueId}", ct) is { ValueKind: JsonValueKind.Array } vr)
        {
            var vd = vr.EnumerateArray().FirstOrDefault();
            if (vd.ValueKind == JsonValueKind.Object)
            {
                var country = vd.TryGetProperty("country", out var co) ? co.GetString() : null;
                var surface = vd.TryGetProperty("surface", out var su) ? su.GetString() : null;
                int? capacity = vd.TryGetProperty("capacity", out var cap) && cap.ValueKind == JsonValueKind.Number ? cap.GetInt32() : null;
                return new Venue(name!, city, country, capacity, null, null, surface);
            }
        }
        return new Venue(name!, city);
    }

    public async Task<MatchOdds?> GetOddsAsync(string fixtureId, CancellationToken ct)
    {
        var response = await GetResponseAsync($"odds?fixture={Uri.EscapeDataString(fixtureId)}&bet=1", ct);
        if (response is not { ValueKind: JsonValueKind.Array }) return null;

        foreach (var entry in response.Value.EnumerateArray())
        {
            if (!entry.TryGetProperty("bookmakers", out var bookmakers) || bookmakers.ValueKind != JsonValueKind.Array) continue;
            foreach (var bk in bookmakers.EnumerateArray())
            {
                if (!bk.TryGetProperty("bets", out var bets) || bets.ValueKind != JsonValueKind.Array) continue;
                foreach (var bet in bets.EnumerateArray())
                {
                    if (!bet.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Array) continue;
                    double? home = null, draw = null, away = null;
                    foreach (var val in values.EnumerateArray())
                    {
                        var label = val.TryGetProperty("value", out var lv) ? lv.GetString() : null;
                        var odd = ParseDouble(val.TryGetProperty("odd", out var ov) ? ov : default);
                        switch (label)
                        {
                            case "Home": home = odd; break;
                            case "Draw": draw = odd; break;
                            case "Away": away = odd; break;
                        }
                    }
                    if (home is not null || draw is not null || away is not null)
                    {
                        var name = bk.TryGetProperty("name", out var bn) ? bn.GetString() : null;
                        return new MatchOdds(home, draw, away, name, DateTimeOffset.UtcNow);
                    }
                }
            }
        }
        return null;
    }

    private record RawFixture(string HomeId);

    private async Task<RawFixture?> GetFixtureRawAsync(string fixtureId, CancellationToken ct)
    {
        var response = await GetResponseAsync($"fixtures?id={Uri.EscapeDataString(fixtureId)}", ct);
        if (response is not { ValueKind: JsonValueKind.Array }) return null;
        var first = response.Value.EnumerateArray().FirstOrDefault();
        if (first.ValueKind != JsonValueKind.Object) return null;
        if (first.TryGetProperty("teams", out var teams) &&
            teams.TryGetProperty("home", out var home) &&
            home.TryGetProperty("id", out var id))
        {
            return new RawFixture(id.GetRawText());
        }
        return null;
    }

    private static Fixture? ParseFixture(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty("fixture", out var fx) || !el.TryGetProperty("teams", out var teams)) return null;

        var id = fx.TryGetProperty("id", out var fid) ? fid.GetRawText() : null;
        if (id is null) return null;

        var home = ParseTeam(teams.GetProperty("home"));
        var away = ParseTeam(teams.GetProperty("away"));

        int hg = 0, ag = 0;
        if (el.TryGetProperty("goals", out var goals))
        {
            hg = goals.TryGetProperty("home", out var h) && h.ValueKind == JsonValueKind.Number ? h.GetInt32() : 0;
            ag = goals.TryGetProperty("away", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetInt32() : 0;
        }

        var statusShort = fx.TryGetProperty("status", out var st) && st.TryGetProperty("short", out var ss) ? ss.GetString() : null;
        var elapsed = st.ValueKind == JsonValueKind.Object && st.TryGetProperty("elapsed", out var elp) && elp.ValueKind == JsonValueKind.Number ? elp.GetInt32() : 0;
        var kickoff = fx.TryGetProperty("date", out var dt) && dt.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(dt.GetString(), out var k) ? k : DateTimeOffset.UtcNow;
        var competition = el.TryGetProperty("league", out var lg) && lg.TryGetProperty("name", out var ln) ? ln.GetString() : null;
        var referee = fx.TryGetProperty("referee", out var rf) ? rf.GetString() : null;

        return new Fixture(id, home, away, new Score(hg, ag), MapStatus(statusShort), elapsed, kickoff, competition, referee);
    }

    private static Team ParseTeam(JsonElement el)
    {
        var id = el.TryGetProperty("id", out var i) ? i.GetRawText() : "?";
        var name = el.TryGetProperty("name", out var n) ? n.GetString() ?? "?" : "?";
        var logo = el.TryGetProperty("logo", out var l) ? l.GetString() : null;
        var shortName = name.Length >= 3 ? name[..3].ToUpperInvariant() : name.ToUpperInvariant();
        return new Team(id, name, shortName, name, logo);
    }

    private static Lineup ParseLineup(JsonElement el)
    {
        var formation = el.TryGetProperty("formation", out var f) ? f.GetString() : null;
        var xi = ParsePlayers(el, "startXI");
        var bench = ParsePlayers(el, "substitutes");
        return new Lineup(formation, xi, bench);
    }

    private static IReadOnlyList<Player> ParsePlayers(JsonElement teamEl, string prop)
    {
        if (!teamEl.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array) return [];
        var list = new List<Player>();
        foreach (var item in arr.EnumerateArray())
        {
            if (!item.TryGetProperty("player", out var p)) continue;
            var id = p.TryGetProperty("id", out var pid) ? pid.GetRawText() : Guid.NewGuid().ToString();
            var name = p.TryGetProperty("name", out var pn) ? pn.GetString() ?? "?" : "?";
            var pos = p.TryGetProperty("pos", out var pp) ? pp.GetString() : null;
            int? num = p.TryGetProperty("number", out var nu) && nu.ValueKind == JsonValueKind.Number ? nu.GetInt32() : null;
            list.Add(new Player(id, name, pos, num));
        }
        return list;
    }

    private static TeamStatistics ParseStatistics(JsonElement el)
    {
        int? possession = null, shots = null, sot = null, corners = null, fouls = null, offsides = null, passAcc = null;
        double? xg = null;
        if (el.TryGetProperty("statistics", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in arr.EnumerateArray())
            {
                var type = s.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (!s.TryGetProperty("value", out var v)) continue;
                switch (type)
                {
                    case "Ball Possession": possession = ParsePercent(v); break;
                    case "Total Shots": shots = ParseInt(v); break;
                    case "Shots on Goal": sot = ParseInt(v); break;
                    case "Corner Kicks": corners = ParseInt(v); break;
                    case "Fouls": fouls = ParseInt(v); break;
                    case "Offsides": offsides = ParseInt(v); break;
                    case "Passes %": passAcc = ParsePercent(v); break;
                    case "expected_goals": xg = ParseDouble(v); break;
                }
            }
        }
        return new TeamStatistics(possession, shots, sot, corners, fouls, offsides, passAcc, xg);
    }

    private static int? ParseInt(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Number => v.GetInt32(),
        JsonValueKind.String when int.TryParse(v.GetString(), out var n) => n,
        _ => null
    };

    private static int? ParsePercent(JsonElement v) => v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString()?.TrimEnd('%'), out var n) ? n : ParseInt(v);

    private static double? ParseDouble(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Number => v.GetDouble(),
        JsonValueKind.String when double.TryParse(v.GetString(), out var n) => n,
        _ => null
    };

    private static MatchStatus MapStatus(string? shortCode) => shortCode switch
    {
        "1H" or "2H" or "ET" or "P" or "BT" or "LIVE" => MatchStatus.Live,
        "HT" => MatchStatus.HalfTime,
        "FT" or "AET" or "PEN" => MatchStatus.Finished,
        "NS" or "TBD" => MatchStatus.Scheduled,
        "PST" => MatchStatus.Postponed,
        "SUSP" or "INT" => MatchStatus.Suspended,
        _ => MatchStatus.Unknown
    };

    private static MatchEventType MapEventType(string type, string? detail) => (type, detail) switch
    {
        ("Goal", "Own Goal") => MatchEventType.OwnGoal,
        ("Goal", "Penalty") => MatchEventType.PenaltyGoal,
        ("Goal", "Missed Penalty") => MatchEventType.MissedPenalty,
        ("Goal", _) => MatchEventType.Goal,
        ("Card", "Yellow Card") => MatchEventType.YellowCard,
        ("Card", "Second Yellow card") => MatchEventType.SecondYellow,
        ("Card", "Red Card") => MatchEventType.RedCard,
        ("subst", _) => MatchEventType.Substitution,
        ("Var", _) => MatchEventType.Var,
        _ => MatchEventType.Other
    };
}
