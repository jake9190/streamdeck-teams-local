namespace MsTeamsLocal;

/// <summary>Which Teams control an action drives.</summary>
public enum ActionKind
{
    ToggleMute,
    ToggleCamera,
    RaiseHand,
    ShareScreen,
    Leave,
    Reaction,
    AudioDevice,
}

/// <summary>Static description of a Stream Deck action exposed by this plugin.</summary>
public sealed record ActionDescriptor(
    string Id,
    string Label,
    ActionKind Kind,
    string? ReactionAutomationId = null);

/// <summary>The full set of actions, keyed by the UUID suffix used in the manifest.</summary>
public static class ActionCatalog
{
    public const string Prefix = "com.local.msteams-local.";

    public static readonly IReadOnlyList<ActionDescriptor> All = new[]
    {
        new ActionDescriptor("toggle-mute",   "Mute",     ActionKind.ToggleMute),
        new ActionDescriptor("toggle-camera", "Camera",   ActionKind.ToggleCamera),
        new ActionDescriptor("raise-hand",    "Hand",     ActionKind.RaiseHand),
        new ActionDescriptor("share-screen",  "Share",    ActionKind.ShareScreen),
        new ActionDescriptor("leave-meeting", "Leave",    ActionKind.Leave),
        new ActionDescriptor("react-like",      "Like",      ActionKind.Reaction, "like-button"),
        new ActionDescriptor("react-love",      "Love",      ActionKind.Reaction, "heart-button"),
        new ActionDescriptor("react-applause",  "Applause",  ActionKind.Reaction, "applause-button"),
        new ActionDescriptor("react-laugh",     "Laugh",     ActionKind.Reaction, "laugh-button"),
        new ActionDescriptor("react-surprised", "Surprised", ActionKind.Reaction, "surprised-button"),

        // Switches the meeting's microphone and/or speaker to a pre-selected device pairing.
        new ActionDescriptor("audio-device", "Audio Device", ActionKind.AudioDevice),
    };

    private static readonly Dictionary<string, ActionDescriptor> ById =
        All.ToDictionary(a => a.Id, StringComparer.Ordinal);

    /// <summary>Every action requires an active meeting to be pressable.</summary>
    public static bool RequiresMeeting(ActionDescriptor d) => true;

    /// <summary>Resolve a descriptor from a full Stream Deck action UUID (or its short id).</summary>
    public static ActionDescriptor? Resolve(string actionUuid)
    {
        if (string.IsNullOrEmpty(actionUuid)) return null;
        var id = actionUuid.StartsWith(Prefix, StringComparison.Ordinal)
            ? actionUuid[Prefix.Length..]
            : actionUuid;
        return ById.TryGetValue(id, out var d) ? d : null;
    }
}
