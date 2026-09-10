namespace Aqorin.Phone.Core.Abstractions;

/// <summary>User-selectable verbose diagnostics (redacted SIP traces, debug-level log entries).</summary>
public interface IDiagnosticsSwitch
{
    bool Verbose { get; set; }
}
