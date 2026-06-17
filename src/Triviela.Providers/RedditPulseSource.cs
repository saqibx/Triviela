using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Triviela.Domain;

namespace Triviela.Providers;

public sealed class RedditPulseSource(
    HttpClient http,
    IOptions<RedditOptions> options,
    ILogger<RedditPulseSource> logger) : ISocialPulse
{
    private readonly RedditOptions _opts = options.Value;

    private string? _token;
    private DateTimeOffset _tokenExpiry = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    private sealed record ThreadCache(string? ThreadId, DateTimeOffset ResolvedAt);
    private readonly ConcurrentDictionary<string, ThreadCache> _threads = new();
    private static readonly TimeSpan ThreadTtl = TimeSpan.FromMinutes(5);

    public string SourceName => "Reddit";

    public async Task<IReadOnlyList<SocialPost>> GetMatchChatterAsync(string homeTeam, string awayTeam, CancellationToken ct)
    {
        if (!_opts.IsConfigured) return [];
        var token = await GetTokenAsync(ct);
        if (token is null) return [];

        var threadId = await ResolveThreadAsync(homeTeam, awayTeam, token, ct);
        if (threadId is null) return [];

        return await GetCommentsAsync(threadId, token, ct);
    }

    private async Task<string?> ResolveThreadAsync(string home, string away, string token, CancellationToken ct)
    {
        var key = $"{home}|{away}";
        if (_threads.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.ResolvedAt < ThreadTtl)
            return cached.ThreadId;

        var threadId = await SearchThreadAsync(home, away, token, ct);
        _threads[key] = new ThreadCache(threadId, DateTimeOffset.UtcNow);
        return threadId;
    }

    private async Task<string?> SearchThreadAsync(string home, string away, string token, CancellationToken ct)
    {
        var q = Uri.EscapeDataString($"{home} {away} \"Match Thread\"");
        var url = $"https://oauth.reddit.com/r/soccer+football/search?q={q}&restrict_sr=1&sort=new&limit=25&type=link";
        var root = await GetJsonAsync(url, token, ct);
        if (root is null || !root.Value.TryGetProperty("data", out var data) || !data.TryGetProperty("children", out var children))
            return null;

        var homeTok = FirstToken(home);
        var awayTok = FirstToken(away);

        foreach (var c in children.EnumerateArray())
        {
            if (!c.TryGetProperty("data", out var d)) continue;
            var title = d.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            var lower = title.ToLowerInvariant();
            if (!lower.Contains("match thread")) continue;
            if (!lower.Contains(homeTok) || !lower.Contains(awayTok)) continue;

            return d.TryGetProperty("id", out var id) ? id.GetString() : null;
        }
        return null;
    }

    private static string FirstToken(string name)
    {
        var word = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? name;
        return word.ToLowerInvariant();
    }

    private async Task<IReadOnlyList<SocialPost>> GetCommentsAsync(string threadId, string token, CancellationToken ct)
    {
        var url = $"https://oauth.reddit.com/comments/{threadId}?sort=new&limit=40&depth=1";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                logger.LogWarning("Reddit comments -> {Status}", (int)resp.StatusCode);
                return [];
            }
            await using var s = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() < 2) return [];

            var commentsListing = doc.RootElement[1];
            if (!commentsListing.TryGetProperty("data", out var data) || !data.TryGetProperty("children", out var children))
                return [];

            var posts = new List<SocialPost>();
            foreach (var c in children.EnumerateArray())
            {
                if (c.TryGetProperty("kind", out var kind) && kind.GetString() != "t1") continue;
                if (!c.TryGetProperty("data", out var d)) continue;
                var body = d.TryGetProperty("body", out var b) ? b.GetString() : null;
                var author = d.TryGetProperty("author", out var a) ? a.GetString() : null;
                if (string.IsNullOrWhiteSpace(body) || body is "[deleted]" or "[removed]") continue;
                if (author is null or "AutoModerator" or "[deleted]") continue;

                var created = d.TryGetProperty("created_utc", out var cu) && cu.ValueKind == JsonValueKind.Number
                    ? DateTimeOffset.FromUnixTimeSeconds((long)cu.GetDouble()) : DateTimeOffset.UtcNow;
                var perma = d.TryGetProperty("permalink", out var pl) ? pl.GetString() : null;
                int? score = d.TryGetProperty("score", out var sc) && sc.ValueKind == JsonValueKind.Number ? sc.GetInt32() : null;

                posts.Add(new SocialPost(body!.Trim(), $"u/{author}", created, perma is null ? null : $"https://reddit.com{perma}", score));
            }
            return posts.OrderByDescending(p => p.CreatedUtc).Take(15).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Reddit comments failed");
            return [];
        }
    }

    private async Task<JsonElement?> GetJsonAsync(string url, string token, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                logger.LogWarning("Reddit {Url} -> {Status}", url, (int)resp.StatusCode);
                return null;
            }
            await using var s = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct);
            return doc.RootElement.Clone();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Reddit request failed");
            return null;
        }
    }

    private async Task<string?> GetTokenAsync(CancellationToken ct)
    {
        if (_token is not null && DateTimeOffset.UtcNow < _tokenExpiry) return _token;

        await _tokenLock.WaitAsync(ct);
        try
        {
            if (_token is not null && DateTimeOffset.UtcNow < _tokenExpiry) return _token;

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://www.reddit.com/api/v1/access_token");
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_opts.ClientId}:{_opts.ClientSecret}"));
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
            req.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "client_credentials" });

            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                logger.LogWarning("Reddit token -> {Status}", (int)resp.StatusCode);
                return null;
            }
            await using var s = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct);
            var token = doc.RootElement.TryGetProperty("access_token", out var at) ? at.GetString() : null;
            var expires = doc.RootElement.TryGetProperty("expires_in", out var ei) && ei.ValueKind == JsonValueKind.Number ? ei.GetInt32() : 3600;
            if (token is null) return null;

            _token = token;
            _tokenExpiry = DateTimeOffset.UtcNow.AddSeconds(expires - 60);
            return _token;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Reddit token request failed");
            return null;
        }
        finally { _tokenLock.Release(); }
    }
}
