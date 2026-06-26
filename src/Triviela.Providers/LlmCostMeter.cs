namespace Triviela.Providers;

public sealed class LlmCostMeter
{
    private readonly object _gate = new();
    private string? _matchId;
    private decimal _spentUsd;

    // Global, day-rolling spend that does NOT reset per match. This is the real ceiling on a relay
    // that enriches many matches — without it the per-match budget resets every StartMatch and the
    // cap is never actually enforced over a day (which is how a relay can run away with LLM spend).
    private decimal _globalSpentUsd;
    private DateOnly _globalDay;

    private const decimal InputUsdPerMillion = 1.00m;
    private const decimal OutputUsdPerMillion = 5.00m;
    private const decimal UsdPerWebSearch = 0.01m;

    private const long BaseInputTokens = 8_000;
    private const long InputTokensPerWebSearch = 50_000;

    public decimal BudgetUsd { get; init; } = 1.50m;

    /// <summary>Hard ceiling on total LLM spend per UTC day across ALL matches. The genuine guard.</summary>
    public decimal GlobalDailyBudgetUsd { get; init; } = 5.00m;

    public decimal SpentUsd { get { lock (_gate) return _spentUsd; } }
    public decimal RemainingUsd { get { lock (_gate) return Math.Max(0m, BudgetUsd - _spentUsd); } }
    public decimal GlobalSpentTodayUsd { get { lock (_gate) { RollDay(); return _globalSpentUsd; } } }

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
        lock (_gate)
        {
            RollDay();
            return _spentUsd + reserveUsd <= BudgetUsd
                && _globalSpentUsd + reserveUsd <= GlobalDailyBudgetUsd;
        }
    }

    private void RollDay()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (today != _globalDay) { _globalDay = today; _globalSpentUsd = 0m; }
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
        lock (_gate)
        {
            RollDay();
            _spentUsd += usd;
            _globalSpentUsd += usd;
        }
    }
}
