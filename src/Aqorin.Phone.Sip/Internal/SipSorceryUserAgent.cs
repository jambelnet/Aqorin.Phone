using Aqorin.Phone.Core.Abstractions;
using Aqorin.Phone.Core.Model;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;

namespace Aqorin.Phone.Sip.Internal;

/// <summary>Adapter over <see cref="SIPUserAgent"/> (non-exclusive transport: registration shares the socket).</summary>
internal sealed class SipSorceryUserAgent : ISipUserAgent
{
    private readonly SIPTransport _transport;
    private readonly SIPUserAgent _ua;
    private readonly ILogger _logger;
    private bool _disposed;

    public SipSorceryUserAgent(SIPTransport transport, ILogger logger, SIPEndPoint? outboundProxy = null)
    {
        _transport = transport;
        _logger = logger;
        _ua = new SIPUserAgent(transport, outboundProxy, isTransportExclusive: false);
        _ua.OnIncomingCall += OnIncomingCall;
        _ua.ClientCallRinging += OnClientRinging;
        _ua.ClientCallAnswered += OnClientAnswered;
        _ua.ClientCallFailed += OnClientFailed;
        _ua.OnCallHungup += OnCallHungup;
    }

    public event Action<ISipIncomingCall>? IncomingCall;
    public event Action? Ringing;
    public event Action? Answered;
    public event Action<SipCallFailure>? Failed;
    public event Action? Hungup;

    public bool IsCallActive => _ua.IsCallActive;

    public Task<bool> CallAsync(string destinationUri, SipAccount account, IAudioMediaSession mediaSession, int ringTimeoutSeconds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var provider = mediaSession as ISipSorceryMediaSessionProvider
            ?? throw new ArgumentException("The media session was not created by the SIP media session factory.", nameof(mediaSession));

        if (!SIPURI.TryParse(destinationUri, out var dstUri))
        {
            throw new InvalidDestinationException($"'{destinationUri}' is not a valid SIP URI.");
        }

        var settings = account.Settings;
        var username = settings.Username.Trim();
        var fromUri = new SIPURI(username, settings.Registrar.Trim(), null, SIPSchemesEnum.sip, dstUri.Protocol);
        var fromHeader = new SIPFromHeader(string.IsNullOrWhiteSpace(settings.DisplayName) ? null : settings.DisplayName.Trim(), fromUri, null).ToString();

        var descriptor = new SIPCallDescriptor(
            username,
            account.Password,
            dstUri.ToString(),
            fromHeader,
            dstUri.CanonicalAddress,
            routeSet: null,
            customHeaders: null,
            authUsername: null,
            SIPCallDirection.Out,
            SDP.SDP_MIME_CONTENTTYPE,
            content: null,
            mangleIPAddress: null);

        return _ua.Call(descriptor, provider.MediaSession, ringTimeoutSeconds);
    }

    public void Cancel()
    {
        if (!_disposed)
        {
            _ua.Cancel();
        }
    }

    public void Hangup()
    {
        if (!_disposed)
        {
            _ua.Hangup();
        }
    }

    public void RejectBusy(ISipIncomingCall call)
    {
        if (call is SipSorceryIncomingCall incoming)
        {
            incoming.RejectImmediately(SIPResponseStatusCodesEnum.BusyHere, "Busy Here");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _ua.OnIncomingCall -= OnIncomingCall;
        _ua.ClientCallRinging -= OnClientRinging;
        _ua.ClientCallAnswered -= OnClientAnswered;
        _ua.ClientCallFailed -= OnClientFailed;
        _ua.OnCallHungup -= OnCallHungup;
        try
        {
            _ua.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Disposing SIP user agent failed.");
        }
    }

    private void OnIncomingCall(SIPUserAgent ua, SIPRequest request)
    {
        var call = new SipSorceryIncomingCall(_ua, _transport, request, _logger);
        var handler = IncomingCall;
        if (handler is null)
        {
            _logger.LogWarning("Incoming call from {From} with no handler; rejecting.", call.FromUri);
            call.RejectImmediately(SIPResponseStatusCodesEnum.TemporarilyUnavailable, "Unavailable");
            return;
        }

        handler(call);
    }

    private void OnClientRinging(ISIPClientUserAgent uac, SIPResponse response) => Ringing?.Invoke();

    private void OnClientAnswered(ISIPClientUserAgent uac, SIPResponse response) => Answered?.Invoke();

    private void OnClientFailed(ISIPClientUserAgent uac, string errorMessage, SIPResponse? response) =>
        Failed?.Invoke(new SipCallFailure(response?.StatusCode, response?.ReasonPhrase, errorMessage));

    private void OnCallHungup(SIPDialogue dialogue) => Hungup?.Invoke();
}

internal sealed class SipSorceryIncomingCall : ISipIncomingCall
{
    private readonly SIPUserAgent _ua;
    private readonly SIPTransport _transport;
    private readonly SIPRequest _request;
    private readonly ILogger _logger;
    private SIPServerUserAgent? _uas;
    private bool _finalised;

    public SipSorceryIncomingCall(SIPUserAgent ua, SIPTransport transport, SIPRequest request, ILogger logger)
    {
        _ua = ua;
        _transport = transport;
        _request = request;
        _logger = logger;
        CallId = request.Header.CallId;
        FromUri = request.Header.From?.FromURI?.ToParameterlessString() ?? request.URI.ToString();
        FromDisplayName = string.IsNullOrWhiteSpace(request.Header.From?.FromName) ? null : request.Header.From!.FromName;
    }

    public string CallId { get; }

    public string FromUri { get; }

    public string? FromDisplayName { get; }

    public event Action? Cancelled;

    /// <summary>Sends 100 Trying + 180 Ringing. Must be called before <see cref="AnswerAsync"/>.</summary>
    public void StartRinging()
    {
        if (_uas is not null)
        {
            return;
        }

        _uas = _ua.AcceptCall(_request);
        _uas.CallCancelled += (_, _) =>
        {
            _finalised = true;
            Cancelled?.Invoke();
        };
    }

    public Task<bool> AnswerAsync(IAudioMediaSession mediaSession)
    {
        var provider = mediaSession as ISipSorceryMediaSessionProvider
            ?? throw new ArgumentException("The media session was not created by the SIP media session factory.", nameof(mediaSession));
        if (_uas is null)
        {
            StartRinging();
        }

        _finalised = true;
        return _ua.Answer(_uas!, provider.MediaSession);
    }

    public void Reject(int statusCode, string reasonPhrase)
    {
        if (_finalised)
        {
            return;
        }

        _finalised = true;
        var status = (SIPResponseStatusCodesEnum)statusCode;
        if (_uas is not null)
        {
            _uas.Reject(status, reasonPhrase);
        }
        else
        {
            RejectImmediately(status, reasonPhrase);
        }
    }

    internal void RejectImmediately(SIPResponseStatusCodesEnum status, string reasonPhrase)
    {
        _finalised = true;
        try
        {
            var transaction = new UASInviteTransaction(_transport, _request, null);
            transaction.SendFinalResponse(SIPResponse.GetResponse(_request, status, reasonPhrase));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send {Status} for incoming call {CallId}.", status, CallId);
        }
    }
}

internal sealed class SipSorceryUserAgentFactory(ILogger<SipSorceryUserAgent> logger) : ISipUserAgentFactory
{
    public ISipUserAgent Create(ISipTransportHandle transport)
    {
        var handle = transport as SipSorceryTransportHandle
            ?? throw new ArgumentException("Transport handle was not created by the SIPSorcery transport factory.", nameof(transport));
        return new SipSorceryUserAgent(handle.Transport, logger, handle.OutboundProxy);
    }
}
