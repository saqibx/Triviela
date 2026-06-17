namespace Triviela.Core;

public sealed class FreeModeState
{
    public const int FreeDailyCallBudget = 100;

    public const int LookupReserve = 10;

    public const int PollingBudget = FreeDailyCallBudget - LookupReserve;

    public const int TypicalMatchMinutes = 100;

    public const int CallsPerCycle = 2;

    private volatile bool _enabled;

    public bool Enabled => _enabled;

    public void Enable() => _enabled = true;
    public void Disable() => _enabled = false;

    public bool Toggle() => _enabled = !_enabled;

    public int LivePollSeconds => 180;

    public int CapDailyBudget(int configuredBudget) =>
    Enabled ? Math.Min(configuredBudget, FreeDailyCallBudget) : configuredBudget;
}
