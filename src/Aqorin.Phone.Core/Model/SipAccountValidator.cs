using System.Text.RegularExpressions;

namespace Aqorin.Phone.Core.Model;

public sealed record ValidationResult(IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
    public static ValidationResult Ok { get; } = new(Array.Empty<string>());
}

/// <summary>Produces friendly, user-facing validation errors for account settings.</summary>
public static partial class SipAccountValidator
{
    [GeneratedRegex(@"^(?=.{1,253}$)([A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)(\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*\.?$")]
    private static partial Regex HostNameRegex();

    public static ValidationResult Validate(SipAccountSettings settings, string password)
    {
        var errors = new List<string>();

        var host = settings.Registrar?.Trim() ?? string.Empty;
        if (host.Length == 0)
        {
            errors.Add("Registrar/host is required.");
        }
        else if (!System.Net.IPAddress.TryParse(host, out _) && !HostNameRegex().IsMatch(host))
        {
            errors.Add("Registrar/host must be a host name or an IP address.");
        }

        if (settings.Port is < 1 or > 65535)
        {
            errors.Add("Port must be between 1 and 65535 (SIP default: 5060).");
        }

        if (settings.LocalPort is < 0 or > 65535)
        {
            errors.Add("Local port must be 0 (automatic) or between 1 and 65535.");
        }

        var user = settings.Username?.Trim() ?? string.Empty;
        if (user.Length == 0)
        {
            errors.Add("Username is required.");
        }
        else if (user.Any(char.IsWhiteSpace) || user.Contains('@') || user.Contains(':'))
        {
            errors.Add("Username must not contain spaces, '@' or ':'.");
        }

        if (string.IsNullOrEmpty(password))
        {
            errors.Add("Password is required.");
        }

        if (settings.RegistrationExpirySeconds is < SipAccountSettings.MinRegistrationExpirySeconds
            or > SipAccountSettings.MaxRegistrationExpirySeconds)
        {
            errors.Add($"Registration expiry must be between {SipAccountSettings.MinRegistrationExpirySeconds} and {SipAccountSettings.MaxRegistrationExpirySeconds} seconds.");
        }

        return errors.Count == 0 ? ValidationResult.Ok : new ValidationResult(errors);
    }
}
