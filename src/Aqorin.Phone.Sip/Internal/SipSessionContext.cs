using Aqorin.Phone.Core.Model;

namespace Aqorin.Phone.Sip.Internal;

/// <summary>
/// Shared state between the registration service (which owns the transport) and the call service
/// (which needs the transport to send INVITEs and to receive incoming calls).
/// </summary>
internal sealed class SipSessionContext
{
    private readonly object _gate = new();
    private ISipTransportHandle? _transport;
    private SipAccount? _account;
    private volatile bool _isRegistered;

    /// <summary>Raised right after a transport was opened for a registration attempt.</summary>
    public event Action<ISipTransportHandle>? TransportOpened;

    /// <summary>Raised right before the transport is disposed. Handlers must release everything bound to it synchronously.</summary>
    public event Action<ISipTransportHandle>? TransportClosing;

    public ISipTransportHandle? Transport
    {
        get
        {
            lock (_gate)
            {
                return _transport;
            }
        }
    }

    public SipAccount? Account
    {
        get
        {
            lock (_gate)
            {
                return _account;
            }
        }
    }

    public bool IsRegistered
    {
        get => _isRegistered;
        set => _isRegistered = value;
    }

    private volatile bool _callInProgress;

    /// <summary>Maintained by the call service; the registration service must not recycle the transport while true.</summary>
    public bool CallInProgress
    {
        get => _callInProgress;
        set => _callInProgress = value;
    }

    /// <summary>
    /// Replaces the transport (new socket) without changing the account. Used by the registration retry to recover from a
    /// transport that stopped receiving. Raises <see cref="TransportClosing"/> for the old and <see cref="TransportOpened"/> for the new one.
    /// </summary>
    public void ReplaceTransport(ISipTransportHandle transport)
    {
        ISipTransportHandle? old;
        SipAccount? account;
        lock (_gate)
        {
            old = _transport;
            account = _account;
            _transport = transport;
        }

        if (old is not null)
        {
            try
            {
                TransportClosing?.Invoke(old);
            }
            finally
            {
                old.Dispose();
            }
        }

        if (account is not null)
        {
            TransportOpened?.Invoke(transport);
        }
    }

    public void Open(ISipTransportHandle transport, SipAccount account)
    {
        lock (_gate)
        {
            _transport = transport;
            _account = account;
        }

        TransportOpened?.Invoke(transport);
    }

    public void Close()
    {
        ISipTransportHandle? transport;
        lock (_gate)
        {
            transport = _transport;
            _transport = null;
            _account = null;
            _isRegistered = false;
        }

        if (transport is null)
        {
            return;
        }

        try
        {
            TransportClosing?.Invoke(transport);
        }
        finally
        {
            transport.Dispose();
        }
    }
}
