using Aqorin.Phone.Core.Model;

namespace Aqorin.Phone.Core.Abstractions;

/// <summary>
/// Registers the account with the configured SIP registrar and keeps the registration alive
/// (refresh before expiry, bounded retry with exponential backoff, re-register on network change).
/// Implementations must be thread-safe; events may be raised on any thread.
/// </summary>
public interface ISipRegistrationService : IAsyncDisposable
{
    RegistrationStatus Status { get; }

    /// <summary>The account currently registered or being registered. Null when disconnected.</summary>
    SipAccountSettings? CurrentAccount { get; }

    event EventHandler<RegistrationStatus>? StatusChanged;

    /// <summary>
    /// Starts registering. Completes when the first attempt has a definite outcome (registered, retry scheduled,
    /// or unequivocal failure). Throws <see cref="InvalidRegistrationOperationException"/> if already registering/registered.
    /// </summary>
    Task RegisterAsync(SipAccount account, CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-resolves the registrar and recreates the active transport and registration client without signing out.
    /// Also restarts a terminal non-authentication failure when an account is still available.
    /// </summary>
    Task RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>Sends a de-registration (Expires: 0) if registered, cancels retries and releases the transport.</summary>
    Task UnregisterAsync(CancellationToken cancellationToken = default);
}
