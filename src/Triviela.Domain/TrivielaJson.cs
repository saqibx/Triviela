using System.Text.Json;
using System.Text.Json.Serialization;

namespace Triviela.Domain;

/// <summary>
/// The single JSON contract shared by the relay hub, the SignalR client, and the Redis
/// snapshot cache. Web defaults give camelCase + case-insensitive reads; enums are written as
/// readable strings so <see cref="MatchStatus"/> / <see cref="MatchEventType"/> / <see cref="Side"/>
/// round-trip and stay debuggable on the wire.
/// </summary>
public static class TrivielaJson
{
    /// <summary>Shared, frozen options for one-shot (de)serialization, e.g. the Redis cache.</summary>
    public static readonly JsonSerializerOptions Options = CreateOptions();

    /// <summary>
    /// A fresh options instance. Use this when handing options to a component that takes ownership
    /// and may lock/mutate them (the SignalR JSON protocol on both the hub and the client), so the
    /// shared <see cref="Options"/> instance is never locked out from under other callers.
    /// </summary>
    public static JsonSerializerOptions CreateOptions()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        o.Converters.Add(new JsonStringEnumConverter());
        return o;
    }
}
