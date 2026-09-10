using Aqorin.Phone.Core.Audio;
using Aqorin.Phone.Core.Model;
using Aqorin.Phone.Core.Retry;
using Aqorin.Phone.Sip.Internal;
using Aqorin.Phone.Sip.Media;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aqorin.Phone.Sip.Tests.Loopback;

/// <summary>Regression tests: a second call must work after the first one ended, however it ended.</summary>
[Trait("Category", "Loopback")]
[Collection("Loopback")] // real UDP sockets and timing: run these classes one at a time
public sealed class ConsecutiveCallTests : IAsyncLifetime
{
    private readonly FakeFritzBox _box = FakeFritzBox.Start();
    private readonly SipSessionContext _context = new();
    private SipRegistrationService _registration = null!;
    private SipCallService _calls = null!;

    public async Task InitializeAsync()
    {
        var diagnostics = new SipDiagnosticsOptions();
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
            NullLogger<SipCallService>.Instance)
        {
            RingTimeoutSeconds = 5,
        };

        _box.Behaviour = InviteBehaviour.RingThenAnswer;
        _box.RingDuration = TimeSpan.FromMilliseconds(150);
        await _registration.RegisterAsync(new SipAccount(
            new SipAccountSettings { Registrar = _box.Host, Port = _box.Port, Username = _box.Username, RegistrationExpirySeconds = 300 },
            _box.Password));
        Assert.Equal(RegistrationState.Registered, _registration.Status.State);
    }

    public async Task DisposeAsync()
    {
        await _calls.DisposeAsync();
        await _registration.DisposeAsync();
        await _box.DisposeAsync();
    }

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

    private async Task<CallInfo> CallAndExpectActive(string number)
    {
        var result = await _calls.PlaceCallAsync(number).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(result.State == CallState.Active, $"call to {number}: {result.State} {result.Message} {result.Detail}\n{string.Join("\n", _box.Log)}");
        await WaitFor(() => _box.HasEstablishedCall, "200 OK sent by the box");
        return result;
    }

    [Fact]
    public async Task Second_call_works_after_local_hangup()
    {
        await CallAndExpectActive("0301111");
        await _calls.HangupAsync();
        await WaitFor(() => _box.ByesReceived == 1, "BYE from the softphone");
        Assert.Equal(CallState.Idle, _calls.CurrentCall.State);
        Assert.Equal(CallEndReason.LocalHangup, _calls.CurrentCall.EndReason);

        var second = await CallAndExpectActive("0302222");

        Assert.Equal("0302222", second.RemoteParty);
        Assert.Equal(2, _box.AcceptedInvites);
        await _calls.HangupAsync();
        await WaitFor(() => _box.ByesReceived == 2, "second BYE");
        Assert.Equal(CallState.Idle, _calls.CurrentCall.State);
    }

    [Fact]
    public async Task Second_call_works_after_remote_hangup()
    {
        await CallAndExpectActive("0301111");

        _box.HangUpSoftphone();
        await WaitFor(() => _calls.CurrentCall.State == CallState.Idle, "remote BYE processed");
        Assert.Equal(CallEndReason.RemoteHangup, _calls.CurrentCall.EndReason);

        var second = await CallAndExpectActive("0302222");

        Assert.Equal("0302222", second.RemoteParty);
        Assert.Equal(2, _box.AcceptedInvites);
        await _calls.HangupAsync();
        await WaitFor(() => _box.ByesReceived == 1, "BYE for the second call");
    }

    [Fact]
    public async Task Second_call_works_after_busy_and_after_cancel()
    {
        _box.Behaviour = InviteBehaviour.RingThenBusy;
        var busy = await _calls.PlaceCallAsync("0301111");
        Assert.Equal(CallEndReason.Busy, busy.EndReason);

        _box.Behaviour = InviteBehaviour.RingThenAnswer;
        _box.RingDuration = TimeSpan.FromSeconds(3);
        var cancelled = _calls.PlaceCallAsync("0303333");
        await WaitFor(() => _calls.CurrentCall.State == CallState.Ringing, "ringing");
        await _calls.HangupAsync();
        var cancelledResult = await cancelled.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(CallState.Idle, cancelledResult.State);
        Assert.Equal(CallEndReason.LocalCancel, cancelledResult.EndReason);

        _box.RingDuration = TimeSpan.FromMilliseconds(150);
        var third = await CallAndExpectActive("0302222");
        Assert.Equal("0302222", third.RemoteParty);
        await _calls.HangupAsync();
        await WaitFor(() => _box.ByesReceived == 1, "BYE");
    }

    [Fact]
    public async Task Incoming_call_after_an_outgoing_call_is_presented()
    {
        await CallAndExpectActive("0301111");
        await _calls.HangupAsync();
        await WaitFor(() => _box.ByesReceived == 1, "BYE");

        _box.CallSoftphone("0305555", "Bob");
        await WaitFor(() => _calls.CurrentCall.State == CallState.Incoming, "incoming after outgoing");
        Assert.Equal("Bob", _calls.CurrentCall.RemoteParty);
        await _calls.RejectAsync();
        Assert.Equal(486, await _box.OutgoingInviteFinalStatus.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
