using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Triviela.Domain;

namespace Triviela.Core;

/// <summary>Reference lookups served by the relay backend (using its key), invoked over SignalR.</summary>
public sealed class RelayFootballReference(HubConnection connection, ILogger<RelayFootballReference> logger) : IFootballReference
{
    public bool IsLive => true;

    public Task<TeamProfile?> GetTeamProfileAsync(string teamQuery, CancellationToken ct) =>
        InvokeAsync<TeamProfile?>("LookupTeam", ct, teamQuery);

    public Task<PlayerProfile?> GetPlayerProfileAsync(string playerQuery, string? fixtureId, CancellationToken ct) =>
        InvokeAsync<PlayerProfile?>("LookupPlayer", ct, playerQuery, fixtureId);

    public Task<HeadToHead?> GetHeadToHeadAsync(string teamAQuery, string teamBQuery, CancellationToken ct) =>
        InvokeAsync<HeadToHead?>("LookupHeadToHead", ct, teamAQuery, teamBQuery);

    // Poller-only path; unused on the relay client (the client doesn't run a poller).
    public Task<HeadToHead?> GetHeadToHeadByIdsAsync(string idA, string nameA, string idB, string nameB, CancellationToken ct) =>
        GetHeadToHeadAsync(nameA, nameB, ct);

    private async Task<T?> InvokeAsync<T>(string method, CancellationToken ct, params object?[] args)
    {
        if (connection.State != HubConnectionState.Connected) return default;
        try
        {
            return await connection.InvokeCoreAsync<T>(method, args, ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Relay {Method} failed", method);
            return default;
        }
    }
}
