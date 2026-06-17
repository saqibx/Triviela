namespace Triviela.Providers;

public sealed class ApiFootballOptions
{
    public const string SectionName = "ApiFootball";

    public string ApiKey { get; set; } = "";

    public string BaseUrl { get; set; } = "https://v3.football.api-sports.io/";

    public int Season { get; set; } = 2026;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);
}

public sealed class RedditOptions
{
    public const string SectionName = "Reddit";

    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}

public sealed class LlmOptions
{
    public const string SectionName = "Llm";

    public string Provider { get; set; } = "claude";

    public decimal BudgetUsdPerMatch { get; set; } = 1.50m;
}

public sealed class ClaudeOptions
{
    public const string SectionName = "Claude";

    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "https://api.anthropic.com/";

    public string Model { get; set; } = "claude-haiku-4-5";

    public decimal InputUsdPerMillion { get; set; } = 1.00m;
    public decimal OutputUsdPerMillion { get; set; } = 5.00m;
    public decimal CachedInputUsdPerMillion { get; set; } = 0.10m;
    public decimal UsdPerWebSearch { get; set; } = 0.01m;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);
}

public sealed class OpenAiOptions
{
    public const string SectionName = "OpenAI";

    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "https://api.openai.com/";

    public string Model { get; set; } = "gpt-4o-mini";

    public decimal InputUsdPerMillion { get; set; } = 0.15m;
    public decimal OutputUsdPerMillion { get; set; } = 0.60m;
    public decimal CachedInputUsdPerMillion { get; set; } = 0.075m;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);
}
