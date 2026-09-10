using System.Threading.Channels;
using Aqorin.Phone.Core.Abstractions;
using Aqorin.Phone.Core.Dialing;
using Aqorin.Phone.Core.Model;
using Aqorin.Phone.Core.StateMachines;
using Aqorin.Phone.Sip.Internal;
using Microsoft.Extensions.Logging;

namespace Aqorin.Phone.Sip;

/// <summary>
/// Single-call service. Owns one <see cref="ISipUserAgent"/> per open transport, enforces the
/// <see cref="CallStateMachine"/> and turns stack events into <see cref="CallInfo"/> snapshots.
/// </summary>
public sealed class SipCallService : ICallService
{
    /// <summary>Outgoing calls are cancelled automatically after this many seconds of ringing.</summary>
    public const int DefaultRingTimeoutSeconds = 90;

    private readonly ISipUserAgentFactory _agentFactory;
    private readonly IAudioMediaSessionFactory _mediaFactory;
    private readonly SipSessionContext _context;
    private readonly ILogger<SipCallService> _logger;
    private readonly object _gate = new();

    // Snapshots are published from a single pump task so subscribers see them strictly in order and off the lock.
    private readonly Channel<CallInfo> _events = Channel.CreateUnbounded<CallInfo>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _pump;

    private CallInfo _call = CallInfo.Idle;
    private ISipUserAgent? _agent;
    private ISipIncomingCall? _incoming;
    private IAudioMediaSession? _media;
    private SipCallFailure? _lastFailure;
    private CallEndReason _pendingLocalReason;
    private bool _isMuted;
    private bool _isOnHold;
    private bool _isSpeakerEnabled;
    private bool _disposed;
    private AudioMediaSessionOptions _mediaOptions = new();

    internal SipCallService(ISipUserAgentFactory agentFactory, IAudioMediaSessionFactory mediaFactory, SipSessionContext context, ILogger<SipCallService> logger)
    {
        _agentFactory = agentFactory;
        _mediaFactory = mediaFactory;
        _context = context;
        _logger = logger;
        _pump = Task.Run(PumpEventsAsync);
        _context.TransportOpened += OnTransportOpened;
        _context.TransportClosing += OnTransportClosing;
        if (_context.Transport is { } existing)
        {
            OnTransportOpened(existing);
        }
    }

    private async Task PumpEventsAsync()
    {
        await foreach (var info in _events.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                CallChanged?.Invoke(this, info);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "A CallChanged handler threw.");
            }
        }
    }

    public CallInfo CurrentCall
    {
        get
        {
            lock (_gate)
            {
                return _call;
            }
        }
    }

    public event EventHandler<CallInfo>? CallChanged;

    public int RingTimeoutSeconds { get; set; } = DefaultRingTimeoutSeconds;

    public AudioMediaSessionOptions MediaOptions
    {
        get
        {
            lock (_gate)
            {
                return _mediaOptions;
            }
        }

        set
        {
            lock (_gate)
            {
                _mediaOptions = value;
            }
        }
    }

    public bool IsMuted
    {
        get
        {
            lock (_gate)
            {
                return _isMuted;
            }
        }
    }

    public bool IsOnHold
    {
        get
        {
            lock (_gate)
            {
                return _isOnHold;
            }
        }
    }

    public bool IsSpeakerEnabled
    {
        get
        {
            lock (_gate)
            {
                return _isSpeakerEnabled;
            }
        }
    }

    public async Task<CallInfo> PlaceCallAsync(string destination, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var account = _context.Account;
        var agent = _agent;
        if (account is null || agent is null || !_context.IsRegistered)
        {
            throw new InvalidOperationException("You must be registered with a SIP registrar before placing a call.");
        }

        var target = DialPlan.Normalize(destination, account.Settings);

        IAudioMediaSession media;
        lock (_gate)
        {
            if (!CallStateMachine.CanPlaceCall(_call.State))
            {
                throw new InvalidCallOperationException("place a call", _call.State);
            }

            _lastFailure = null;
            _pendingLocalReason = CallEndReason.None;
            media = _mediaFactory.Create(EffectiveMediaOptionsLocked());
            _media = media;
            Transition(new CallInfo
            {
                CallId = Guid.NewGuid().ToString("N"),
                State = CallState.Dialing,
                Direction = CallDirection.Outgoing,
                RemoteParty = target.DisplayNumber,
                RemoteUri = target.SipUri,
                StartedAt = DateTimeOffset.UtcNow,
                Message = $"Calling {target.DisplayNumber}…",
            });
        }

        HookMedia(media);
        await ApplyCurrentMediaControlsAsync(media).ConfigureAwait(false);

        using var registration = cancellationToken.Register(() =>
        {
            _logger.LogInformation("Outgoing call cancelled by caller token.");
            RequestLocalEnd(CallEndReason.LocalCancel);
        });

        bool answered;
        try
        {
            answered = await agent.CallAsync(target.SipUri, account, media, RingTimeoutSeconds).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Placing the call failed.");
            Finish(CallState.Failed, CallEndReason.Error, "Call failed", ex.Message);
            return CurrentCall;
        }

        lock (_gate)
        {
            if (answered)
            {
                if (_call.State is CallState.Dialing or CallState.Ringing or CallState.Connecting)
                {
                    Transition(_call with
                    {
                        State = CallState.Active,
                        ConnectedAt = _call.ConnectedAt ?? DateTimeOffset.UtcNow,
                        Message = "Connected",
                        Codec = media.NegotiatedCodec ?? _call.Codec,
                    });
                }
            }
            else if (_call.State is CallState.Dialing or CallState.Ringing or CallState.Connecting or CallState.Ending)
            {
                if (_pendingLocalReason != CallEndReason.None || _call.State == CallState.Ending)
                {
                    var reason = _pendingLocalReason == CallEndReason.None ? CallEndReason.LocalCancel : _pendingLocalReason;
                    FinishLocked(CallState.Idle, reason, "Call cancelled", _lastFailure?.Message);
                }
                else
                {
                    var failure = _lastFailure;
                    var (reason, message) = SipResponseMapper.MapFailure(failure?.StatusCode, failure?.ReasonPhrase, failure?.Message);
                    var detail = failure is null ? null : failure.StatusCode is { } code ? $"{code} {failure.ReasonPhrase}".Trim() : failure.Message;
                    FinishLocked(CallState.Failed, reason, message, detail);
                }
            }
        }

        return CurrentCall;
    }

    public async Task AnswerAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ISipIncomingCall incoming;
        IAudioMediaSession media;
        lock (_gate)
        {
            if (!CallStateMachine.CanAnswer(_call.State) || _incoming is null)
            {
                throw new InvalidCallOperationException("answer", _call.State);
            }

            incoming = _incoming;
            media = _mediaFactory.Create(EffectiveMediaOptionsLocked());
            _media = media;
            Transition(_call with { State = CallState.Connecting, Message = "Answering…" });
        }

        HookMedia(media);
        await ApplyCurrentMediaControlsAsync(media).ConfigureAwait(false);
        bool ok;
        try
        {
            ok = await incoming.AnswerAsync(media).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Answering the call failed.");
            ok = false;
        }

        lock (_gate)
        {
            if (!ReferenceEquals(_incoming, incoming))
            {
                return;
            }

            if (ok && _call.State == CallState.Connecting)
            {
                Transition(_call with
                {
                    State = CallState.Active,
                    ConnectedAt = DateTimeOffset.UtcNow,
                    Message = "Connected",
                    Codec = media.NegotiatedCodec,
                });
            }
            else if (!ok && _call.State is CallState.Connecting or CallState.Incoming)
            {
                FinishLocked(CallState.Failed, CallEndReason.MediaFailure, "Could not answer the call", "The call was cancelled or the media session could not be started.");
            }
        }
    }

    public Task RejectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ISipIncomingCall incoming;
        lock (_gate)
        {
            if (!CallStateMachine.CanReject(_call.State) || _incoming is null)
            {
                throw new InvalidCallOperationException("reject", _call.State);
            }

            incoming = _incoming;
            Transition(_call with { State = CallState.Ending, Message = "Rejecting…" });
        }

        try
        {
            incoming.Reject(486, "Busy Here");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Sending the rejection failed.");
        }

        Finish(CallState.Idle, CallEndReason.LocalReject, "Call rejected", null);
        return Task.CompletedTask;
    }

    public Task HangupAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (!CallStateMachine.CanHangup(_call.State))
            {
                throw new InvalidCallOperationException("hang up", _call.State);
            }
        }

        var reason = CurrentCall.State is CallState.Dialing or CallState.Ringing ? CallEndReason.LocalCancel : CallEndReason.LocalHangup;
        RequestLocalEnd(reason);
        return Task.CompletedTask;
    }

    public async Task SetMutedAsync(bool muted, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        IAudioMediaSession? media;
        lock (_gate)
        {
            _isMuted = muted;
            media = _media;
        }

        if (media is not null)
        {
            await media.SetMutedAsync(muted).WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        PublishCurrent();
    }

    public async Task SetHoldAsync(bool held, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        IAudioMediaSession? media;
        lock (_gate)
        {
            _isOnHold = held;
            media = _media;
        }

        if (media is not null)
        {
            await media.SetHeldAsync(held).WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        PublishCurrent();
    }

    public async Task SetSpeakerAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        IAudioMediaSession? media;
        string? outputDeviceId;
        lock (_gate)
        {
            _isSpeakerEnabled = enabled;
            media = _media;
            outputDeviceId = EffectiveMediaOptionsLocked().OutputDeviceId;
        }

        if (media is not null)
        {
            await media.SetOutputDeviceAsync(outputDeviceId).WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        PublishCurrent();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _context.TransportOpened -= OnTransportOpened;
        _context.TransportClosing -= OnTransportClosing;
        if (CurrentCall.IsInProgress)
        {
            try
            {
                RequestLocalEnd(CallEndReason.LocalHangup);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Hang-up during dispose failed.");
            }
        }

        DetachAgent();
        var media = Interlocked.Exchange(ref _media, null);
        if (media is not null)
        {
            await media.DisposeAsync().ConfigureAwait(false);
        }

        _events.Writer.TryComplete();
        await Task.WhenAny(_pump, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------------------------------------------------------

    private void RequestLocalEnd(CallEndReason reason)
    {
        ISipUserAgent? agent;
        ISipIncomingCall? incoming;
        CallState state;
        lock (_gate)
        {
            state = _call.State;
            if (!CallStateMachine.CanHangup(state) && state != CallState.Incoming)
            {
                return;
            }

            agent = _agent;
            incoming = _incoming;
            _pendingLocalReason = reason;
            Transition(_call with { State = CallState.Ending, Message = reason == CallEndReason.LocalCancel ? "Cancelling…" : "Hanging up…" });
        }

        try
        {
            switch (state)
            {
                case CallState.Dialing:
                case CallState.Ringing:
                    agent?.Cancel();
                    // The CallAsync continuation finishes the call.
                    return;
                case CallState.Incoming:
                    incoming?.Reject(486, "Busy Here");
                    break;
                default:
                    agent?.Hangup();
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Hang-up signalling failed.");
        }

        Finish(CallState.Idle, reason, reason == CallEndReason.LocalCancel ? "Call cancelled" : "Call ended", null);
    }

    private void OnTransportOpened(ISipTransportHandle transport)
    {
        DetachAgent();
        try
        {
            var agent = _agentFactory.Create(transport);
            agent.IncomingCall += OnIncomingCall;
            agent.Ringing += OnRinging;
            agent.Answered += OnAnswered;
            agent.Failed += OnFailed;
            agent.Hungup += OnHungup;
            _agent = agent;
            _logger.LogDebug("Call agent attached to transport {Transport}.", transport.Description);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not create the SIP user agent.");
        }
    }

    private void OnTransportClosing(ISipTransportHandle transport)
    {
        if (CurrentCall.IsInProgress)
        {
            _logger.LogInformation("Transport closing while a call is in progress; ending the call.");
            RequestLocalEnd(CallEndReason.LocalHangup);
            Finish(CallState.Idle, CallEndReason.LocalHangup, "Call ended (unregistered)", null);
        }

        DetachAgent();
    }

    private void DetachAgent()
    {
        var agent = Interlocked.Exchange(ref _agent, null);
        if (agent is null)
        {
            return;
        }

        agent.IncomingCall -= OnIncomingCall;
        agent.Ringing -= OnRinging;
        agent.Answered -= OnAnswered;
        agent.Failed -= OnFailed;
        agent.Hungup -= OnHungup;
        agent.Dispose();
    }

    private void OnIncomingCall(ISipIncomingCall call)
    {
        _logger.LogInformation("Incoming SIP call {CallId} from {From}.", call.CallId, call.FromUri);
        var agent = _agent;
        lock (_gate)
        {
            if (!CallStateMachine.CanReceiveCall(_call.State))
            {
                _logger.LogInformation("Incoming call from {From} rejected busy: another call is {State}.", call.FromUri, _call.State);
                try
                {
                    agent?.RejectBusy(call);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Busy rejection failed.");
                }

                return;
            }

            _incoming = call;
            _lastFailure = null;
            _pendingLocalReason = CallEndReason.None;
            var remote = string.IsNullOrWhiteSpace(call.FromDisplayName) ? DialPlan.UserPart(call.FromUri) : call.FromDisplayName!;
            Transition(new CallInfo
            {
                CallId = call.CallId,
                State = CallState.Incoming,
                Direction = CallDirection.Incoming,
                RemoteParty = remote,
                RemoteUri = call.FromUri,
                StartedAt = DateTimeOffset.UtcNow,
                Message = $"Incoming call from {remote}",
            });
        }

        call.Cancelled += () =>
        {
            lock (_gate)
            {
                if (ReferenceEquals(_incoming, call) && _call.State is CallState.Incoming or CallState.Connecting)
                {
                    FinishLocked(CallState.Idle, CallEndReason.RemoteCancel, "Caller hung up before answer", null);
                }
            }
        };

        try
        {
            call.StartRinging();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not signal ringing for the incoming call.");
            Finish(CallState.Failed, CallEndReason.Error, "Incoming call failed", ex.Message);
        }
    }

    private void OnRinging()
    {
        lock (_gate)
        {
            if (_call.State == CallState.Dialing)
            {
                Transition(_call with { State = CallState.Ringing, Message = $"Ringing {_call.RemoteParty}…" });
            }
        }
    }

    private void OnAnswered()
    {
        lock (_gate)
        {
            if (_call.State is CallState.Dialing or CallState.Ringing)
            {
                Transition(_call with { State = CallState.Connecting, ConnectedAt = DateTimeOffset.UtcNow, Message = "Connecting…" });
            }
        }
    }

    private void OnFailed(SipCallFailure failure)
    {
        lock (_gate)
        {
            _lastFailure = failure;
        }

        _logger.LogInformation("Call failure reported by the stack: {Status} {Reason} {Message}", failure.StatusCode, failure.ReasonPhrase, failure.Message);
    }

    private void OnHungup()
    {
        lock (_gate)
        {
            switch (_call.State)
            {
                case CallState.Active:
                case CallState.Connecting:
                    FinishLocked(CallState.Idle, CallEndReason.RemoteHangup, "The other party hung up", null);
                    break;
                case CallState.Ending:
                    var reason = _pendingLocalReason == CallEndReason.None ? CallEndReason.LocalHangup : _pendingLocalReason;
                    FinishLocked(CallState.Idle, reason, "Call ended", null);
                    break;
            }
        }
    }

    private void HookMedia(IAudioMediaSession media)
    {
        media.AudioError += message =>
        {
            _logger.LogWarning("Audio problem during the call: {Message}", message);
            lock (_gate)
            {
                if (_call.IsInProgress)
                {
                    Transition(_call with { Detail = "Audio: " + message });
                }
            }
        };
        media.CodecNegotiated += codec =>
        {
            lock (_gate)
            {
                if (_call.IsInProgress)
                {
                    Transition(_call with { Codec = codec });
                }
            }
        };
    }

    private void Finish(CallState final, CallEndReason reason, string message, string? detail)
    {
        lock (_gate)
        {
            FinishLocked(final, reason, message, detail);
        }
    }

    /// <summary>Caller holds <see cref="_gate"/>.</summary>
    private void FinishLocked(CallState final, CallEndReason reason, string message, string? detail)
    {
        if (_call.State is CallState.Idle or CallState.Failed && _call.EndedAt is not null)
        {
            return;
        }

        if (!CallStateMachine.CanTransition(_call.State, final))
        {
            final = CallStateMachine.CanTransition(_call.State, CallState.Idle) ? CallState.Idle : _call.State;
        }

        _incoming = null;
        _isOnHold = false;
        _isMuted = false;
        var media = _media;
        _media = null;
        Transition(_call with
        {
            State = final,
            EndReason = reason,
            EndedAt = DateTimeOffset.UtcNow,
            Message = message,
            Detail = detail ?? _call.Detail,
        });

        if (media is not null)
        {
            // Off the caller's thread: FinishLocked may run on SIPSorcery's transport thread (remote BYE) or the UI thread.
            _ = Task.Run(() => CloseMediaAsync(media, reason.ToString()));
        }
    }

    private async Task CloseMediaAsync(IAudioMediaSession media, string reason)
    {
        try
        {
            await media.CloseAsync(reason).ConfigureAwait(false);
            await media.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Closing the media session failed.");
        }
    }

    /// <summary>Caller holds <see cref="_gate"/>. Validates and publishes a new snapshot.</summary>
    private void Transition(CallInfo next)
    {
        if (next.State != _call.State)
        {
            CallStateMachine.EnsureTransition(_call.State, next.State);
            _logger.LogInformation("Call {CallId}: {From} -> {To} ({Message})", next.CallId, _call.State, next.State, next.Message);
        }

        _call = next;
        _context.CallInProgress = next.IsInProgress;
        _events.Writer.TryWrite(next);
    }

    private async Task ApplyCurrentMediaControlsAsync(IAudioMediaSession media)
    {
        bool muted;
        bool held;
        string? outputDeviceId;
        lock (_gate)
        {
            muted = _isMuted;
            held = _isOnHold;
            outputDeviceId = EffectiveMediaOptionsLocked().OutputDeviceId;
        }

        await media.SetOutputDeviceAsync(outputDeviceId).ConfigureAwait(false);

        if (muted)
        {
            await media.SetMutedAsync(true).ConfigureAwait(false);
        }

        if (held)
        {
            await media.SetHeldAsync(true).ConfigureAwait(false);
        }
    }

    private AudioMediaSessionOptions EffectiveMediaOptionsLocked() =>
        _isSpeakerEnabled ? _mediaOptions with { OutputDeviceId = null } : _mediaOptions;

    private void PublishCurrent()
    {
        lock (_gate)
        {
            if (_call.IsInProgress)
            {
                _events.Writer.TryWrite(_call);
            }
        }
    }
}
