using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Triviela.Domain;

namespace Triviela.Providers;

public sealed class ApiFootballMatchDossier(
    HttpClient http,
    IOptions<ApiFootballOptions> options,
    ILogger<ApiFootballMatchDossier> logger)
{
    private readonly ApiFootballOptions _opts = options.Value;

    public bool IsLive => _opts.IsConfigured;

    private async Task<JsonElement?> GetAsync(string path, CancellationToken ct)
    {
        if (!IsLive) return null;
        try
        {
            using var resp = await http.GetAsync(path, ct);
            if (!resp.IsSuccessStatusCode)
            {
                logger.LogWarning("Dossier {Path} -> {Status}", path, (int)resp.StatusCode);
                return null;
            }
            await using var s = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct);
            return doc.RootElement.TryGetProperty("response", out var r) ? r.Clone() : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Dossier {Path} failed", path);
            return null;
        }
    }

    public async Task<string?> BuildAsync(Fixture fixture, CancellationToken ct)
    {
        if (!IsLive) return null;

        var sb = new StringBuilder();
        sb.AppendLine($"FIXTURE: {fixture.Home.Name} vs {fixture.Away.Name} — {fixture.Competition}.");
        sb.AppendLine();

        foreach (var (team, label) in new[] { (fixture.Home, "HOME"), (fixture.Away, "AWAY") })
        {
            sb.AppendLine($"=== {label}: {team.Name} (id {team.Id}) ===");
            await AppendCoachAsync(sb, team.Id, ct);
            await AppendSquadAsync(sb, team.Id, ct);
            sb.AppendLine();
        }

        await AppendInjuriesAsync(sb, fixture.Id, ct);

        var text = sb.ToString();

        return text.Contains("Coach:") || text.Contains("Squad:") ? text : null;
    }

    private async Task AppendCoachAsync(StringBuilder sb, string teamId, CancellationToken ct)
    {
        var resp = await GetAsync($"coachs?team={teamId}", ct);
        if (resp is not { ValueKind: JsonValueKind.Array }) return;
        var coach = resp.Value.EnumerateArray().FirstOrDefault();
        if (coach.ValueKind != JsonValueKind.Object) return;

        var name = coach.TryGetProperty("name", out var n) ? n.GetString() : null;
        if (string.IsNullOrWhiteSpace(name)) return;
        var nat = coach.TryGetProperty("nationality", out var na) ? na.GetString() : null;
        int? age = coach.TryGetProperty("age", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetInt32() : null;
        sb.AppendLine($"Coach: {name}{(nat is not null ? $" ({nat})" : "")}{(age is not null ? $", age {age}" : "")}.");

        var coachId = coach.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number ? id.GetRawText() : null;
        if (coachId is not null) await AppendTrophiesAsync(sb, coachId, ct);
    }

    private async Task AppendTrophiesAsync(StringBuilder sb, string coachId, CancellationToken ct)
    {
        var resp = await GetAsync($"trophies?coach={coachId}", ct);
        if (resp is not { ValueKind: JsonValueKind.Array } || resp.Value.GetArrayLength() == 0) return;

        var won = new List<string>();
        foreach (var t in resp.Value.EnumerateArray())
        {
            var league = t.TryGetProperty("league", out var l) ? l.GetString() : null;
            var place = t.TryGetProperty("place", out var p) ? p.GetString() : null;
            var season = t.TryGetProperty("season", out var s) ? s.GetString() : null;
            if (league is null) continue;
            won.Add($"{league}{(season is not null ? $" {season}" : "")} — {place ?? "?"}");
        }
        if (won.Count > 0)
            sb.AppendLine("Coach honours: " + string.Join("; ", won.Take(40)) + ".");
    }

    private async Task AppendSquadAsync(StringBuilder sb, string teamId, CancellationToken ct)
    {
        var resp = await GetAsync($"players/squads?team={teamId}", ct);
        if (resp is not { ValueKind: JsonValueKind.Array }) return;
        var entry = resp.Value.EnumerateArray().FirstOrDefault();
        if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("players", out var players) || players.ValueKind != JsonValueKind.Array)
            return;

        var names = new List<string>();
        foreach (var p in players.EnumerateArray())
        {
            var name = p.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (name is null) continue;
            var pos = p.TryGetProperty("position", out var po) ? po.GetString() : null;
            int? num = p.TryGetProperty("number", out var nu) && nu.ValueKind == JsonValueKind.Number ? nu.GetInt32() : null;
            int? age = p.TryGetProperty("age", out var ag) && ag.ValueKind == JsonValueKind.Number ? ag.GetInt32() : null;
            names.Add($"{(num is not null ? $"#{num} " : "")}{name}{(pos is not null ? $" ({pos}" : "")}{(age is not null ? $", {age}y)" : pos is not null ? ")" : "")}");
        }
        if (names.Count > 0)
            sb.AppendLine("Squad: " + string.Join(", ", names) + ".");
    }

    private async Task AppendInjuriesAsync(StringBuilder sb, string fixtureId, CancellationToken ct)
    {
        var resp = await GetAsync($"injuries?fixture={Uri.EscapeDataString(fixtureId)}", ct);
        if (resp is not { ValueKind: JsonValueKind.Array } || resp.Value.GetArrayLength() == 0) return;

        var lines = new List<string>();
        foreach (var i in resp.Value.EnumerateArray())
        {
            var player = i.TryGetProperty("player", out var pl) && pl.TryGetProperty("name", out var pn) ? pn.GetString() : null;
            if (player is null) continue;
            var team = i.TryGetProperty("team", out var t) && t.TryGetProperty("name", out var tn) ? tn.GetString() : null;
            var reason = pl.TryGetProperty("reason", out var r) ? r.GetString() : null;
            var type = pl.TryGetProperty("type", out var ty) ? ty.GetString() : null;
            lines.Add($"{player}{(team is not null ? $" ({team})" : "")}: {type ?? "out"}{(reason is not null ? $" — {reason}" : "")}");
        }
        if (lines.Count > 0)
        {
            sb.AppendLine("=== INJURIES / UNAVAILABLE ===");
            sb.AppendLine(string.Join("; ", lines) + ".");
        }
    }
}
