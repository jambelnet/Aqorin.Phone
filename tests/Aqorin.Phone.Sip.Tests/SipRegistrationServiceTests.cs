using Aqorin.Phone.Core.Model;
using Aqorin.Phone.Core.Retry;
using Aqorin.Phone.Sip.Internal;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aqorin.Phone.Sip.Tests;

public class SipRegistrationServiceTests
{
    private static readonly SipAccount Account = new(new SipAccountSettings { Registrar = "fritz.box", Username = "620", RegistrationExpirySeconds = 300 }, "pw");

    private readonly FakeTransportFactory _transports = new();
    private readonly FakeRegistrationClientFactory _clients = new();
    private readonly SipSessionContext _context = new();
    private readonly FakeRegistrarResolver _resolver = new();
    private readonly List<RegistrationStatus> _statuses = [];

    private SipRegistrationService Create(ExponentialBackoffPolicy? backoff = null)
    {
        var service = new SipRegistrationService(_transports, _clients, _context, NullLogger<SipRegistrationService>.Instance,
            backoff ?? new ExponentialBackoffPolicy(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(40), maxAttempts: 2),
            attemptTimeout: TimeSpan.FromMilliseconds(100),
            resolver: _resolver)
        {
            ReRegisterOnNetworkChange = false,
            GuardGrace = TimeSpan.FromMilliseconds(200),
        };
        service.StatusChanged += (_, s) => { lock (_statuses) { _statuses.Add(s); } };
        return service;
    }

    private static RegistrationEvent Success(int expiry = 300) => new(RegistrationOutcome.Success, "200 OK", 200, expiry);
    private static RegistrationEvent Unauthorized() => new(RegistrationOutcome.AuthenticationFailure, "Registration failed with 401 Unauthorized.", 401, null);
    private static RegistrationEvent Timeout() => new(RegistrationOutcome.TemporaryFailure, "Registration to fritz.box timed out.", null, null);

    [Fact]
    public async Task Successful_registration_publishes_registered_and_opens_the_transport()
    {
        await using var service = Create();
        _clients.ScriptedOutcomes.Enqueue(Success());

        await service.RegisterAsync(Account);

        Assert.Equal(RegistrationState.Registered, service.Status.State);
        Assert.Equal("620@fritz.box", service.Status.AddressOfRecord);
        Assert.NotNull(service.Status.ExpiresAt);
        Assert.Contains("Registered as 620@fritz.box", service.Status.Message);
        Assert.Equal([RegistrationState.Registering, RegistrationState.Registered], _statuses.Select(s => s.State));
        Assert.Single(_transports.Created);
        Assert.True(_context.IsRegistered);
        Assert.Same(_transports.Created[0], _context.Transport);
        Assert.Equal("pw", _clients.Accounts.Single().Password);
        Assert.Equal(Account.Settings, service.CurrentAccount);
    }

    [Fact]
    public async Task Registering_twice_throws()
    {
        await using var service = Create();
        _clients.ScriptedOutcomes.Enqueue(Success());
        await service.RegisterAsync(Account);

        var ex = await Assert.ThrowsAsync<InvalidRegistrationOperationException>(() => service.RegisterAsync(Account));
        Assert.Equal(RegistrationState.Registered, ex.State);
        Assert.Single(_transports.Created);
    }

    [Fact]
    public async Task Invalid_account_is_rejected_before_touching_the_network()
    {
        await using var service = Create();
        var bad = new SipAccount(new SipAccountSettings { Registrar = "", Username = "620" }, "pw");

        await Assert.ThrowsAsync<ArgumentException>(() => service.RegisterAsync(bad));
        Assert.Empty(_transports.Created);
        Assert.Equal(RegistrationState.Disconnected, service.Status.State);
    }

    [Fact]
    public async Task Authentication_failure_is_terminal_and_releases_the_transport()
    {
        await using var service = Create();
        _clients.ScriptedOutcomes.Enqueue(Unauthorized());

        await service.RegisterAsync(Account);

        Assert.Equal(RegistrationState.Failed, service.Status.State);
        Assert.True(service.Status.IsAuthenticationFailure);
        Assert.Null(service.Status.NextRetryAt);
        Assert.Contains("wrong username or password", service.Status.Message);
        Assert.Contains("401", service.Status.Detail);
        Assert.True(_transports.Created.Single().Disposed);
        Assert.True(_clients.Created.Single().Disposed);
        Assert.False(_context.IsRegistered);
        Assert.Null(_context.Transport);

        // ...and the user can try again with corrected credentials.
        _clients.ScriptedOutcomes.Enqueue(Success());
        await service.RegisterAsync(Account);
        Assert.Equal(RegistrationState.Registered, service.Status.State);
        Assert.Equal(2, _transports.Created.Count);
    }

    [Fact]
    public async Task Temporary_failure_retries_with_backoff_then_succeeds()
    {
        await using var service = Create();
        _clients.ScriptedOutcomes.Enqueue(Timeout());
        _clients.ScriptedOutcomes.Enqueue(Success());

        await service.RegisterAsync(Account);

        // First attempt failed: still "Registering" with a scheduled retry.
        var afterFirst = _statuses.Last();
        Assert.Equal(RegistrationState.Registering, afterFirst.State);
        Assert.NotNull(afterFirst.NextRetryAt);
        Assert.Equal(1, afterFirst.FailedAttempts);
        Assert.Contains("Retrying", afterFirst.Message);

        await Wait.Until(() => service.Status.State == RegistrationState.Registered, what: "registered after retry");
        Assert.Equal(2, _clients.Created.Count);
        Assert.True(_clients.Created[0].Disposed);
        // The retry re-opens the transport (a socket that stopped receiving is the usual cause of "no response").
        Assert.Equal(2, _transports.Created.Count);
        Assert.True(_transports.Created[0].Disposed);
        Assert.False(_transports.Created[1].Disposed);
        Assert.Same(_transports.Created[1], _context.Transport);
        Assert.Equal(0, service.Status.FailedAttempts);
    }

    [Fact]
    public async Task Retries_are_bounded_and_end_in_failed()
    {
        await using var service = Create();
        _clients.ScriptedOutcomes.Enqueue(Timeout());
        _clients.ScriptedOutcomes.Enqueue(Timeout());
        _clients.ScriptedOutcomes.Enqueue(Timeout());

        await service.RegisterAsync(Account);
        await Wait.Until(() => service.Status.State == RegistrationState.Failed, what: "gave up");

        Assert.Equal(3, _clients.Created.Count); // 1 initial + 2 retries
        Assert.Contains("Gave up", service.Status.Message);
        Assert.False(service.Status.IsAuthenticationFailure);
        Assert.Equal(3, _transports.Created.Count); // one fresh transport per retry
        Assert.All(_transports.Created, t => Assert.True(t.Disposed));
        Assert.Null(_context.Transport);
    }

    [Fact]
    public async Task No_response_at_all_is_treated_as_temporary_failure()
    {
        await using var service = Create(new ExponentialBackoffPolicy(TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10), maxAttempts: 0));
        // No scripted outcome: the fake client never completes.

        await service.RegisterAsync(Account);

        Assert.Equal(RegistrationState.Failed, service.Status.State);
        Assert.Contains("did not respond", service.Status.Message);
    }

    [Fact]
    public async Task Resolution_failure_message_names_the_host()
    {
        await using var service = Create(new ExponentialBackoffPolicy(TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10), maxAttempts: 0));
        _clients.ScriptedOutcomes.Enqueue(new RegistrationEvent(RegistrationOutcome.ResolutionFailure, "Could not resolve fritz.box.", null, null));

        await service.RegisterAsync(Account);

        Assert.Equal(RegistrationState.Failed, service.Status.State);
        Assert.Contains("Cannot resolve host fritz.box", service.Status.Message);
    }

    [Fact]
    public async Task Transport_failure_is_reported_as_failed()
    {
        _transports.ThrowOnCreate = new InvalidOperationException("port in use");
        await using var service = Create();

        await service.RegisterAsync(Account);

        Assert.Equal(RegistrationState.Failed, service.Status.State);
        Assert.Contains("local SIP socket", service.Status.Message);
        Assert.Equal("port in use", service.Status.Detail);
    }

    [Fact]
    public async Task Refresh_failure_after_registration_triggers_a_retry()
    {
        await using var service = Create();
        _clients.ScriptedOutcomes.Enqueue(Success());
        await service.RegisterAsync(Account);

        _clients.ScriptedOutcomes.Enqueue(Success());
        _clients.Created[0].Raise(Timeout()); // later refresh failed

        await Wait.Until(() => _clients.Created.Count == 2 && service.Status.State == RegistrationState.Registered, what: "re-registered");
        Assert.Contains(_statuses, s => s.State == RegistrationState.Registering && s.NextRetryAt is not null);
    }

    [Fact]
    public async Task Explicit_refresh_replaces_the_client_without_unregistering()
    {
        await using var service = Create();
        _clients.ScriptedOutcomes.Enqueue(Success());
        await service.RegisterAsync(Account);
        _clients.ScriptedOutcomes.Enqueue(Success());

        await service.RefreshAsync();

        Assert.Equal(RegistrationState.Registered, service.Status.State);
        Assert.Equal(2, _clients.Created.Count);
        Assert.Equal([false], _clients.Created[0].StopCalls);
        Assert.True(_clients.Created[0].Disposed);
    }

    [Fact]
    public async Task Unregister_sends_zero_expiry_and_releases_everything()
    {
        await using var service = Create();
        _clients.ScriptedOutcomes.Enqueue(Success());
        await service.RegisterAsync(Account);

        await service.UnregisterAsync();

        Assert.Equal(RegistrationState.Disconnected, service.Status.State);
        Assert.Equal([true], _clients.Created.Single().StopCalls);
        Assert.True(_clients.Created.Single().Disposed);
        Assert.True(_transports.Created.Single().Disposed);
        Assert.Null(_context.Transport);
        Assert.False(_context.IsRegistered);
        Assert.Null(service.CurrentAccount);
        Assert.Contains(RegistrationState.Unregistering, _statuses.Select(s => s.State));
    }

    [Fact]
    public async Task Unregister_while_retrying_cancels_the_retry()
    {
        await using var service = Create(new ExponentialBackoffPolicy(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), maxAttempts: 3));
        _clients.ScriptedOutcomes.Enqueue(Timeout());
        await service.RegisterAsync(Account);
        Assert.Equal(RegistrationState.Registering, service.Status.State);

        await service.UnregisterAsync();

        Assert.Equal(RegistrationState.Disconnected, service.Status.State);
        Assert.True(_transports.Created.Single().Disposed);
        await Task.Delay(50);
        Assert.Single(_clients.Created);
    }

    [Fact]
    public async Task Unregister_when_disconnected_throws()
    {
        await using var service = Create();
        await Assert.ThrowsAsync<InvalidRegistrationOperationException>(() => service.UnregisterAsync());
    }

    [Fact]
    public async Task Dispose_unregisters_and_cleans_up()
    {
        var service = Create();
        _clients.ScriptedOutcomes.Enqueue(Success());
        await service.RegisterAsync(Account);

        await service.DisposeAsync();

        Assert.Equal(RegistrationState.Disconnected, service.Status.State);
        Assert.True(_transports.Created.Single().Disposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.RegisterAsync(Account));
    }
}

public class RefreshTimingTests
{
    [Theory]
    [InlineData(300, 255)]
    [InlineData(60, 50)]
    [InlineData(3600, 3060)]
    [InlineData(20, 10)]
    public void Refresh_happens_before_expiry(long expiry, int expectedRefresh)
    {
        var refresh = SipSorceryRegistrationClient.RefreshSeconds(expiry);
        Assert.Equal(expectedRefresh, refresh);
        Assert.True(refresh < expiry);
    }
}

public class RegistrarResolutionTests
{
    private readonly FakeTransportFactory _transports = new();
    private readonly FakeRegistrationClientFactory _clients = new();
    private readonly SipSessionContext _context = new();
    private readonly FakeRegistrarResolver _resolver = new();

    private SipRegistrationService Create() =>
        new(_transports, _clients, _context, NullLogger<SipRegistrationService>.Instance,
            new ExponentialBackoffPolicy(TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10), maxAttempts: 0),
            attemptTimeout: TimeSpan.FromMilliseconds(100), resolver: _resolver)
        { ReRegisterOnNetworkChange = false, GuardGrace = TimeSpan.FromMilliseconds(100) };

    private static SipAccount Account(string host) => new(new SipAccountSettings { Registrar = host, Username = "620" }, "pw");

    [Fact]
    public async Task Resolved_lan_address_is_handed_to_the_transport_as_registrar_endpoint()
    {
        await using var service = Create();
        _clients.ScriptedOutcomes.Enqueue(new RegistrationEvent(RegistrationOutcome.Success, "200 OK", 200, 300));

        await service.RegisterAsync(Account("fritz.box"));

        Assert.Equal(RegistrationState.Registered, service.Status.State);
        Assert.Equal(["fritz.box"], _resolver.Queries);
        Assert.Equal(new System.Net.IPEndPoint(System.Net.IPAddress.Parse("192.168.178.1"), 5060), _transports.Created.Single().Registrar);
    }

    [Fact]
    public async Task Fritz_box_resolving_to_a_public_address_is_refused_before_anything_is_sent()
    {
        await using var service = Create();
        _resolver.Answers["fritz.box"] = [System.Net.IPAddress.Parse("212.42.244.122")];

        await service.RegisterAsync(Account("fritz.box"));

        Assert.Equal(RegistrationState.Failed, service.Status.State);
        Assert.Contains("does not point to your FRITZ!Box", service.Status.Message);
        Assert.Contains("212.42.244.122", service.Status.Detail);
        Assert.Contains("public domain since 2024", service.Status.Detail);
        Assert.Empty(_transports.Created);
        Assert.Empty(_clients.Created);
    }

    [Fact]
    public async Task Unresolvable_host_fails_with_a_hint()
    {
        await using var service = Create();

        await service.RegisterAsync(Account("nonexistent.fritz.box"));

        Assert.Equal(RegistrationState.Failed, service.Status.State);
        Assert.Contains("Cannot resolve host", service.Status.Message);
        Assert.Empty(_transports.Created);
    }

    [Fact]
    public async Task Ip_literal_is_used_directly()
    {
        await using var service = Create();
        _clients.ScriptedOutcomes.Enqueue(new RegistrationEvent(RegistrationOutcome.Success, "200 OK", 200, 300));

        await service.RegisterAsync(Account("192.168.178.1"));

        Assert.Equal(RegistrationState.Registered, service.Status.State);
        Assert.Equal(System.Net.IPAddress.Parse("192.168.178.1"), _transports.Created.Single().Registrar!.Address);
    }

    [Fact]
    public async Task Non_fritz_hostnames_may_resolve_to_public_addresses()
    {
        await using var service = Create();
        _resolver.Answers["sip.example.com"] = [System.Net.IPAddress.Parse("203.0.113.5")];
        _clients.ScriptedOutcomes.Enqueue(new RegistrationEvent(RegistrationOutcome.Success, "200 OK", 200, 300));

        await service.RegisterAsync(Account("sip.example.com"));

        Assert.Equal(RegistrationState.Registered, service.Status.State);
    }

    [Theory]
    [InlineData("10.0.0.1", true)]
    [InlineData("172.16.5.5", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.168.178.1", true)]
    [InlineData("169.254.1.1", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("212.42.244.122", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("fd00::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("::1", true)]
    [InlineData("2001:db8::1", false)]
    public void Private_address_detection(string address, bool expected)
    {
        Assert.Equal(expected, RegistrarResolution.IsPrivateOrLocal(System.Net.IPAddress.Parse(address)));
    }
}
