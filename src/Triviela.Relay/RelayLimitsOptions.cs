namespace Triviela.Relay;

/// <summary>
/// Server-side abuse guards. Lookups and ASK run on the operator's shared API-Football / LLM
/// keys, so one user must not be able to drain them. ASK (LLM) is the real cost ceiling.
/// </summary>
public sealed class RelayLimitsOptions
{
    public const string SectionName = "RelayLimits";

    public int LookupsPerConnectionPerMinute { get; set; } = 20;

    public int AsksPerConnectionPerMinute { get; set; } = 4;

    /// <summary>Hard daily cap on ASK (LLM) calls across ALL connections — protects the LLM budget.</summary>
    public int GlobalAsksPerDay { get; set; } = 500;

    public int MaxQuestionLength { get; set; } = 500;
}
