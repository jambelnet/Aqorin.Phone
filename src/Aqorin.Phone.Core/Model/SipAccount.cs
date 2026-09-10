namespace Aqorin.Phone.Core.Model;

public enum SipRouterProfile
{
    GenericSip,
    FritzBox,
    Speedport,
    VodafoneStation,
    Livebox,
    Freebox,
    Custom
}

/// <summary>
/// Non-secret account settings. Safe to persist to disk and to log.
/// </summary>
public sealed record SipAccountSettings
{
    public const int DefaultPort = 5060;
    public const int DefaultRegistrationExpirySeconds = 300;
    public const int MinRegistrationExpirySeconds = 60;
    public const int MaxRegistrationExpirySeconds = 7200;

    /// <summary>Registrar host name or IP address. For many home routers this is the router's LAN host name or IP.</summary>
    public string Registrar { get; init; } = "fritz.box";

    public SipRouterProfile RouterProfile { get; init; } = SipRouterProfile.GenericSip;

    public int Port { get; init; } = DefaultPort;

    public SipTransport Transport { get; init; } = SipTransport.Udp;

    /// <summary>The SIP account user name, often an internal extension such as <c>620</c>.</summary>
    public string Username { get; init; } = string.Empty;

    /// <summary>Optional display name used in the From header.</summary>
    public string DisplayName { get; init; } = string.Empty;

    public int RegistrationExpirySeconds { get; init; } = DefaultRegistrationExpirySeconds;

    /// <summary>Local SIP listening port. 0 lets the operating system pick an ephemeral port.</summary>
    public int LocalPort { get; init; }

    /// <summary>Verbose SIP/RTP diagnostics (redacted) in the log panel.</summary>
    public bool DiagnosticLogging { get; init; }

    /// <summary>Preferred microphone device ID; null means the system default input.</summary>
    public string? InputDeviceId { get; init; }

    /// <summary>Preferred speaker device ID; null means the system default output.</summary>
    public string? OutputDeviceId { get; init; }

    /// <summary>Address of record without scheme, e.g. <c>620@fritz.box</c>.</summary>
    public string AddressOfRecord => $"{Username}@{Registrar}";

    /// <summary>Host with port, omitting the default SIP port.</summary>
    public string HostPort => Port == DefaultPort ? Registrar : $"{Registrar}:{Port}";
}

/// <summary>
/// Settings plus the in-memory password. Never persisted, never logged. <see cref="ToString"/> is redacted.
/// </summary>
public sealed record SipAccount(SipAccountSettings Settings, string Password)
{
    public override string ToString() => $"SipAccount({Settings.AddressOfRecord}, password: [redacted])";
}
