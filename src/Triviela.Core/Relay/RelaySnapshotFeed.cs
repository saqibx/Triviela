using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Triviela.Domain;

namespace Triviela.Core;

/// <summary>
/// Relay-mode feed: owns the SignalR connection to the hosted backend and pumps pushed snapshots
/// into the local <see cref="SnapshotStore"/> / <see cref="FocusState"/>, so the TUI's read path
/// is identical to local mode. Mirrors the user's focus to the server via WatchFixture/Unwatch so
/// the relay enriches what this client is viewing. Connecting is resilient and non-blocking.
/// </summary>
public sealed class RelaySnapshotFeed(
    HubConnection connection,
    SnapshotStore store,
    FocusState focus,
    RelayConnectionState state,
    ILogger<RelaySnapshotFeed> logger) : IHostedService, IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private string? _watched;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        connection.On<MatchSnapshot>("Snapshot", s => store.Publish(s));
        connection.On<List<Fixture>>("LiveFixtures", f => focus.SetLive(f));

        connection.Reconnecting += _ => { state.Status = "reconnecting"; return Task.CompletedTask; };
        connection.Reconnected += async _ => { state.Status = "connected"; await ResyncAsync(); };
        connection.Closed += async error =>
        {
            state.Status = "offline";
            if (_cts.IsCancellationRequested) return;
            logger.LogDebug(error, "Relay connection closed; reconnecting");
            try { await Task.Delay(TimeSpan.FromSeconds(3), _cts.Token); } catch { return; }
            _ = ConnectLoopAsync();
        };

        focus.Changed += OnFocusChanged;
        _ = ConnectLoopAsync();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        focus.Changed -= OnFocusChanged;
        _cts.Cancel();
        try { await connection.StopAsync(cancellationToken); } catch { }
    }

    private async Task ConnectLoopAsync()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested && connection.State == HubConnectionState.Disconnected)
        {
            try
            {
                state.Status = "connecting";
                await connection.StartAsync(ct);
                state.Status = "connected";
                logger.LogInformation("Connected to relay");
                await ResyncAsync();
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                state.Status = "offline";
                logger.LogDebug(ex, "Relay connect failed; retrying in 5s");
                try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch { return; }
            }
        }
    }

    private async Task ResyncAsync()
    {
        try
        {
            var live = await connection.InvokeAsync<List<Fixture>>("GetLiveFixtures", _cts.Token);
            focus.SetLive(live);
            var selected = focus.SelectedId;
            if (selected is not null)
            {
                await connection.InvokeAsync("WatchFixture", selected, _cts.Token);
                _watched = selected;
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Relay resync failed");
        }
    }

    private void OnFocusChanged()
    {
        var selected = focus.SelectedId;
        if (selected == _watched) return;
        var previous = _watched;
        _watched = selected;
        _ = SwitchWatchAsync(previous, selected);
    }

    private async Task SwitchWatchAsync(string? previous, string? next)
    {
        if (connection.State != HubConnectionState.Connected) return;
        try { if (previous is not null) await connection.InvokeAsync("UnwatchFixture", previous, _cts.Token); }
        catch (Exception ex) { logger.LogDebug(ex, "Relay unwatch failed"); }
        try { if (next is not null) await connection.InvokeAsync("WatchFixture", next, _cts.Token); }
        catch (Exception ex) { logger.LogDebug(ex, "Relay watch failed"); }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _cts.Dispose();
        await connection.DisposeAsync();
    }
}
