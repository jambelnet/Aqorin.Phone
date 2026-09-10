namespace Aqorin.Phone.Core.Model;

/// <summary>Immutable snapshot of the registration status that is safe to hand to the UI.</summary>
public sealed record RegistrationStatus
{
    public RegistrationState State { get; init; } = RegistrationState.Disconnected;

    /// <summary>Short, user-friendly message (for example "Registered as 620@fritz.box").</summary>
    public string Message { get; init; } = "Not registered";

    /// <summary>Technical detail for the diagnostics panel. Never contains credentials.</summary>
    public string? Detail { get; init; }

    /// <summary>The address of record the status refers to, if any.</summary>
    public string? AddressOfRecord { get; init; }

    /// <summary>When the current registration expires at the registrar (UTC).</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>When the next automatic retry is scheduled (UTC), if a retry is pending.</summary>
    public DateTimeOffset? NextRetryAt { get; init; }

    /// <summary>Consecutive failed attempts.</summary>
    public int FailedAttempts { get; init; }

    /// <summary>True when failure is unequivocal (wrong password, unknown user) and no retry is scheduled.</summary>
    public bool IsAuthenticationFailure { get; init; }

    public bool IsRegistered => State == RegistrationState.Registered;

    public static RegistrationStatus Disconnected { get; } = new();
}
