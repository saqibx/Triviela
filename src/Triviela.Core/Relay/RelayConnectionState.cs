namespace Triviela.Core;

/// <summary>Shared, observable relay connection status for the UI status line. Empty in local mode.</summary>
public sealed class RelayConnectionState
{
    private volatile string _status = "";

    /// <summary>One of "", "connecting", "connected", "reconnecting", "offline".</summary>
    public string Status
    {
        get => _status;
        internal set => _status = value;
    }

    public bool IsTrouble => Status is "connecting" or "reconnecting" or "offline";
}
