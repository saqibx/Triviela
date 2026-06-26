using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Triviela.Domain;

namespace Triviela.Providers;

public sealed class ApiFootballReference(
    HttpClient http,
    IOptions<ApiFootballOptions> options,
    ILogger<ApiFootballReference> logger) : IFootballReference
{
    private readonly ApiFootballOptions _opts = options.Value;

    public bool IsLive => _opts.IsConfigured;

    private int[] Seasons => [_opts.Season, _opts.Season - 1];

    private async Task<JsonElement?> GetAsync(string path, CancellationToken ct)
    {
        if (!IsLive) return null;
        try
        {
            using var resp = await http.GetAsync(path, ct);
            if (!resp.IsSuccessStatusCode)
            {
                logger.LogWarning("Reference {Path} -> {Status}", path, (int)resp.StatusCode);
                return null;
            }
            await using var s = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct);
            return doc.RootElement.TryGetProperty("response", out var r) ? r.Clone() : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Reference {Path} failed", path);
            return null;
        }
    }

    private record ResolvedTeam(string Id, string Name, string? Country, string? Logo);

    private async Task<ResolvedTeam?> ResolveTeamAsync(string query, CancellationToken ct)
    {
        var resp = await GetAsync($"teams?search={Uri.EscapeDataString(query)}", ct);
        if (resp is not { ValueKind: JsonValueKind.Array }) return null;

        var nq = Normalize(query);
        ResolvedTeam? best = null;
        int bestScore = int.MinValue;
        int order = 0;

        foreach (var el in resp.Value.EnumerateArray())
        {
            if (!el.TryGetProperty("team", out var t)) continue;
            var id = t.TryGetProperty("id", out var i) ? i.GetRawText() : null;
            var name = t.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (id is null || name is null) continue;
            var country = t.TryGetProperty("country", out var c) ? c.GetString() : null;
            var logo = t.TryGetProperty("logo", out var l) ? l.GetString() : null;
            var national = t.TryGetProperty("national", out var na) && na.ValueKind == JsonValueKind.True;

            var nn = Normalize(name);
            int score = nn == nq ? 1000
                : national ? 500
                : nn.Contains(nq) ? 100
                : 0;
            score = score * 1000 - order++;

            if (score > bestScore)
            {
                bestScore = score;
                best = new ResolvedTeam(id, name, country, logo);
            }
        }
        return best;
    }

    private static string Normalize(string s)
    {
        var decomposed = s.Trim().ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        return sb.ToString().Normalize(System.Text.NormalizationForm.FormC);
    }

    public async Task<TeamProfile?> GetTeamProfileAsync(string teamQuery, CancellationToken ct)
    {
        var team = await ResolveTeamAsync(teamQuery, ct);
        if (team is null) return null;

        var domainTeam = new Team(team.Id, team.Name,
            team.Name.Length >= 3 ? team.Name[..3].ToUpperInvariant() : team.Name.ToUpperInvariant(),
            team.Country, team.Logo);

        var recent = await GetTeamFormAsync(team.Id, 8, ct);
        var manager = await GetManagerAsync(team.Id, ct);
        var standing = await GetStandingAsync(team.Id, ct);

        return new TeamProfile(
            domainTeam,
            standing?.Competition,
            standing?.Rank, standing?.Points, standing?.Played,
            standing?.Win, standing?.Draw, standing?.Loss,
            standing?.GoalsFor, standing?.GoalsAgainst,
            manager,
            recent,

            []);
    }

    private async Task<IReadOnlyList<FormResult>> GetTeamFormAsync(string teamId, int last, CancellationToken ct)
    {
        var resp = await GetAsync($"fixtures?team={teamId}&last={last}", ct);
        if (resp is not { ValueKind: JsonValueKind.Array }) return [];

        var results = new List<FormResult>();
        foreach (var f in resp.Value.EnumerateArray())
        {
            try
            {
                if (!f.TryGetProperty("teams", out var teams) || !f.TryGetProperty("goals", out var goals)) continue;
                var home = teams.GetProperty("home");
                var away = teams.GetProperty("away");
                if (goals.GetProperty("home").ValueKind != JsonValueKind.Number) continue;

                var homeId = home.GetProperty("id").GetRawText();
                bool isHome = homeId == teamId;
                int hg = goals.GetProperty("home").GetInt32();
                int ag = goals.GetProperty("away").GetInt32();
                int gf = isHome ? hg : ag, ga = isHome ? ag : hg;
                var opp = (isHome ? away : home).GetProperty("name").GetString() ?? "?";
                var date = f.GetProperty("fixture").GetProperty("date").GetString();
                var comp = f.TryGetProperty("league", out var lg) && lg.TryGetProperty("name", out var ln) ? ln.GetString() ?? "" : "";
                results.Add(new FormResult(DateTimeOffset.TryParse(date, out var dt) ? dt : default, opp, isHome, gf, ga, comp));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Skipping malformed form fixture");
            }
        }

        return results.OrderByDescending(r => r.Date).ToArray();
    }

    private async Task<Manager?> GetManagerAsync(string teamId, CancellationToken ct)
    {
        // `coachs?team=` returns EVERY coach the team has ever had, unordered. The current one is
        // the coach whose career stint at this team is still open (end == null). Among historical
        // coaches (none open), fall back to the most recent start, so we never return an ancient name.
        var resp = await GetAsync($"coachs?team={teamId}", ct);
        if (resp is not { ValueKind: JsonValueKind.Array }) return null;

        JsonElement chosen = default;
        bool found = false, chosenCurrent = false;
        DateTimeOffset chosenStart = DateTimeOffset.MinValue;

        foreach (var coach in resp.Value.EnumerateArray())
        {
            if (coach.ValueKind != JsonValueKind.Object) continue;

            bool isCurrent = false;
            DateTimeOffset latestStart = DateTimeOffset.MinValue;
            if (coach.TryGetProperty("career", out var career) && career.ValueKind == JsonValueKind.Array)
            {
                foreach (var stint in career.EnumerateArray())
                {
                    if (!stint.TryGetProperty("team", out var st) || !st.TryGetProperty("id", out var sid)
                        || sid.ValueKind == JsonValueKind.Null || sid.GetRawText() != teamId) continue;

                    if (stint.TryGetProperty("start", out var ss) && DateTimeOffset.TryParse(ss.GetString(), out var sd) && sd > latestStart)
                        latestStart = sd;
                    var end = stint.TryGetProperty("end", out var ee) ? ee : default;
                    if (end.ValueKind == JsonValueKind.Null || (end.ValueKind == JsonValueKind.String && string.IsNullOrEmpty(end.GetString())))
                        isCurrent = true;
                }
            }

            bool better = !found
                || (isCurrent && !chosenCurrent)
                || (isCurrent == chosenCurrent && latestStart > chosenStart);
            if (better) { chosen = coach; chosenCurrent = isCurrent; chosenStart = latestStart; found = true; }
        }

        if (!found) return null;
        var name = chosen.TryGetProperty("name", out var n) ? n.GetString() : null;
        if (string.IsNullOrWhiteSpace(name)) return null;
        var id = chosen.TryGetProperty("id", out var i) ? i.GetRawText() : name;
        var nat = chosen.TryGetProperty("nationality", out var na) ? na.GetString() : null;
        int? age = chosen.TryGetProperty("age", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetInt32() : null;
        return new Manager(id!, name!, nat, age);
    }

    private record Standing(string Competition, int Rank, int Points, int Played, int Win, int Draw, int Loss, int GoalsFor, int GoalsAgainst);

    private async Task<Standing?> GetStandingAsync(string teamId, CancellationToken ct)
    {
        foreach (var season in Seasons)
        {
            var resp = await GetAsync($"standings?team={teamId}&season={season}", ct);
            if (resp is not { ValueKind: JsonValueKind.Array }) continue;

            foreach (var lgEl in resp.Value.EnumerateArray())
            {
                if (!lgEl.TryGetProperty("league", out var lg)) continue;
                var comp = lg.TryGetProperty("name", out var ln) ? ln.GetString() ?? "" : "";
                if (!lg.TryGetProperty("standings", out var groups) || groups.ValueKind != JsonValueKind.Array) continue;

                foreach (var group in groups.EnumerateArray())
                {
                    foreach (var row in group.EnumerateArray())
                    {
                        if (!row.TryGetProperty("team", out var t) || t.GetProperty("id").GetRawText() != teamId) continue;
                        int rank = row.TryGetProperty("rank", out var rk) ? rk.GetInt32() : 0;
                        int pts = row.TryGetProperty("points", out var p) ? p.GetInt32() : 0;
                        var all = row.GetProperty("all");
                        int played = all.GetProperty("played").GetInt32();
                        int win = all.GetProperty("win").GetInt32();
                        int draw = all.GetProperty("draw").GetInt32();
                        int loss = all.GetProperty("lose").GetInt32();
                        var g = all.GetProperty("goals");
                        return new Standing(comp, rank, pts, played, win, draw, loss,
                            g.GetProperty("for").GetInt32(), g.GetProperty("against").GetInt32());
                    }
                }
            }
        }
        return null;
    }

    public async Task<PlayerProfile?> GetPlayerProfileAsync(string playerQuery, string? fixtureId, CancellationToken ct)
    {
        // 1. Prefer a player in the live match's squad (cheap, unambiguous).
        if (fixtureId is not null && await ResolvePlayerInFixtureAsync(fixtureId, playerQuery, ct) is { } inFixtureId)
        {
            var fixtureProfile = await GetPlayerByIdAsync(inFixtureId, ct);
            if (fixtureProfile is not null) return fixtureProfile;
        }

        // 2. Global search returns many namesakes (e.g. "Sane" → 75 players). Disambiguate by who's
        //    actually playing: the candidate with the most appearances is almost always the one meant.
        var candidates = await ResolvePlayerCandidatesAsync(playerQuery, ct);
        PlayerProfile? best = null;
        int bestApps = -1;
        foreach (var id in candidates)
        {
            var profile = await GetPlayerByIdAsync(id, ct);
            if (profile is null) continue;
            if (profile.Appearances > bestApps) { bestApps = profile.Appearances; best = profile; }
        }
        return best;
    }

    private async Task<string?> ResolvePlayerInFixtureAsync(string fixtureId, string query, CancellationToken ct)
    {
        var resp = await GetAsync($"fixtures/players?fixture={Uri.EscapeDataString(fixtureId)}", ct);
        if (resp is not { ValueKind: JsonValueKind.Array }) return null;
        foreach (var team in resp.Value.EnumerateArray())
        {
            if (!team.TryGetProperty("players", out var players) || players.ValueKind != JsonValueKind.Array) continue;
            foreach (var p in players.EnumerateArray())
            {
                if (!p.TryGetProperty("player", out var pl)) continue;
                var name = pl.TryGetProperty("name", out var pn) ? pn.GetString() : null;
                if (name is not null && name.Contains(query, StringComparison.OrdinalIgnoreCase))
                    return pl.TryGetProperty("id", out var id) ? id.GetRawText() : null;
            }
        }
        return null;
    }

    // Returns the most relevant candidate ids: exact full-name first, then exact surname, then
    // partial — capped to bound the API cost of profiling each one for the appearance-based ranking.
    private const int MaxPlayerCandidates = 5;

    private async Task<IReadOnlyList<string>> ResolvePlayerCandidatesAsync(string query, CancellationToken ct)
    {
        var resp = await GetAsync($"players/profiles?search={Uri.EscapeDataString(query)}", ct);
        if (resp is not { ValueKind: JsonValueKind.Array }) return [];

        var nq = Normalize(query);
        var exactFull = new List<string>();
        var exactSurname = new List<string>();
        var partial = new List<string>();

        foreach (var el in resp.Value.EnumerateArray())
        {
            if (!el.TryGetProperty("player", out var pl)) continue;
            var id = pl.TryGetProperty("id", out var pid) ? pid.GetRawText() : null;
            if (id is null) continue;

            var name = pl.TryGetProperty("name", out var pn) ? pn.GetString() : null;
            var firstname = pl.TryGetProperty("firstname", out var fn) ? fn.GetString() : null;
            var lastname = pl.TryGetProperty("lastname", out var lnm) ? lnm.GetString() : null;

            var nName = Normalize(name ?? "");
            var nFull = Normalize($"{firstname} {lastname}".Trim());
            var nLast = Normalize(lastname ?? "");

            if (nq.Length > 0 && (nName == nq || nFull == nq)) exactFull.Add(id);
            else if (nq.Length > 0 && nLast == nq) exactSurname.Add(id);
            else if (nq.Length > 0 && (nName.Contains(nq) || nFull.Contains(nq))) partial.Add(id);
        }

        return exactFull.Concat(exactSurname).Concat(partial).Take(MaxPlayerCandidates).ToList();
    }

    private async Task<PlayerProfile?> GetPlayerByIdAsync(string playerId, CancellationToken ct)
    {
        PlayerProfile? best = null;
        int bestApps = -1;

        foreach (var season in Seasons)
        {
            var resp = await GetAsync($"players?id={playerId}&season={season}", ct);
            if (resp is not { ValueKind: JsonValueKind.Array }) continue;
            var first = resp.Value.EnumerateArray().FirstOrDefault();
            if (first.ValueKind != JsonValueKind.Object || !first.TryGetProperty("player", out var pl)) continue;

            var name = pl.TryGetProperty("name", out var pn) ? pn.GetString() ?? "?" : "?";
            int? age = pl.TryGetProperty("age", out var ag) && ag.ValueKind == JsonValueKind.Number ? ag.GetInt32() : null;
            var nat = pl.TryGetProperty("nationality", out var na) ? na.GetString() : null;
            var pos = pl.TryGetProperty("position", out var po) ? po.GetString() : null;
            var photo = pl.TryGetProperty("photo", out var ph) ? ph.GetString() : null;

            var (club, apps, goals, assists, lines) = AggregateStats(first);
            if (apps > bestApps)
            {
                bestApps = apps;
                var player = new Player(playerId, name, pos, null, age, nat, club, photo);
                best = new PlayerProfile(player, club, apps, goals, assists, lines);
            }
        }
        return best;
    }

    private static (string? Club, int Apps, int Goals, int Assists, IReadOnlyList<PlayerStatLine> Lines) AggregateStats(JsonElement playerResponse)
    {
        if (!playerResponse.TryGetProperty("statistics", out var stats) || stats.ValueKind != JsonValueKind.Array)
            return (null, 0, 0, 0, []);

        var lines = new List<PlayerStatLine>();
        int totApps = 0, totGoals = 0, totAssists = 0;
        string? topClub = null; int topApps = -1;
        foreach (var s in stats.EnumerateArray())
        {
            var teamName = s.TryGetProperty("team", out var t) && t.TryGetProperty("name", out var tn) ? tn.GetString() ?? "?" : "?";
            var comp = s.TryGetProperty("league", out var lg) && lg.TryGetProperty("name", out var ln) ? ln.GetString() ?? "?" : "?";
            int apps = s.TryGetProperty("games", out var g) && g.TryGetProperty("appearences", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetInt32() : 0;
            int goals = s.TryGetProperty("goals", out var go) && go.TryGetProperty("total", out var gt) && gt.ValueKind == JsonValueKind.Number ? gt.GetInt32() : 0;
            int assists = go.ValueKind == JsonValueKind.Object && go.TryGetProperty("assists", out var asn) && asn.ValueKind == JsonValueKind.Number ? asn.GetInt32() : 0;
            lines.Add(new PlayerStatLine(comp, teamName, apps, goals, assists));
            totApps += apps; totGoals += goals; totAssists += assists;
            if (apps > topApps) { topApps = apps; topClub = teamName; }
        }
        return (topClub, totApps, totGoals, totAssists, lines.OrderByDescending(l => l.Apps).ToArray());
    }

    public async Task<HeadToHead?> GetHeadToHeadAsync(string teamAQuery, string teamBQuery, CancellationToken ct)
    {
        var a = await ResolveTeamAsync(teamAQuery, ct);
        var b = await ResolveTeamAsync(teamBQuery, ct);
        if (a is null || b is null) return null;
        return await GetHeadToHeadByIdsAsync(a.Id, a.Name, b.Id, b.Name, ct);
    }

    public async Task<HeadToHead?> GetHeadToHeadByIdsAsync(string idA, string nameA, string idB, string nameB, CancellationToken ct)
    {
        var resp = await GetAsync($"fixtures/headtohead?h2h={idA}-{idB}&last=10", ct);
        if (resp is not { ValueKind: JsonValueKind.Array }) return null;

        int aWins = 0, draws = 0, bWins = 0, aGoals = 0, bGoals = 0;
        var meetings = new List<Meeting>();
        Meeting? biggest = null; int biggestMargin = -1;

        foreach (var f in resp.Value.EnumerateArray())
        {
            try
            {
                if (!f.TryGetProperty("goals", out var goals) || goals.GetProperty("home").ValueKind != JsonValueKind.Number) continue;
                var teams = f.GetProperty("teams");
                var homeId = teams.GetProperty("home").GetProperty("id").GetRawText();
                var homeName = teams.GetProperty("home").GetProperty("name").GetString() ?? "?";
                var awayName = teams.GetProperty("away").GetProperty("name").GetString() ?? "?";
                int hg = goals.GetProperty("home").GetInt32();
                int ag = goals.GetProperty("away").GetInt32();
                var date = f.GetProperty("fixture").GetProperty("date").GetString();
                var comp = f.TryGetProperty("league", out var lg) && lg.TryGetProperty("name", out var ln) ? ln.GetString() ?? "" : "";

                var meeting = new Meeting(DateTimeOffset.TryParse(date, out var dt) ? dt : default, homeName, awayName, hg, ag, comp);
                meetings.Add(meeting);

                bool aIsHome = homeId == idA;
                int aG = aIsHome ? hg : ag, bG = aIsHome ? ag : hg;
                aGoals += aG; bGoals += bG;
                if (aG > bG) aWins++; else if (aG < bG) bWins++; else draws++;

                int margin = Math.Abs(hg - ag);
                if (margin > biggestMargin) { biggestMargin = margin; biggest = meeting; }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Skipping malformed h2h fixture");
            }
        }

        return new HeadToHead(nameA, nameB, meetings.Count, aWins, draws, bWins, aGoals, bGoals, biggest,
            meetings.OrderByDescending(m => m.Date).ToArray());
    }
}
