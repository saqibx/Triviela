namespace Triviela.Providers;

public sealed class LlmCostMeter
{
    private readonly object _gate = new();
    private string? _matchId;
    private decimal _spentUsd;

    private const decimal InputUsdPerMillion = 1.00m;
    private const decimal OutputUsdPerMillion = 5.00m;
    private const decimal UsdPerWebSearch = 0.01m;

    private const long BaseInputTokens = 8_000;
    private const long InputTokensPerWebSearch = 50_000;

    public decimal BudgetUsd { get; init; } = 1.50m;

    public decimal SpentUsd { get { lock (_gate) return _spentUsd; } }
    public decimal RemainingUsd { get { lock (_gate) return Math.Max(0m, BudgetUsd - _spentUsd); } }

    public void StartMatch(string matchId)
    {
        lock (_gate)
        {
            if (_matchId == matchId) return;
            _matchId = matchId;
            _spentUsd = 0m;
        }
    }

    public bool CanSpend(decimal reserveUsd)
    {
        lock (_gate) return _spentUsd + reserveUsd <= BudgetUsd;
    }

    public static decimal EstimateMaxCost(LlmRequest request)
    {
        long searches = request.WebSearch ? Math.Max(1, request.WebSearchMaxUses) : 0;
        long inputTokens = BaseInputTokens + searches * InputTokensPerWebSearch;

        return inputTokens / 1_000_000m * InputUsdPerMillion
             + request.MaxTokens / 1_000_000m * OutputUsdPerMillion
             + searches * UsdPerWebSearch;
    }

    public void RecordCost(decimal usd)
    {
        if (usd <= 0m) return;
        lock (_gate) _spentUsd += usd;
    }
}
