using Aqorin.Phone.Core.Abstractions;
using Aqorin.Phone.Core.Model;

namespace Aqorin.Phone.Sip.Internal;

// ---------------------------------------------------------------------------------------------------------------------
// Internal seams between the application services and SIPSorcery. Production implementations wrap SIPSorcery;
// the Sip.Tests project substitutes fakes so registration/call logic is tested without a FRITZ!Box or network.
// ---------------------------------------------------------------------------------------------------------------------

/// <summary>An open SIP transport (UDP or TCP socket) bound for one account configuration.</summary>
internal interface ISipTransportHandle : IDisposable
{
    string Description { get; }

    /// <summary>Resolved registrar address all SIP requests are sent to (null = let the stack resolve the URI host).</summary>
    System.Net.IPEndPoint? Registrar { get; }

    /// <summary>Local SIP endpoint advertised in REGISTER Contact so the registrar can call this phone back.</summary>
    System.Net.IPEndPoint? LocalContactEndPoint { get; }
}

internal interface ISipTransportFactory
{
    ISipTransportHandle Create(SipAccountSettings settings, System.Net.IPEndPoint? registrar);
}

/// <summary>Resolves the registrar host name with the operating system resolver (hosts file, VPN DNS, etc.).</summary>
internal interface IRegistrarResolver
{
    Task<System.Net.IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken);
}

/// <summary>Outcome of one REGISTER cycle.</summary>
internal enum RegistrationOutcome
{
    Success,
    /// <summary>Timeout, transport error, 5xx — worth retrying.</summary>
    TemporaryFailure,
    /// <summary>401/403/404/Payment required — credentials or account are wrong; do not retry blindly.</summary>
    AuthenticationFailure,
    /// <summary>Host could not be resolved.</summary>
    ResolutionFailure
}

internal sealed record RegistrationEvent(RegistrationOutcome Outcome, string? Detail, int? StatusCode, int? ExpirySeconds);

/// <summary>Wraps one registration client bound to one transport and account.</summary>
internal interface ISipRegistrationClient : IDisposable
{
    event Action<RegistrationEvent>? Completed;

    /// <summary>Raised after a successful un-register (Expires: 0) was acknowledged.</summary>
    event Action? Removed;

    /// <summary>Sends the first REGISTER and keeps refreshing on success.</summary>
    void Start();

    /// <summary>Stops refreshing. When <paramref name="sendUnregister"/> is true and registered, sends Expires: 0.</summary>
    void Stop(bool sendUnregister);

    bool IsRegistered { get; }
}

internal interface ISipRegistrationClientFactory
{
    ISipRegistrationClient Create(ISipTransportHandle transport, SipAccount account, TimeSpan attemptTimeout);
}

/// <summary>Reason an outgoing call attempt or an established call ended, as reported by the stack.</summary>
internal sealed record SipCallFailure(int? StatusCode, string? ReasonPhrase, string Message);

/// <summary>A ringing incoming call that has not been answered or rejected yet.</summary>
internal interface ISipIncomingCall
{
    string CallId { get; }

    string FromUri { get; }

    string? FromDisplayName { get; }

    /// <summary>Raised when the caller sends CANCEL before we answer.</summary>
    event Action? Cancelled;

    /// <summary>Sends 100 Trying / 180 Ringing. Called once the service decided to present the call to the user.</summary>
    void StartRinging();

    Task<bool> AnswerAsync(IAudioMediaSession mediaSession);

    void Reject(int statusCode, string reasonPhrase);
}

/// <summary>Single-call user agent bound to one transport.</summary>
internal interface ISipUserAgent : IDisposable
{
    event Action<ISipIncomingCall>? IncomingCall;

    /// <summary>Remote 180/183 for the outgoing call.</summary>
    event Action? Ringing;

    /// <summary>Outgoing call was answered and media started.</summary>
    event Action? Answered;

    event Action<SipCallFailure>? Failed;

    /// <summary>The established call ended (remote BYE or after a local hang-up).</summary>
    event Action? Hungup;

    bool IsCallActive { get; }

    /// <summary>Places a call; completes true when answered, false when it failed or was cancelled.</summary>
    Task<bool> CallAsync(string destinationUri, SipAccount account, IAudioMediaSession mediaSession, int ringTimeoutSeconds);

    /// <summary>Cancels a pending outgoing call (CANCEL) or hangs up if it was answered meanwhile.</summary>
    void Cancel();

    /// <summary>Ends the established call (BYE).</summary>
    void Hangup();

    /// <summary>Immediately answers an INVITE with 486 Busy Here without ringing. Used while another call is in progress.</summary>
    void RejectBusy(ISipIncomingCall call);
}

internal interface ISipUserAgentFactory
{
    ISipUserAgent Create(ISipTransportHandle transport);
}

/// <summary>Lets the SIP layer reach the SIPSorcery media session behind an <see cref="IAudioMediaSession"/>.</summary>
internal interface ISipSorceryMediaSessionProvider
{
    SIPSorcery.SIP.App.IMediaSession MediaSession { get; }
}
