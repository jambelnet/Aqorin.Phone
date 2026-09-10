using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using SIPSorcery.SIP;

namespace Aqorin.Phone.Sip.Tests.Loopback;

internal enum InviteBehaviour
{
    /// <summary>100 Trying, 180 Ringing, then 486 Busy Here.</summary>
    RingThenBusy,
    /// <summary>100 Trying, 180 Ringing, then 200 OK with a G.711 SDP answer; BYE from either side ends the dialog.</summary>
    RingThenAnswer,
}

/// <summary>
/// A tiny SIP registrar/UAS on 127.0.0.1 that mimics the FRITZ!Box behaviour the softphone depends on:
/// digest-challenges REGISTER and INVITE (401), answers REGISTER with the granted expiry, rings and then answers or
/// rejects INVITEs, handles BYE, can hang up an established call (BYE) and can place an incoming INVITE to the
/// registered contact. Built on SIPSorcery's own transport so the tests exercise the production signalling code
/// end-to-end without a physical FRITZ!Box or network access beyond loopback.
/// </summary>
internal sealed class FakeFritzBox : IAsyncDisposable
{
    private readonly SIPTransport _transport = new();
    private readonly SIPUDPChannel _channel;
    private readonly ConcurrentDictionary<string, byte> _nonces = new();
    private readonly object _dialogGate = new();
    private Dialog? _dialog;

    private FakeFritzBox()
    {
        _channel = new SIPUDPChannel(new IPEndPoint(IPAddress.Loopback, 0));
        _transport.AddSIPChannel(_channel);
        _transport.SIPTransportRequestReceived += OnRequest;
    }

    public static FakeFritzBox Start() => new();

    public int Port => _channel.ListeningSIPEndPoint.Port;

    public string Host => "127.0.0.1";

    public string Realm { get; set; } = "fritz.box";

    public string Username { get; set; } = "620";

    public string Password { get; set; } = "s3cret-box-pw";

    public int GrantedExpiry { get; set; } = 120;

    public InviteBehaviour Behaviour { get; set; } = InviteBehaviour.RingThenBusy;

    /// <summary>Ring for this long before sending the final response to an INVITE.</summary>
    public TimeSpan RingDuration { get; set; } = TimeSpan.FromMilliseconds(300);

    public ConcurrentQueue<string> Log { get; } = new();

    public int RegisterRequests => _registerRequests;
    private int _registerRequests;

    /// <summary>All INVITEs including the unauthenticated ones that were challenged.</summary>
    public int InviteRequests => _inviteRequests;
    private int _inviteRequests;

    /// <summary>Authenticated INVITEs that were actually processed (rung).</summary>
    public int AcceptedInvites => _acceptedInvites;
    private int _acceptedInvites;

    public int AcksReceived => _acks;
    private int _acks;

    public int ByesReceived => _byes;
    private int _byes;

    public SIPURI? RegisteredContact { get; private set; }

    /// <summary>True while a call answered by the box is established (200 OK sent, no BYE yet).</summary>
    public bool HasEstablishedCall
    {
        get
        {
            lock (_dialogGate)
            {
                return _dialog is not null;
            }
        }
    }

    public TaskCompletionSource Registered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Unregistered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource<SIPRequest> InviteReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource<int> OutgoingInviteRinging { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource<int> OutgoingInviteFinalStatus { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Sends an INVITE (with a G.711 SDP offer) to the registered contact, i.e. an incoming call for the softphone.</summary>
    public void CallSoftphone(string callerNumber = "0301234", string callerName = "Caller")
    {
        var contact = RegisteredContact ?? throw new InvalidOperationException("The softphone has not registered yet.");
        var from = new SIPFromHeader(callerName, new SIPURI(callerNumber, Realm, null), CallProperties.CreateNewTag());
        var to = new SIPToHeader(null, contact, null);
        var invite = SIPRequest.GetRequest(SIPMethodsEnum.INVITE, contact, to, from);
        invite.Header.Contact = SIPContactHeader.CreateSIPContactList(new SIPURI(SIPSchemesEnum.sip, _channel.ListeningSIPEndPoint));
        invite.Header.ContentType = "application/sdp";
        invite.Body = Sdp(40000);

        var uac = new UACInviteTransaction(_transport, invite, null);
        uac.UACInviteTransactionInformationResponseReceived += (_, _, _, resp) =>
        {
            Log.Enqueue($"<- {resp.StatusCode} {resp.ReasonPhrase} (provisional)");
            if (resp.StatusCode == 180)
            {
                OutgoingInviteRinging.TrySetResult(resp.StatusCode);
            }

            return Task.FromResult(SocketError.Success);
        };
        uac.UACInviteTransactionFinalResponseReceived += (_, _, _, resp) =>
        {
            Log.Enqueue($"<- {resp.StatusCode} {resp.ReasonPhrase} (final)");
            OutgoingInviteFinalStatus.TrySetResult(resp.StatusCode);
            return Task.FromResult(SocketError.Success);
        };
        uac.UACInviteTransactionFailed += (_, error) => OutgoingInviteFinalStatus.TrySetException(new IOException(error.ToString()));
        uac.SendInviteRequest();
    }

    /// <summary>Ends the call the box answered by sending BYE to the softphone (remote hang-up).</summary>
    public void HangUpSoftphone()
    {
        Dialog dialog;
        lock (_dialogGate)
        {
            dialog = _dialog ?? throw new InvalidOperationException("No established call to hang up.");
            _dialog = null;
        }

        var bye = SIPRequest.GetRequest(
            SIPMethodsEnum.BYE,
            dialog.RemoteTarget,
            new SIPToHeader(null, dialog.RemoteUri, dialog.RemoteTag),
            new SIPFromHeader(null, dialog.LocalUri, dialog.LocalTag));
        bye.Header.CallId = dialog.CallId;
        bye.Header.CSeq = dialog.LocalCSeq++;
        Log.Enqueue("-> BYE (box hangs up)");
        var tx = new SIPNonInviteTransaction(_transport, bye, null);
        tx.NonInviteTransactionFinalResponseReceived += (_, _, _, resp) =>
        {
            Log.Enqueue($"<- {resp.StatusCode} {resp.ReasonPhrase} (to BYE)");
            return Task.FromResult(SocketError.Success);
        };
        tx.SendRequest();
    }

    public ValueTask DisposeAsync()
    {
        _transport.SIPTransportRequestReceived -= OnRequest;
        _transport.Shutdown();
        _transport.Dispose();
        return ValueTask.CompletedTask;
    }

    private static string Sdp(int port) =>
        "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=-\r\nc=IN IP4 127.0.0.1\r\nt=0 0\r\n" +
        $"m=audio {port} RTP/AVP 8 0\r\na=rtpmap:8 PCMA/8000\r\na=rtpmap:0 PCMU/8000\r\na=sendrecv\r\n";

    private async Task OnRequest(SIPEndPoint local, SIPEndPoint remote, SIPRequest request)
    {
        Log.Enqueue($"-> {request.Method} {request.URI} auth={request.Header.HasAuthenticationHeader}");
        try
        {
            switch (request.Method)
            {
                case SIPMethodsEnum.REGISTER:
                    Interlocked.Increment(ref _registerRequests);
                    await HandleRegister(request).ConfigureAwait(false);
                    break;
                case SIPMethodsEnum.INVITE:
                    Interlocked.Increment(ref _inviteRequests);
                    await HandleInvite(request).ConfigureAwait(false);
                    break;
                case SIPMethodsEnum.ACK:
                    Interlocked.Increment(ref _acks);
                    break;
                case SIPMethodsEnum.BYE:
                    Interlocked.Increment(ref _byes);
                    lock (_dialogGate)
                    {
                        if (_dialog is not null && _dialog.CallId == request.Header.CallId)
                        {
                            _dialog = null;
                        }
                    }

                    await _transport.SendResponseAsync(SIPResponse.GetResponse(request, SIPResponseStatusCodesEnum.Ok, null)).ConfigureAwait(false);
                    break;
                default:
                    await _transport.SendResponseAsync(SIPResponse.GetResponse(request, SIPResponseStatusCodesEnum.MethodNotAllowed, null)).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Enqueue("!! " + ex);
        }
    }

    private bool IsAuthenticated(SIPRequest request, out SIPResponse challenge)
    {
        var header = request.Header.AuthenticationHeaders?.FirstOrDefault();
        if (header is not null)
        {
            var digest = header.SIPDigest;
            var nonceKnown = _nonces.ContainsKey(digest.Nonce ?? string.Empty);
            var expected = new SIPAuthorisationDigest(SIPAuthorisationHeadersEnum.Authorize, Realm, Username, Password, digest.URI, digest.Nonce, request.Method.ToString()).GetDigest();
            if (nonceKnown && digest.Username == Username && string.Equals(expected, digest.Response, StringComparison.OrdinalIgnoreCase))
            {
                challenge = null!;
                return true;
            }

            Log.Enqueue("   digest mismatch or unknown nonce");
        }

        var nonce = Guid.NewGuid().ToString("N");
        _nonces[nonce] = 0;
        challenge = SIPResponse.GetResponse(request, SIPResponseStatusCodesEnum.Unauthorised, null);
        challenge.Header.AuthenticationHeaders.Add(new SIPAuthenticationHeader(SIPAuthorisationHeadersEnum.WWWAuthenticate, Realm, nonce));
        return false;
    }

    private async Task HandleRegister(SIPRequest request)
    {
        if (!IsAuthenticated(request, out var challenge))
        {
            await _transport.SendResponseAsync(challenge).ConfigureAwait(false);
            return;
        }

        var contact = request.Header.Contact?.FirstOrDefault();
        var requestedExpiry = contact is { Expires: >= 0 } c && c.Expires != -1 ? c.Expires : request.Header.Expires;
        var ok = SIPResponse.GetResponse(request, SIPResponseStatusCodesEnum.Ok, null);
        if (requestedExpiry == 0)
        {
            ok.Header.Expires = 0;
            RegisteredContact = null;
            Unregistered.TrySetResult();
        }
        else
        {
            ok.Header.Expires = GrantedExpiry;
            if (contact is not null)
            {
                RegisteredContact = contact.ContactURI.CopyOf();
                ok.Header.Contact = [new SIPContactHeader(contact.ContactName, contact.ContactURI.CopyOf()) { Expires = GrantedExpiry }];
            }

            Registered.TrySetResult();
        }

        await _transport.SendResponseAsync(ok).ConfigureAwait(false);
    }

    private async Task HandleInvite(SIPRequest request)
    {
        if (!IsAuthenticated(request, out var challenge))
        {
            // Like the FRITZ!Box: a final 401 via a transaction so the UAC acks it and retries with credentials.
            var challengeTx = new UASInviteTransaction(_transport, request, null);
            challengeTx.SendFinalResponse(challenge);
            return;
        }

        Interlocked.Increment(ref _acceptedInvites);
        InviteReceived.TrySetResult(request);
        var uas = new UASInviteTransaction(_transport, request, null);
        await uas.SendProvisionalResponse(SIPResponse.GetResponse(request, SIPResponseStatusCodesEnum.Trying, null)).ConfigureAwait(false);
        await uas.SendProvisionalResponse(SIPResponse.GetResponse(request, SIPResponseStatusCodesEnum.Ringing, null)).ConfigureAwait(false);
        await Task.Delay(RingDuration).ConfigureAwait(false);

        if (Behaviour == InviteBehaviour.RingThenBusy)
        {
            uas.SendFinalResponse(SIPResponse.GetResponse(request, SIPResponseStatusCodesEnum.BusyHere, "Busy Here"));
            return;
        }

        var ok = uas.GetOkResponse("application/sdp", Sdp(40002));
        ok.Header.Contact = SIPContactHeader.CreateSIPContactList(new SIPURI(SIPSchemesEnum.sip, _channel.ListeningSIPEndPoint));
        lock (_dialogGate)
        {
            _dialog = new Dialog
            {
                CallId = request.Header.CallId,
                RemoteTag = request.Header.From.FromTag,
                LocalTag = ok.Header.To.ToTag,
                RemoteUri = request.Header.From.FromURI.CopyOf(),
                LocalUri = request.Header.To.ToURI.CopyOf(),
                RemoteTarget = request.Header.Contact?.FirstOrDefault()?.ContactURI.CopyOf() ?? request.Header.From.FromURI.CopyOf(),
                LocalCSeq = 1,
            };
        }

        uas.SendFinalResponse(ok);
    }

    private sealed class Dialog
    {
        public required string CallId { get; init; }
        public required string RemoteTag { get; init; }
        public required string LocalTag { get; init; }
        public required SIPURI RemoteUri { get; init; }
        public required SIPURI LocalUri { get; init; }
        public required SIPURI RemoteTarget { get; init; }
        public int LocalCSeq { get; set; }
    }
}
