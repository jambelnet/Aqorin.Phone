namespace Aqorin.Phone.Sip;

/// <summary>Runtime-toggleable diagnostics. Bound to the "Diagnostic logging" switch in the UI.</summary>
public sealed class SipDiagnosticsOptions
{
    /// <summary>When true, full (redacted) SIP messages are logged at Debug level.</summary>
    public bool SipTraceEnabled { get; set; }
}
