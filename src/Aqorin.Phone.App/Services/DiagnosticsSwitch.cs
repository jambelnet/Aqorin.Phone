using Aqorin.Phone.Core.Abstractions;
using Aqorin.Phone.Core.Diagnostics;
using Aqorin.Phone.Sip;
using Microsoft.Extensions.Logging;

namespace Aqorin.Phone.App.Services;

/// <summary>Toggles verbose diagnostics in both the log panel and the SIP transport tracing.</summary>
public sealed class DiagnosticsSwitch(DiagnosticsLog log, SipDiagnosticsOptions sipOptions) : IDiagnosticsSwitch
{
    public bool Verbose
    {
        get => sipOptions.SipTraceEnabled;
        set
        {
            sipOptions.SipTraceEnabled = value;
            log.MinimumLevel = value ? LogLevel.Debug : LogLevel.Information;
        }
    }
}
