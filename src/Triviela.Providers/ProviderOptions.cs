namespace Triviela.Providers;

public sealed class ApiFootballOptions
{
    public const string SectionName = "ApiFootball";

    public string ApiKey { get; set; } = "";

    public string BaseUrl { get; set; } = "https://v3.football.api-sports.io/";

    public int Season { get; set; } = 2026;

    /// <summary>
    /// Only show live fixtures from these API-Football league IDs. Defaults to the competitions
    /// that matter: World Cup (1), Euros (4), Champions League (2), and the top-5 European leagues —
    /// Premier League (39), La Liga (140), Serie A (135), Bundesliga (78), Ligue 1 (61).
    /// Empty array = no filter (all live matches).
    /// </summary>
    public int[] LiveLeagueIds { get; set; } = [1, 4, 2, 39, 140, 135, 78, 61];

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);
}

public sealed class EspnOptions
{
    public const string SectionName = "Espn";

    public string BaseUrl { get; set; } = "https://site.api.espn.com/";

    /// <summary>ESPN soccer league slugs to scan for live fixtures: World Cup, Euros, Champions
    /// League, and the top-5 European leagues. ESPN's scoreboard is already per-league, so this is
    /// the equivalent whitelist for the keyless tier.</summary>
    public string[] Leagues { get; set; } =
    [
        "fifa.world", "uefa.euro", "uefa.champions", "eng.1", "esp.1", "ita.1", "ger.1", "fra.1"
    ];

    public bool Enabled { get; set; } = true;

    public bool IsConfigured => Enabled;
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

    /// <summary>Hard ceiling on total LLM spend per UTC day across every match — the real cost guard
    /// on a relay that enriches many matches. Raise it if the AI panels go quiet too early.</summary>
    public decimal GlobalDailyBudgetUsd { get; set; } = 5.00m;
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
