using Microsoft.Extensions.Hosting;

namespace Triviela.Core;

/// <summary>
/// Local-mode bridge: mirrors the user's <see cref="FocusState.SelectedId"/> into the
/// <see cref="SubscriptionRegistry"/> under a single pseudo-connection, so the poller has exactly
/// one notion of "what to enrich" (the registry) in both local and relay hosts. Only registered
/// for the standalone client poller — on the relay the registry is driven by SignalR connections.
/// </summary>
public sealed class FocusSubscriptionBridge(FocusState focus, SubscriptionRegistry registry) : IHostedService, IDisposable
{
    private const string LocalConnection = "local";
    private string? _watched;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        focus.Changed += OnChanged;
        OnChanged();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        focus.Changed -= OnChanged;
        return Task.CompletedTask;
    }

    private void OnChanged()
    {
        var selected = focus.SelectedId;
        if (selected == _watched) return;
        if (_watched is not null) registry.Unwatch(LocalConnection, _watched);
        if (selected is not null) registry.Watch(LocalConnection, selected);
        _watched = selected;
    }

    public void Dispose() => focus.Changed -= OnChanged;
}
