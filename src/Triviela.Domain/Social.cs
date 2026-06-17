namespace Triviela.Domain;

public record SocialPost(
    string Text,
    string Source,
    DateTimeOffset CreatedUtc,
    string? Url,
    int? Score);

public interface ISocialPulse
{
    string SourceName { get; }

    Task<IReadOnlyList<SocialPost>> GetMatchChatterAsync(string homeTeam, string awayTeam, CancellationToken ct);
}
