namespace Aqorin.Phone.Core.Model;

/// <summary>Immutable snapshot of the single call slot. Safe to hand to the UI thread.</summary>
public sealed record CallInfo
{
    public static CallInfo Idle { get; } = new();

    public string? CallId { get; init; }

    public CallState State { get; init; } = CallState.Idle;

    public CallDirection Direction { get; init; }

    /// <summary>Human-readable remote party (number, display name or SIP URI user part).</summary>
    public string RemoteParty { get; init; } = string.Empty;

    /// <summary>The SIP URI that was dialled or that the INVITE came from.</summary>
    public string? RemoteUri { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? ConnectedAt { get; init; }

    public DateTimeOffset? EndedAt { get; init; }

    public CallEndReason EndReason { get; init; }

    /// <summary>Friendly status text for the UI ("Ringing…", "Busy").</summary>
    public string Message { get; init; } = "No call";

    /// <summary>Technical detail for diagnostics (e.g. "486 Busy Here"). Never contains credentials.</summary>
    public string? Detail { get; init; }

    /// <summary>Negotiated audio codec name once media is up (e.g. "PCMA").</summary>
    public string? Codec { get; init; }

    public bool IsInProgress => State is CallState.Dialing or CallState.Ringing or CallState.Incoming or CallState.Connecting or CallState.Active or CallState.Ending;

    public bool IsResting => State is CallState.Idle or CallState.Failed;

    public TimeSpan? Duration(DateTimeOffset now)
    {
        if (ConnectedAt is null)
        {
            return null;
        }

        var end = EndedAt ?? now;
        var duration = end - ConnectedAt.Value;
        return duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
    }
}
