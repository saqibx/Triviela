namespace Triviela.Core;

/// <summary>
/// Client-side relay config. When <see cref="Url"/> is set the client runs in relay mode:
/// it subscribes to the hosted backend over SignalR instead of polling football data locally.
/// </summary>
public sealed class RelayOptions
{
    public const string SectionName = "Relay";

    /// <summary>Full SignalR hub URL, e.g. https://triviela.fly.dev/hub/match. Empty = no relay.</summary>
    public string? Url { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Url);
}
