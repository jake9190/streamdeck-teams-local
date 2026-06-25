namespace MsTeamsLocal;

/// <summary>Immutable snapshot of the observed Teams meeting state.</summary>
public sealed record TeamsSnapshot
{
    /// <summary>True once the first poll has completed (so we never render a blank/"offline" flash at startup once known).</summary>
    public bool Initialized { get; init; }

    public bool TeamsRunning { get; init; }
    public bool MeetingActive { get; init; }

    /// <summary>True when the microphone is currently muted.</summary>
    public bool Muted { get; init; }

    /// <summary>True when the camera is currently off.</summary>
    public bool CameraOff { get; init; }

    /// <summary>True when the hand is currently raised.</summary>
    public bool HandRaised { get; init; }

    /// <summary>True when screen sharing is currently active.</summary>
    public bool Sharing { get; init; }

    public static readonly TeamsSnapshot Empty = new();
}
