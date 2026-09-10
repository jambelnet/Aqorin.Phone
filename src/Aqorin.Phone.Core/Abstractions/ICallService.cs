using Aqorin.Phone.Core.Model;

namespace Aqorin.Phone.Core.Abstractions;

/// <summary>
/// Single-call telephony service. Enforces the <see cref="CallStateMachine"/> rules and raises
/// <see cref="CallChanged"/> with immutable snapshots. Events may be raised on any thread.
/// </summary>
public interface ICallService : IAsyncDisposable
{
    CallInfo CurrentCall { get; }

    AudioMediaSessionOptions MediaOptions { get; set; }

    bool IsMuted { get; }

    bool IsOnHold { get; }

    bool IsSpeakerEnabled { get; }

    event EventHandler<CallInfo>? CallChanged;

    /// <summary>
    /// Normalises <paramref name="destination"/> and places an outgoing call. Completes when the call is answered
    /// (Active) or has failed/ended. Requires an active registration; throws <see cref="InvalidCallOperationException"/>
    /// when a call is already in progress and <see cref="InvalidDestinationException"/> for bad input.
    /// </summary>
    Task<CallInfo> PlaceCallAsync(string destination, CancellationToken cancellationToken = default);

    /// <summary>Answers the ringing incoming call. Throws <see cref="InvalidCallOperationException"/> when none exists.</summary>
    Task AnswerAsync(CancellationToken cancellationToken = default);

    /// <summary>Rejects the ringing incoming call with 486 Busy Here.</summary>
    Task RejectAsync(CancellationToken cancellationToken = default);

    /// <summary>Ends the current call (BYE) or cancels the outgoing attempt (CANCEL).</summary>
    Task HangupAsync(CancellationToken cancellationToken = default);

    Task SetMutedAsync(bool muted, CancellationToken cancellationToken = default);

    Task SetHoldAsync(bool held, CancellationToken cancellationToken = default);

    Task SetSpeakerAsync(bool enabled, CancellationToken cancellationToken = default);
}
