namespace Aqorin.Phone.IntegrationTests;

/// <summary>
/// Opt-in test against a real FRITZ!Box. Skipped unless the FRITZ_SIP_* environment variables are set.
/// Never put real credentials in source control; export them in the shell that runs <c>dotnet test</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class IntegrationFactAttribute : FactAttribute
{
    public IntegrationFactAttribute(bool requiresDestination = false)
    {
        var missing = FritzEnvironment.MissingVariables(requiresDestination).ToList();
        if (missing.Count > 0)
        {
            Skip = "Integration test skipped; set " + string.Join(", ", missing) + " to run it against a FRITZ!Box.";
        }
    }
}

public static class FritzEnvironment
{
    public const string Host = "FRITZ_SIP_HOST";
    public const string Port = "FRITZ_SIP_PORT";
    public const string Username = "FRITZ_SIP_USERNAME";
    public const string Password = "FRITZ_SIP_PASSWORD";
    public const string Destination = "FRITZ_SIP_DESTINATION";

    public static IEnumerable<string> MissingVariables(bool requiresDestination)
    {
        foreach (var name in new[] { Host, Username, Password })
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)))
            {
                yield return name;
            }
        }

        if (requiresDestination && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Destination)))
        {
            yield return Destination;
        }
    }

    public static Core.Model.SipAccount Account()
    {
        var port = int.TryParse(Environment.GetEnvironmentVariable(Port), out var p) ? p : Core.Model.SipAccountSettings.DefaultPort;
        var settings = new Core.Model.SipAccountSettings
        {
            Registrar = Environment.GetEnvironmentVariable(Host)!.Trim(),
            Port = port,
            Username = Environment.GetEnvironmentVariable(Username)!.Trim(),
            DisplayName = "Aqorin.Phone integration test",
            RegistrationExpirySeconds = 120,
            DiagnosticLogging = true,
        };
        return new Core.Model.SipAccount(settings, Environment.GetEnvironmentVariable(Password)!);
    }
}
