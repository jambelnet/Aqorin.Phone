using System.Text.RegularExpressions;

namespace Aqorin.Phone.Core.Diagnostics;

/// <summary>
/// Removes reusable credentials from text before it reaches any log sink:
/// SIP Authorization / Proxy-Authorization / WWW-Authenticate / Proxy-Authenticate headers,
/// digest <c>response=</c>/<c>nonce=</c>/<c>cnonce=</c>/<c>opaque=</c> values and <c>password=...</c> pairs.
/// </summary>
public static partial class SensitiveDataRedactor
{
    public const string Mask = "[redacted]";

    // Whole SIP auth header values (multi-token, may wrap onto continuation lines).
    [GeneratedRegex(@"(?im)^(\s*(?:Authorization|Proxy-Authorization|WWW-Authenticate|Proxy-Authenticate)\s*:\s*)(.*(?:\r?\n[ \t]+.*)*)$")]
    private static partial Regex AuthHeaderRegex();

    // Digest parameters that can be replayed.
    [GeneratedRegex(@"(?i)\b(response|nonce|cnonce|opaque|nc)\s*=\s*""?[^"",\s]*""?")]
    private static partial Regex DigestParamRegex();

    // password=..., pwd=..., "password": "..."  (query strings, JSON, key/value logs)
    [GeneratedRegex(@"(?i)(\b(?:password|passwd|pwd|secret)\b[""']?\s*[=:]\s*)(""[^""]*""|'[^']*'|[^\s,;&]+)")]
    private static partial Regex PasswordPairRegex();

    /// <summary>Redacts the text. Never throws; returns the input unchanged when it is null/empty.</summary>
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        var result = AuthHeaderRegex().Replace(text, m => m.Groups[1].Value + Mask);
        result = DigestParamRegex().Replace(result, m => m.Groups[1].Value + "=" + Mask);
        result = PasswordPairRegex().Replace(result, m => m.Groups[1].Value + Mask);
        return result;
    }

    /// <summary>Redacts a specific known secret value wherever it appears verbatim (defensive extra layer).</summary>
    public static string RedactValue(string? text, string? secret)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(secret))
        {
            return text ?? string.Empty;
        }

        return text.Replace(secret, Mask, StringComparison.Ordinal);
    }
}
