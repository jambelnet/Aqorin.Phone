using Aqorin.Phone.Core.Model;

namespace Aqorin.Phone.Sip;

/// <summary>Maps SIP final responses / stack failure messages to <see cref="CallEndReason"/> and friendly text.</summary>
public static class SipResponseMapper
{
    public static (CallEndReason Reason, string Message) MapFailure(int? statusCode, string? reasonPhrase, string? stackMessage)
    {
        var detail = string.IsNullOrWhiteSpace(reasonPhrase) ? stackMessage : reasonPhrase;
        switch (statusCode)
        {
            case 486:
            case 600:
                return (CallEndReason.Busy, "Busy");
            case 603:
                return (CallEndReason.Rejected, "Call declined");
            case 403:
                return (CallEndReason.Rejected, "Call forbidden (403)");
            case 404:
            case 484:
            case 604:
                return (CallEndReason.NotFound, "Number not found");
            case 401:
            case 407:
                return (CallEndReason.Unauthorized, "Authentication failed for the call");
            case 408:
                return (CallEndReason.Timeout, "No answer (timeout)");
            case 480:
                return (CallEndReason.Timeout, "Temporarily unavailable / no answer");
            case 487:
                return (CallEndReason.LocalCancel, "Call cancelled");
            case 488:
            case 415:
            case 406:
                return (CallEndReason.MediaFailure, "Codec not acceptable to the remote party");
            case >= 500 and <= 599:
                return (CallEndReason.TransportFailure, $"Registrar error ({statusCode} {detail})".TrimEnd());
            case >= 400 and <= 699:
                return (CallEndReason.Rejected, $"Call failed ({statusCode} {detail})".TrimEnd());
        }

        var text = stackMessage ?? string.Empty;
        if (text.Contains("cancel", StringComparison.OrdinalIgnoreCase))
        {
            return (CallEndReason.LocalCancel, "Call cancelled");
        }

        if (text.Contains("timed out", StringComparison.OrdinalIgnoreCase) || text.Contains("timeout", StringComparison.OrdinalIgnoreCase))
        {
            return (CallEndReason.Timeout, "No response from the registrar (timeout)");
        }

        if (text.Contains("resolve", StringComparison.OrdinalIgnoreCase) || text.Contains("DNS", StringComparison.OrdinalIgnoreCase))
        {
            return (CallEndReason.TransportFailure, "Could not resolve the registrar host");
        }

        if (text.Contains("SDP", StringComparison.OrdinalIgnoreCase) || text.Contains("media", StringComparison.OrdinalIgnoreCase) || text.Contains("offer", StringComparison.OrdinalIgnoreCase))
        {
            return (CallEndReason.MediaFailure, "Audio negotiation failed");
        }

        return (CallEndReason.Error, string.IsNullOrWhiteSpace(text) ? "Call failed" : text);
    }
}
