namespace Aqorin.Phone.Core.Model;

/// <summary>Registration lifecycle with the configured SIP registrar.</summary>
public enum RegistrationState
{
    Disconnected,
    Registering,
    Registered,
    Unregistering,
    Failed
}

/// <summary>Explicit call lifecycle. Only one call exists at a time in this MVP.</summary>
public enum CallState
{
    /// <summary>No call. Also the resting state after a call ended normally.</summary>
    Idle,
    /// <summary>Outgoing INVITE sent, waiting for a provisional or final response.</summary>
    Dialing,
    /// <summary>Outgoing call: the remote side is ringing (180/183 received).</summary>
    Ringing,
    /// <summary>Incoming INVITE received and the local phone is ringing; waiting for answer/reject.</summary>
    Incoming,
    /// <summary>Call was answered (in either direction); media is being set up.</summary>
    Connecting,
    /// <summary>Two-way audio established.</summary>
    Active,
    /// <summary>Local hang-up / cancel / reject in progress.</summary>
    Ending,
    /// <summary>The call could not be completed (busy, rejected, timeout, error). Resting state like Idle.</summary>
    Failed
}

public enum CallDirection
{
    Outgoing,
    Incoming
}

public enum SipTransport
{
    Udp,
    Tcp
}

/// <summary>Why a call left the Dialing/Ringing/Incoming/Connecting/Active states.</summary>
public enum CallEndReason
{
    None,
    LocalHangup,
    LocalCancel,
    LocalReject,
    RemoteHangup,
    RemoteCancel,
    Busy,
    Rejected,
    NotFound,
    Timeout,
    Unauthorized,
    MediaFailure,
    TransportFailure,
    Error
}
