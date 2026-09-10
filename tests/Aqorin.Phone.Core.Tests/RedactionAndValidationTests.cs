using Aqorin.Phone.Core.Diagnostics;
using Aqorin.Phone.Core.Model;
using Aqorin.Phone.Core.Retry;
using Microsoft.Extensions.Logging;

namespace Aqorin.Phone.Core.Tests;

public class SensitiveDataRedactorTests
{
    private const string Register =
        "REGISTER sip:fritz.box SIP/2.0\r\n" +
        "Via: SIP/2.0/UDP 192.168.178.20:5060;branch=z9hG4bK1\r\n" +
        "From: <sip:620@fritz.box>;tag=abc\r\n" +
        "Authorization: Digest username=\"620\", realm=\"fritz.box\", nonce=\"1A2B3C\", uri=\"sip:fritz.box\", response=\"deadbeefcafebabe0123456789abcdef\", algorithm=MD5\r\n" +
        "Content-Length: 0\r\n";

    [Fact]
    public void Authorization_header_is_redacted()
    {
        var redacted = SensitiveDataRedactor.Redact(Register);

        Assert.DoesNotContain("deadbeefcafebabe", redacted);
        Assert.DoesNotContain("1A2B3C", redacted);
        Assert.Contains("Authorization: [redacted]", redacted);
        Assert.Contains("REGISTER sip:fritz.box SIP/2.0", redacted);
        Assert.Contains("From: <sip:620@fritz.box>;tag=abc", redacted);
    }

    [Fact]
    public void Proxy_authorization_and_challenges_are_redacted()
    {
        var text = "SIP/2.0 401 Unauthorized\r\nWWW-Authenticate: Digest realm=\"fritz.box\", nonce=\"6F7E8D\"\r\n" +
                   "Proxy-Authorization: Digest username=\"620\", response=\"aaaa\"\r\n";

        var redacted = SensitiveDataRedactor.Redact(text);

        Assert.DoesNotContain("6F7E8D", redacted);
        Assert.DoesNotContain("aaaa", redacted);
        Assert.Contains("401 Unauthorized", redacted);
    }

    [Fact]
    public void Digest_parameters_outside_headers_are_redacted()
    {
        var redacted = SensitiveDataRedactor.Redact("auth failed with response=abc123 nonce=\"n1\" cnonce=c1 opaque=o1");
        Assert.DoesNotContain("abc123", redacted);
        Assert.DoesNotContain("n1", redacted);
        Assert.DoesNotContain("c1", redacted);
        Assert.DoesNotContain("o1", redacted);
    }

    [Theory]
    [InlineData("password=hunter2", "hunter2")]
    [InlineData("Password: \"hunter2\"", "hunter2")]
    [InlineData("pwd='hunter2'", "hunter2")]
    [InlineData("{\"password\": \"hunter2\"}", "hunter2")]
    [InlineData("secret=abc&user=620", "abc")]
    public void Password_pairs_are_redacted(string text, string secret)
    {
        var redacted = SensitiveDataRedactor.Redact(text);
        Assert.DoesNotContain(secret, redacted);
        Assert.Contains(SensitiveDataRedactor.Mask, redacted);
    }

    [Fact]
    public void Harmless_text_is_untouched()
    {
        const string text = "INVITE sip:0301234@fritz.box SIP/2.0\r\nTo: <sip:0301234@fritz.box>\r\n";
        Assert.Equal(text, SensitiveDataRedactor.Redact(text));
        Assert.Equal(string.Empty, SensitiveDataRedactor.Redact(null));
    }

    [Fact]
    public void Known_secret_value_is_removed_everywhere()
    {
        Assert.Equal("[redacted] and [redacted]", SensitiveDataRedactor.RedactValue("s3cret and s3cret", "s3cret"));
    }

    [Fact]
    public void SipAccount_ToString_never_exposes_the_password()
    {
        var account = new SipAccount(new SipAccountSettings { Username = "620" }, "s3cret");
        Assert.DoesNotContain("s3cret", account.ToString());
    }
}

public class DiagnosticsLogTests
{
    [Fact]
    public void Entries_are_redacted_and_bounded()
    {
        var log = new DiagnosticsLog(capacity: 3);
        var received = new List<DiagnosticsEntry>();
        log.EntryAdded += received.Add;

        for (var i = 0; i < 5; i++)
        {
            log.Add(LogLevel.Information, "Aqorin.Phone.Sip.Test", $"message {i} password=secret{i}");
        }

        var snapshot = log.Snapshot();
        Assert.Equal(3, snapshot.Count);
        Assert.All(snapshot, e => Assert.DoesNotContain("secret", e.Message));
        Assert.All(snapshot, e => Assert.Equal("Test", e.Category));
        Assert.Equal(5, received.Count);
    }

    [Fact]
    public void Entries_below_minimum_level_are_dropped()
    {
        var log = new DiagnosticsLog { MinimumLevel = LogLevel.Information };
        log.Add(LogLevel.Debug, "cat", "debug");
        log.Add(LogLevel.Warning, "cat", "warn");
        Assert.Single(log.Snapshot());
    }

    [Fact]
    public void Logger_provider_forwards_and_redacts()
    {
        var log = new DiagnosticsLog();
        using var provider = new DiagnosticsLoggerProvider(log);
        var logger = provider.CreateLogger("Sip");
        logger.LogInformation("Authorization: Digest response=\"abc\"");
        var entry = Assert.Single(log.Snapshot());
        Assert.DoesNotContain("abc", entry.Message);
    }

    [Fact]
    public void Redacting_logger_factory_redacts_third_party_messages()
    {
        var log = new DiagnosticsLog();
        using var inner = LoggerFactory.Create(b => b.AddProvider(new DiagnosticsLoggerProvider(log)).SetMinimumLevel(LogLevel.Trace));
        var factory = new RedactingLoggerFactory(inner);
        factory.CreateLogger("SIPSorcery").LogWarning("sending password=topsecret");
        Assert.DoesNotContain("topsecret", Assert.Single(log.Snapshot()).Message);
    }
}

public class SipAccountValidatorTests
{
    [Fact]
    public void Valid_settings_pass()
    {
        var result = SipAccountValidator.Validate(new SipAccountSettings { Registrar = "fritz.box", Username = "620" }, "pw");
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Ip_addresses_are_accepted_as_registrar()
    {
        var result = SipAccountValidator.Validate(new SipAccountSettings { Registrar = "192.168.178.1", Username = "620" }, "pw");
        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("", "620", "pw", "Registrar")]
    [InlineData("fritz box", "620", "pw", "Registrar")]
    [InlineData("fritz.box", "", "pw", "Username")]
    [InlineData("fritz.box", "6 20", "pw", "Username")]
    [InlineData("fritz.box", "620@x", "pw", "Username")]
    [InlineData("fritz.box", "620", "", "Password")]
    public void Missing_or_malformed_fields_produce_friendly_errors(string host, string user, string password, string expectedWord)
    {
        var result = SipAccountValidator.Validate(new SipAccountSettings { Registrar = host, Username = user }, password);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains(expectedWord, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70000)]
    public void Port_out_of_range_is_rejected(int port)
    {
        var result = SipAccountValidator.Validate(new SipAccountSettings { Username = "620", Port = port }, "pw");
        Assert.Contains(result.Errors, e => e.Contains("Port"));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(99999)]
    public void Expiry_out_of_range_is_rejected(int expiry)
    {
        var result = SipAccountValidator.Validate(new SipAccountSettings { Username = "620", RegistrationExpirySeconds = expiry }, "pw");
        Assert.Contains(result.Errors, e => e.Contains("expiry", StringComparison.OrdinalIgnoreCase));
    }
}

public class ExponentialBackoffPolicyTests
{
    [Fact]
    public void Delays_double_and_are_capped()
    {
        var policy = new ExponentialBackoffPolicy(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(20), maxAttempts: 6);
        var delays = new List<double>();
        for (var attempt = 1; attempt <= 6; attempt++)
        {
            Assert.True(policy.TryGetDelay(attempt, out var delay));
            delays.Add(delay.TotalSeconds);
        }

        Assert.Equal(new[] { 2d, 4d, 8d, 16d, 20d, 20d }, delays);
        Assert.False(policy.TryGetDelay(7, out _));
        Assert.False(policy.TryGetDelay(0, out _));
    }

    [Fact]
    public void Jitter_stays_within_bounds()
    {
        var policy = new ExponentialBackoffPolicy(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(60), maxAttempts: 3, jitterFraction: 0.2);
        var random = new Random(42);
        for (var i = 0; i < 100; i++)
        {
            Assert.True(policy.TryGetDelay(1, random, out var delay));
            Assert.InRange(delay.TotalSeconds, 8, 12);
        }
    }

    [Fact]
    public void Zero_attempts_disables_retries()
    {
        var policy = new ExponentialBackoffPolicy(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), maxAttempts: 0);
        Assert.False(policy.TryGetDelay(1, out _));
    }
}

public class CallInfoTests
{
    [Fact]
    public void Duration_is_measured_from_connect_until_end_or_now()
    {
        var connected = DateTimeOffset.UtcNow.AddSeconds(-30);
        var info = new CallInfo { State = CallState.Active, ConnectedAt = connected };
        Assert.InRange(info.Duration(DateTimeOffset.UtcNow)!.Value.TotalSeconds, 29, 31);

        var ended = info with { State = CallState.Idle, EndedAt = connected.AddSeconds(12) };
        Assert.Equal(12, ended.Duration(DateTimeOffset.UtcNow)!.Value.TotalSeconds, 0.001);

        Assert.Null(new CallInfo().Duration(DateTimeOffset.UtcNow));
    }
}
