using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Triviela.Domain;

namespace Triviela.Core;

/// <summary>The AI analyst (ASK) served by the relay backend (using its LLM key), invoked over SignalR.</summary>
public sealed class RelayMatchAnalyst(HubConnection connection, ILogger<RelayMatchAnalyst> logger) : IMatchAnalyst
{
    public bool IsEnabled => true;

    public async Task<string?> AskAsync(MatchSnapshot snapshot, string question, CancellationToken ct)
    {
        if (connection.State != HubConnectionState.Connected)
            return "Relay offline — reconnecting, try again in a moment.";
        try
        {
            return await connection.InvokeAsync<string?>("Ask", snapshot.Fixture.Id, question, ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Relay Ask failed");
            return null;
        }
    }
}
