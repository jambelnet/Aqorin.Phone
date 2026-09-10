using Aqorin.Phone.Core.Audio;
using Aqorin.Phone.Core.Model;
using Aqorin.Phone.Core.Retry;
using Aqorin.Phone.Sip.Internal;
using Aqorin.Phone.Sip.Media;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aqorin.Phone.Sip.Tests.Loopback;

/// <summary>
/// End-to-end signalling over loopback UDP against <see cref="FakeFritzBox"/> using the real SIPSorcery-backed services.
/// Audio devices are the silent <see cref="NullAudioDeviceService"/>; RTP sockets are real but carry no audio.
/// </summary>
[Trait("Category", "Loopback")]
[Collection("Loopback")] // real UDP sockets and timing: run these classes one at a time
public sealed class LoopbackTests : IAsyncLifetime
{
    private readonly FakeFritzBox _box = FakeFritzBox.Start();
    private readonly SipSessionContext _context = new();
    private SipRegistrationService _registration = null!;
    private SipCallService _calls = null!;
    private readonly List<CallInfo> _callEvents = [];

    public Task InitializeAsync()
    {
        var diagnostics = new SipDiagnosticsOptions { SipTraceEnabled = true };
        _registration = new SipRegistrationService(
            new SipSorceryTransportFactory(NullLogger<SipSorceryTransportFactory>.Instance, diagnostics),
            new SipSorceryRegistrationClientFactory(NullLogger<SipSorceryRegistrationClient>.Instance),
            _context,
            NullLogger<SipRegistrationService>.Instance,
            new ExponentialBackoffPolicy(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50), maxAttempts: 0),
            attemptTimeout: TimeSpan.FromSeconds(5))
        {
            ReRegisterOnNetworkChange = false,
            GuardGrace = TimeSpan.FromSeconds(2),
        };
        _calls = new SipCallService(
            new SipSorceryUserAgentFactory(NullLogger<SipSorceryUserAgent>.Instance),
            new SipSorceryAudioMediaSessionFactory(new NullAudioDeviceService("loopback test"), NullLogger<SipSorceryAudioMediaSessionFactory>.Instance),
            _context,
            NullLogger<SipCallService>.Instance);
        _calls.CallChanged += (_, c) => { lock (_callEvents) { _callEvents.Add(c); } };
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _calls.DisposeAsync();
        await _registration.DisposeAsync();
        await _box.DisposeAsync();
    }

    private SipAccount Account(string? password = null) => new(
        new SipAccountSettings { Registrar = _box.Host, Port = _box.Port, Username = _box.Username, DisplayName = "Loopback", RegistrationExpirySeconds = 300 },
        password ?? _box.Password);

    private static async Task WaitFor(Func<bool> condition, string what, int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Timed out waiting for: " + what);
            }

            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Registers_with_digest_authentication_and_unregisters()
    {
        await _registration.RegisterAsync(Account());

        Assert.Equal(RegistrationState.Registered, _registration.Status.State);
        Assert.True(_box.Registered.Task.IsCompleted);
        Assert.Equal(2, _box.RegisterRequests); // challenge + authenticated REGISTER
        Assert.NotNull(_box.RegisteredContact);
        Assert.Equal("620", _box.RegisteredContact!.User);
        Assert.Contains("120s", _registration.Status.Detail); // expiry granted by the registrar, not the requested 300
        Assert.NotNull(_registration.Status.ExpiresAt);
        Assert.True(_context.IsRegistered);

        await _registration.UnregisterAsync();

        await Task.WhenAny(_box.Unregistered.Task, Task.Delay(5000));
        Assert.True(_box.Unregistered.Task.IsCompleted, "expected a REGISTER with Expires: 0");
        Assert.Equal(RegistrationState.Disconnected, _registration.Status.State);
        Assert.Null(_box.RegisteredContact);
    }

    [Fact]
    public async Task Wrong_password_is_reported_as_authentication_failure()
    {
        await _registration.RegisterAsync(Account("wrong"));

        Assert.Equal(RegistrationState.Failed, _registration.Status.State);
        Assert.True(_registration.Status.IsAuthenticationFailure, _registration.Status.Message);
        Assert.Contains("wrong username or password", _registration.Status.Message);
        Assert.False(_box.Registered.Task.IsCompleted);
        Assert.Null(_context.Transport); // transport released; user must correct credentials and register again
    }

    [Fact]
    public async Task Outgoing_call_is_challenged_rings_and_ends_busy()
    {
        await _registration.RegisterAsync(Account());
        Assert.Equal(RegistrationState.Registered, _registration.Status.State);

        var result = await _calls.PlaceCallAsync("030 123456");

        Assert.Equal(CallState.Failed, result.State);
        Assert.Equal(CallEndReason.Busy, result.EndReason);
        Assert.Equal("486 Busy Here", result.Detail);
        Assert.Equal("030123456", result.RemoteParty);
        Assert.Equal(2, _box.InviteRequests); // 401 challenge, then authenticated INVITE
        var invite = await _box.InviteReceived.Task;
        Assert.Equal("030123456", invite.URI.User);
        Assert.Equal("620", invite.Header.From.FromURI.User);
        Assert.Equal("Loopback", invite.Header.From.FromName);
        Assert.Contains("m=audio", invite.Body);
        Assert.Contains("PCMA/8000", invite.Body);

        await WaitFor(() => { lock (_callEvents) { return _callEvents.Any(e => e.State == CallState.Failed); } }, "events");
        CallState[] states;
        lock (_callEvents)
        {
            states = _callEvents.Select(e => e.State).Distinct().ToArray();
        }

        Assert.Equal([CallState.Dialing, CallState.Ringing, CallState.Failed], states);
    }

    [Fact]
    public async Task Incoming_call_rings_the_softphone_and_can_be_rejected()
    {
        await _registration.RegisterAsync(Account());
        Assert.Equal(RegistrationState.Registered, _registration.Status.State);

        _box.CallSoftphone("0301234", "Alice");

        await WaitFor(() => _calls.CurrentCall.State == CallState.Incoming, "incoming call");
        Assert.Equal(CallDirection.Incoming, _calls.CurrentCall.Direction);
        Assert.Equal("Alice", _calls.CurrentCall.RemoteParty);
        Assert.Contains("0301234", _calls.CurrentCall.RemoteUri);
        await Task.WhenAny(_box.OutgoingInviteRinging.Task, Task.Delay(5000));
        Assert.True(_box.OutgoingInviteRinging.Task.IsCompleted, "the softphone should send 180 Ringing");

        await _calls.RejectAsync();

        Assert.Equal(CallState.Idle, _calls.CurrentCall.State);
        Assert.Equal(CallEndReason.LocalReject, _calls.CurrentCall.EndReason);
        var status = await _box.OutgoingInviteFinalStatus.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(486, status);
    }

    [Fact]
    public async Task Unregistered_softphone_cannot_call()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _calls.PlaceCallAsync("0301234"));
        Assert.Equal(0, _box.InviteRequests);
    }
}
