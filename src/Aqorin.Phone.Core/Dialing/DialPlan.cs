using System.Text;
using Aqorin.Phone.Core.Model;

namespace Aqorin.Phone.Core.Dialing;

/// <summary>Result of normalising a user-entered destination.</summary>
public sealed record DialTarget(string SipUri, string DisplayNumber, bool IsFullUri);

/// <summary>
/// Centralised destination normalisation. Turns whatever the user typed into the SIP URI to send the INVITE to.
/// <list type="bullet">
/// <item>Whitespace, dots, dashes, slashes and parentheses are removed from telephone numbers.</item>
/// <item>A leading "+" becomes the international prefix "00" used by many SIP routers.</item>
/// <item>"*" and "#" are kept so internal numbers (**620) and service codes work.</item>
/// <item>A full <c>sip:</c>/<c>sips:</c> URI or a <c>user@host</c> pair is passed through.</item>
/// </list>
/// </summary>
public static class DialPlan
{
    public static bool TryNormalize(string? input, SipAccountSettings settings, out DialTarget? target, out string? error)
    {
        ArgumentNullException.ThrowIfNull(settings);
        target = null;
        error = null;

        var text = (input ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            error = "Enter a telephone number.";
            return false;
        }

        if (text.StartsWith("sip:", StringComparison.OrdinalIgnoreCase) || text.StartsWith("sips:", StringComparison.OrdinalIgnoreCase))
        {
            if (text.Any(char.IsWhiteSpace))
            {
                error = "A SIP address must not contain spaces.";
                return false;
            }

            var user = UserPart(text);
            if (user.Length == 0)
            {
                error = "The SIP address must contain a user part (sip:number@host).";
                return false;
            }

            target = new DialTarget(text, user, IsFullUri: true);
            return true;
        }

        if (text.Contains('@'))
        {
            if (text.Any(char.IsWhiteSpace))
            {
                error = "A SIP address must not contain spaces.";
                return false;
            }

            var user = text[..text.IndexOf('@')];
            if (user.Length == 0 || text.EndsWith('@'))
            {
                error = "Use the form number@host.";
                return false;
            }

            target = new DialTarget("sip:" + text, user, IsFullUri: true);
            return true;
        }

        var number = NormalizeNumber(text);
        if (number.Length == 0 || !text.Any(c => char.IsAsciiDigit(c) || c is '*' or '#'))
        {
            // A bare "+" or separators only would normalise to "00" / "" — not a number.
            error = "Enter a telephone number.";
            return false;
        }

        if (number.Any(c => !(char.IsAsciiDigit(c) || c is '*' or '#')))
        {
            error = "The telephone number may only contain digits, +, *, # and separators.";
            return false;
        }

        target = new DialTarget(BuildSipUri(number, settings), number, IsFullUri: false);
        return true;
    }

    public static DialTarget Normalize(string? input, SipAccountSettings settings)
    {
        return TryNormalize(input, settings, out var target, out var error)
            ? target!
            : throw new InvalidDestinationException(error ?? "Invalid destination.");
    }

    /// <summary>Removes formatting characters and converts a leading "+" into "00".</summary>
    public static string NormalizeNumber(string text)
    {
        var sb = new StringBuilder(text.Length + 1);
        var seenSignificant = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c) || c is '-' or '.' or '/' or '(' or ')')
            {
                continue;
            }

            if (c == '+')
            {
                if (!seenSignificant)
                {
                    sb.Append("00");
                    seenSignificant = true;
                }

                // "+" anywhere else is dropped as formatting noise.
                continue;
            }

            seenSignificant = true;
            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>Builds <c>sip:number@host[:port][;transport=tcp]</c> for the configured registrar.</summary>
    public static string BuildSipUri(string user, SipAccountSettings settings)
    {
        var sb = new StringBuilder("sip:").Append(user).Append('@').Append(settings.Registrar.Trim());
        if (settings.Port != SipAccountSettings.DefaultPort)
        {
            sb.Append(':').Append(settings.Port);
        }

        if (settings.Transport == SipTransport.Tcp)
        {
            sb.Append(";transport=tcp");
        }

        return sb.ToString();
    }

    /// <summary>Extracts the user part of a SIP URI for display ("sip:620@fritz.box" → "620").</summary>
    public static string UserPart(string sipUri)
    {
        var text = sipUri;
        var colon = text.IndexOf(':');
        if (colon >= 0 && (text.StartsWith("sip:", StringComparison.OrdinalIgnoreCase) || text.StartsWith("sips:", StringComparison.OrdinalIgnoreCase)))
        {
            text = text[(colon + 1)..];
        }

        var at = text.IndexOf('@');
        if (at < 0)
        {
            var semi = text.IndexOf(';');
            return semi >= 0 ? text[..semi] : text;
        }

        return text[..at];
    }
}
