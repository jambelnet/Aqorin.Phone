using System.Net;
using Aqorin.Phone.Core.Model;
using Microsoft.Extensions.Logging;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;

namespace Aqorin.Phone.Sip.Internal;

/// <summary>
/// Thin adapter over <see cref="SIPRegistrationUserAgent"/>. The SIPSorcery agent handles the digest challenge and
/// periodic refresh; retry-after-failure is deliberately left to <see cref="SipRegistrationService"/> (bounded backoff).
/// </summary>
internal sealed class SipSorceryRegistrationClient : ISipRegistrationClient
{
    // Refresh well before the registration expires (at ~85% of the granted interval, at least 10 s before).
    internal static int RefreshSeconds(long expiry) => (int)Math.Max(10, expiry - Math.Max(10, (long)(expiry * 0.15)));

    private readonly SIPRegistrationUserAgent _agent;
    private readonly ILogger _logger;
    private readonly string _aor;
    private readonly SipWireSummary? _wire;
    private bool _disposed;

    public SipSorceryRegistrationClient(SIPTransport transport, SipAccount account, TimeSpan attemptTimeout, ILogger logger, SipWireSummary? wire = null, SIPEndPoint? outboundProxy = null, IPEndPoint? localContactEndPoint = null)
    {
        _logger = logger;
        _wire = wire;
        var settings = account.Settings;
        var protocol = settings.Transport == SipTransport.Tcp ? SIPProtocolsEnum.tcp : SIPProtocolsEnum.udp;
        var aor = new SIPURI(settings.Username.Trim(), settings.HostPort, null, SIPSchemesEnum.sip, protocol);
        _aor = aor.ToAOR();
        var contact = localContactEndPoint is null
            ? new SIPURI(SIPSchemesEnum.sip, IPAddress.Any, 0)
            : new SIPURI(SIPSchemesEnum.sip, localContactEndPoint.Address, localContactEndPoint.Port);
        contact.User = settings.Username.Trim();
        if (protocol == SIPProtocolsEnum.tcp)
        {
            contact.Protocol = SIPProtocolsEnum.tcp;
        }

        var registrarHost = protocol == SIPProtocolsEnum.tcp ? $"{settings.HostPort};transport=tcp" : settings.HostPort;

        _agent = new SIPRegistrationUserAgent(
            transport,
            outboundProxy: outboundProxy,
            sipAccountAOR: aor,
            authUsername: settings.Username.Trim(),
            password: account.Password,
            realm: null,
            registrarHost: registrarHost,
            contactURI: contact,
            expiry: settings.RegistrationExpirySeconds,
            customHeaders: null,
            maxRegistrationAttemptTimeout: Math.Max(5, (int)attemptTimeout.TotalSeconds),
            // Failure retries are owned by SipRegistrationService; make the built-in interval effectively inert.
            registerFailureRetryInterval: 3600,
            maxRegisterAttempts: 3,
            exitOnUnequivocalFailure: true)
        {
            UserAgent = "Aqorin.Phone/0.1.1",
            UserDisplayName = string.IsNullOrWhiteSpace(settings.DisplayName) ? null : settings.DisplayName.Trim(),
            AdjustRefreshTime = RefreshSeconds,
        };

        _agent.RegistrationSuccessful += OnSuccessful;
        _agent.RegistrationFailed += OnFailed;
        _agent.RegistrationTemporaryFailure += OnTemporaryFailure;
        _agent.RegistrationRemoved += OnRemoved;
    }

    public event Action<RegistrationEvent>? Completed;

    public event Action? Removed;

    public bool IsRegistered => _agent.IsRegistered;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _agent.Start();
    }

    public void Stop(bool sendUnregister)
    {
        if (_disposed)
        {
            return;
        }

        _agent.Stop(sendUnregister);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _agent.RegistrationSuccessful -= OnSuccessful;
        _agent.RegistrationFailed -= OnFailed;
        _agent.RegistrationTemporaryFailure -= OnTemporaryFailure;
        _agent.RegistrationRemoved -= OnRemoved;
        try
        {
            _agent.Stop(sendZeroExpiryRegister: false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Stopping registration agent for {Aor} failed.", _aor);
        }
    }

    private void OnSuccessful(SIPURI uri, SIPResponse response)
    {
        var expiry = ExpiryFrom(response);
        _logger.LogInformation("REGISTER succeeded for {Aor} (expires in {Expiry}s).", _aor, expiry);
        Completed?.Invoke(new RegistrationEvent(RegistrationOutcome.Success, response?.ShortDescription, response?.StatusCode, expiry));
    }

    private void OnFailed(SIPURI uri, SIPResponse? response, string message)
    {
        var status = response?.StatusCode;
        var outcome = status switch
        {
            401 or 403 or 404 or 402 or 407 => RegistrationOutcome.AuthenticationFailure,
            _ when message?.Contains("resolve", StringComparison.OrdinalIgnoreCase) == true => RegistrationOutcome.ResolutionFailure,
            _ => RegistrationOutcome.TemporaryFailure,
        };
        _logger.LogWarning("REGISTER failed for {Aor}: {Message} ({Outcome}).", _aor, message, outcome);
        Completed?.Invoke(new RegistrationEvent(outcome, WithWireSummary(message), status, null));
    }

    private void OnTemporaryFailure(SIPURI uri, SIPResponse? response, string message)
    {
        _logger.LogWarning("REGISTER temporarily failed for {Aor}: {Message}.", _aor, message);
        Completed?.Invoke(new RegistrationEvent(RegistrationOutcome.TemporaryFailure, WithWireSummary(message), response?.StatusCode, null));
    }

    private void OnRemoved(SIPURI uri, SIPResponse response)
    {
        _logger.LogInformation("Registration removed for {Aor}.", _aor);
        Removed?.Invoke();
    }

    /// <summary>Appends what actually went over the wire so a "timed out" can be told apart from "challenged, then ignored".</summary>
    private string WithWireSummary(string? message) => _wire is null ? message ?? string.Empty : (message ?? string.Empty).TrimEnd() + " " + _wire.Describe();

    private static int? ExpiryFrom(SIPResponse? response)
    {
        if (response is null)
        {
            return null;
        }

        if (response.Header.Expires > 0)
        {
            return (int)Math.Min(response.Header.Expires, int.MaxValue);
        }

        var contact = response.Header.Contact?.FirstOrDefault();
        if (contact is not null && contact.Expires > 0)
        {
            return (int)Math.Min(contact.Expires, int.MaxValue);
        }

        return null;
    }
}

internal sealed class SipSorceryRegistrationClientFactory(ILogger<SipSorceryRegistrationClient> logger) : ISipRegistrationClientFactory
{
    public ISipRegistrationClient Create(ISipTransportHandle transport, SipAccount account, TimeSpan attemptTimeout)
    {
        var handle = transport as SipSorceryTransportHandle
            ?? throw new ArgumentException("Transport handle was not created by the SIPSorcery transport factory.", nameof(transport));
        return new SipSorceryRegistrationClient(handle.Transport, account, attemptTimeout, logger, handle.Wire, handle.OutboundProxy, handle.LocalContactEndPoint);
    }
}
