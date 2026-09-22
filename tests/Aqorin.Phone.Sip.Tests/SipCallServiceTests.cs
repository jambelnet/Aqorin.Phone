using Aqorin.Phone.Core.Abstractions;
using Aqorin.Phone.Core.Model;
using Aqorin.Phone.Sip.Internal;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aqorin.Phone.Sip.Tests;

public class SipCallServiceTests
{
    private static readonly SipAccount Account = new(new SipAccountSettings { Registrar = "fritz.box", Username = "620", DisplayName = "Desk" }, "pw");

    private readonly FakeUserAgentFactory _agents = new();
    private readonly FakeMediaSessionFactory _media = new();
    private readonly SipSessionContext _context = new();
    private readonly List<CallInfo> _events = [];

    private SipCallService CreateRegistered()
    {
        var service = new SipCallService(_agents, _media, _context, NullLogger<SipCallService>.Instance);
        service.CallChanged += (_, c) => { lock (_events) { _events.Add(c); } };
        _context.Open(new FakeTransportHandle(Account.Settings), Account);
        _context.IsRegistered = true;
        return service;
    }

    private FakeUserAgent Agent => _agents.Current;

    [Fact]
    public async Task Calling_before_registration_is_rejected()
    {
        await using var service = new SipCallService(_agents, _media, _context, NullLogger<SipCallService>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PlaceCallAsync("0301234"));
        Assert.Equal(CallState.Idle, service.CurrentCall.State);
        Assert.Empty(_media.Created);
    }

    [Fact]
    public async Task Invalid_destination_is_rejected_without_state_change()
    {
        await using var service = CreateRegistered();

        await Assert.ThrowsAsync<InvalidDestinationException>(() => service.PlaceCallAsync("abc"));
        Assert.Equal(CallState.Idle, service.CurrentCall.State);
        Assert.Empty(_media.Created);
    }

    [Fact]
    public async Task Outgoing_call_flows_dialing_ringing_active_and_local_hangup()
    {
        await using var service = CreateRegistered();

        var callTask = service.PlaceCallAsync("+49 30 123456");
        await Wait.Until(() => Agent.PendingCall is not null, what: "INVITE sent");

        Assert.Equal(CallState.Dialing, service.CurrentCall.State);
        Assert.Equal(CallDirection.Outgoing, service.CurrentCall.Direction);
        Assert.Equal("004930123456", service.CurrentCall.RemoteParty);
        Assert.Equal("sip:004930123456@fritz.box", Agent.LastDestination);
        Assert.Same(Account, Agent.LastAccount);
        Assert.Same(_media.Created.Single(), Agent.LastMedia);

        Agent.RemoteRinging();
        Assert.Equal(CallState.Ringing, service.CurrentCall.State);

        Agent.RemoteAnswers();
        var result = await callTask;
        Assert.Equal(CallState.Active, result.State);
        Assert.NotNull(result.ConnectedAt);
        Assert.Equal("PCMA", result.Codec);

        await service.HangupAsync();
        Assert.Equal(1, Agent.HangupCalls);
        Assert.Equal(CallState.Idle, service.CurrentCall.State);
        Assert.Equal(CallEndReason.LocalHangup, service.CurrentCall.EndReason);
        Assert.NotNull(service.CurrentCall.EndedAt);
        await Wait.Until(() => _media.Created.Single().IsClosed, what: "media closed");

        await Wait.Until(() => { lock (_events) { return _events.Count > 0 && _events[^1].State == CallState.Idle; } }, what: "events");
        CallState[] observed;
        lock (_events)
        {
            observed = _events.Select(e => e.State).Distinct().ToArray();
        }

        Assert.Equal([CallState.Dialing, CallState.Ringing, CallState.Connecting, CallState.Active, CallState.Ending, CallState.Idle], observed);
    }

    [Fact]
    public async Task Busy_response_ends_in_failed_with_reason()
    {
        await using var service = CreateRegistered();
        var callTask = service.PlaceCallAsync("0301234");
        await Wait.Until(() => Agent.PendingCall is not null);

        Agent.RemoteFails(486, "Busy Here");
        var result = await callTask;

        Assert.Equal(CallState.Failed, result.State);
        Assert.Equal(CallEndReason.Busy, result.EndReason);
        Assert.Equal("Busy", result.Message);
        Assert.Equal("486 Busy Here", result.Detail);
        await Wait.Until(() => _media.Created.Single().IsClosed, what: "media closed");

        // A new call may be placed from Failed.
        var second = service.PlaceCallAsync("0301234");
        await Wait.Until(() => Agent.PendingCall is not null && !Agent.PendingCall.Task.IsCompleted);
        Assert.Equal(CallState.Dialing, service.CurrentCall.State);
        Agent.RemoteFails(603, "Decline");
        Assert.Equal(CallEndReason.Rejected, (await second).EndReason);
    }

    [Fact]
    public async Task Timeout_and_not_found_are_mapped()
    {
        await using var service = CreateRegistered();
        var t1 = service.PlaceCallAsync("0301234");
        await Wait.Until(() => Agent.PendingCall is not null);
        Agent.RemoteFails(408, "Request Timeout");
        Assert.Equal(CallEndReason.Timeout, (await t1).EndReason);

        var t2 = service.PlaceCallAsync("0301234");
        await Wait.Until(() => Agent.PendingCall is not null && !Agent.PendingCall.Task.IsCompleted);
        Agent.RemoteFails(404, "Not Found");
        Assert.Equal(CallEndReason.NotFound, (await t2).EndReason);
    }

    [Fact]
    public async Task Cancelling_an_outgoing_call_returns_to_idle()
    {
        await using var service = CreateRegistered();
        var callTask = service.PlaceCallAsync("0301234");
        await Wait.Until(() => Agent.PendingCall is not null);
        Agent.RemoteRinging();

        await service.HangupAsync();
        var result = await callTask;

        Assert.Equal(1, Agent.CancelCalls);
        Assert.Equal(0, Agent.HangupCalls);
        Assert.Equal(CallState.Idle, result.State);
        Assert.Equal(CallEndReason.LocalCancel, result.EndReason);
    }

    [Fact]
    public async Task Cancellation_token_cancels_the_outgoing_call()
    {
        await using var service = CreateRegistered();
        using var cts = new CancellationTokenSource();
        var callTask = service.PlaceCallAsync("0301234", cts.Token);
        await Wait.Until(() => Agent.PendingCall is not null);

        cts.Cancel();
        var result = await callTask;

        Assert.Equal(1, Agent.CancelCalls);
        Assert.Equal(CallEndReason.LocalCancel, result.EndReason);
    }

    [Fact]
    public async Task Remote_hangup_during_active_call_returns_to_idle()
    {
        await using var service = CreateRegistered();
        var callTask = service.PlaceCallAsync("0301234");
        await Wait.Until(() => Agent.PendingCall is not null);
        Agent.RemoteAnswers();
        await callTask;

        Agent.RemoteHangsUp();

        Assert.Equal(CallState.Idle, service.CurrentCall.State);
        Assert.Equal(CallEndReason.RemoteHangup, service.CurrentCall.EndReason);
        Assert.Equal("The other party hung up", service.CurrentCall.Message);
        await Wait.Until(() => _media.Created.Single().IsClosed, what: "media closed");
    }

    [Fact]
    public async Task Second_call_while_active_is_prevented()
    {
        await using var service = CreateRegistered();
        var callTask = service.PlaceCallAsync("0301234");
        await Wait.Until(() => Agent.PendingCall is not null);
        Agent.RemoteAnswers();
        await callTask;

        var ex = await Assert.ThrowsAsync<InvalidCallOperationException>(() => service.PlaceCallAsync("0305678"));
        Assert.Equal(CallState.Active, ex.State);
        Assert.Single(_media.Created);
    }

    [Fact]
    public async Task Incoming_call_rings_can_be_answered_and_ended_remotely()
    {
        await using var service = CreateRegistered();
        var incoming = new FakeIncomingCall("sip:0301234@fritz.box", "Alice");

        Agent.RemoteCalls(incoming);

        Assert.True(incoming.RingingStarted);
        Assert.Equal(CallState.Incoming, service.CurrentCall.State);
        Assert.Equal(CallDirection.Incoming, service.CurrentCall.Direction);
        Assert.Equal("Alice", service.CurrentCall.RemoteParty);
        Assert.Equal("sip:0301234@fritz.box", service.CurrentCall.RemoteUri);

        await service.AnswerAsync();

        Assert.Same(_media.Created.Single(), incoming.AnsweredWith);
        Assert.Equal(CallState.Active, service.CurrentCall.State);
        Assert.NotNull(service.CurrentCall.ConnectedAt);

        Agent.RemoteHangsUp();
        Assert.Equal(CallState.Idle, service.CurrentCall.State);
        Assert.Equal(CallEndReason.RemoteHangup, service.CurrentCall.EndReason);
        await Wait.Until(() => _media.Created.Single().IsClosed, what: "media closed");
    }

    [Fact]
    public async Task Incoming_call_without_display_name_uses_the_number()
    {
        await using var service = CreateRegistered();
        Agent.RemoteCalls(new FakeIncomingCall("sip:0301234@fritz.box"));
        Assert.Equal("0301234", service.CurrentCall.RemoteParty);
    }

    [Fact]
    public async Task Incoming_call_can_be_rejected()
    {
        await using var service = CreateRegistered();
        var incoming = new FakeIncomingCall("sip:0301234@fritz.box");
        Agent.RemoteCalls(incoming);

        await service.RejectAsync();

        Assert.Equal((486, "Busy Here"), incoming.Rejected);
        Assert.Equal(CallState.Idle, service.CurrentCall.State);
        Assert.Equal(CallEndReason.LocalReject, service.CurrentCall.EndReason);
        Assert.Empty(_media.Created);
    }

    [Fact]
    public async Task Incoming_call_cancelled_by_caller_returns_to_idle()
    {
        await using var service = CreateRegistered();
        var incoming = new FakeIncomingCall("sip:0301234@fritz.box");
        Agent.RemoteCalls(incoming);

        incoming.RaiseCancelled();

        Assert.Equal(CallState.Idle, service.CurrentCall.State);
        Assert.Equal(CallEndReason.RemoteCancel, service.CurrentCall.EndReason);
    }

    [Fact]
    public async Task Answer_failure_ends_in_failed_and_closes_media()
    {
        await using var service = CreateRegistered();
        var incoming = new FakeIncomingCall("sip:0301234@fritz.box") { AnswerResult = false };
        Agent.RemoteCalls(incoming);

        await service.AnswerAsync();

        Assert.Equal(CallState.Failed, service.CurrentCall.State);
        Assert.Equal(CallEndReason.MediaFailure, service.CurrentCall.EndReason);
        await Wait.Until(() => _media.Created.Single().IsClosed, what: "media closed");
    }

    [Fact]
    public async Task Incoming_call_during_another_call_is_answered_busy()
    {
        await using var service = CreateRegistered();
        var callTask = service.PlaceCallAsync("0301234");
        await Wait.Until(() => Agent.PendingCall is not null);
        Agent.RemoteAnswers();
        await callTask;

        var second = new FakeIncomingCall("sip:0305678@fritz.box");
        Agent.RemoteCalls(second);

        Assert.Contains(second, Agent.BusyRejections);
        Assert.False(second.RingingStarted);
        Assert.Equal(CallState.Active, service.CurrentCall.State);
        Assert.Equal("0301234", service.CurrentCall.RemoteParty);
    }

    [Fact]
    public async Task Answer_reject_and_hangup_without_a_call_are_invalid()
    {
        await using var service = CreateRegistered();

        await Assert.ThrowsAsync<InvalidCallOperationException>(() => service.AnswerAsync());
        await Assert.ThrowsAsync<InvalidCallOperationException>(() => service.RejectAsync());
        await Assert.ThrowsAsync<InvalidCallOperationException>(() => service.HangupAsync());
        Assert.Equal(CallState.Idle, service.CurrentCall.State);
    }

    [Fact]
    public async Task Audio_errors_and_codec_negotiation_are_reflected_in_the_call_info()
    {
        await using var service = CreateRegistered();
        var callTask = service.PlaceCallAsync("0301234");
        await Wait.Until(() => Agent.PendingCall is not null);
        var media = _media.Created.Single();

        media.RaiseCodec("PCMU");
        media.RaiseAudioError("Microphone unavailable");
        Agent.RemoteAnswers();
        await callTask;

        Assert.Equal("PCMU", service.CurrentCall.Codec);
        Assert.Contains("Microphone unavailable", service.CurrentCall.Detail);
    }

    [Fact]
    public async Task Mute_hold_and_speaker_are_applied_to_the_active_media_session()
    {
        await using var service = CreateRegistered();
        service.MediaOptions = new AudioMediaSessionOptions { OutputDeviceId = "headset-output" };

        var callTask = service.PlaceCallAsync("0301234");
        await Wait.Until(() => Agent.PendingCall is not null);
        var media = _media.Created.Single();
        Agent.RemoteAnswers();
        await callTask;

        Assert.Equal("headset-output", media.OutputDeviceId);

        await service.SetMutedAsync(true);
        await service.SetHoldAsync(true);
        await service.SetSpeakerAsync(true);

        Assert.True(service.IsMuted);
        Assert.True(service.IsOnHold);
        Assert.True(service.IsSpeakerEnabled);
        Assert.True(media.IsMuted);
        Assert.True(media.IsHeld);
        Assert.True(media.IsSpeakerEnabled);
        Assert.Equal("headset-output", media.OutputDeviceId);

        await service.SetSpeakerAsync(false);

        Assert.False(service.IsSpeakerEnabled);
        Assert.False(media.IsSpeakerEnabled);
        Assert.Equal("headset-output", media.OutputDeviceId);
    }

    [Fact]
    public async Task Closing_the_transport_ends_the_call_and_disposes_the_agent()
    {
        await using var service = CreateRegistered();
        var callTask = service.PlaceCallAsync("0301234");
        await Wait.Until(() => Agent.PendingCall is not null);
        Agent.RemoteAnswers();
        await callTask;
        var agent = Agent;

        _context.Close();

        Assert.Equal(CallState.Idle, service.CurrentCall.State);
        Assert.True(agent.Disposed);
        await Wait.Until(() => _media.Created.Single().IsClosed, what: "media closed");
    }

    [Fact]
    public async Task Dispose_hangs_up_and_releases_resources()
    {
        var service = CreateRegistered();
        var callTask = service.PlaceCallAsync("0301234");
        await Wait.Until(() => Agent.PendingCall is not null);
        Agent.RemoteAnswers();
        await callTask;
        var agent = Agent;

        await service.DisposeAsync();

        Assert.Equal(1, agent.HangupCalls);
        Assert.True(agent.Disposed);
        await Wait.Until(() => _media.Created.Single().IsClosed, what: "media closed");
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.PlaceCallAsync("0301234"));
    }
}

public class SipResponseMapperTests
{
    [Theory]
    [InlineData(486, "Busy Here", CallEndReason.Busy)]
    [InlineData(600, "Busy Everywhere", CallEndReason.Busy)]
    [InlineData(603, "Decline", CallEndReason.Rejected)]
    [InlineData(404, "Not Found", CallEndReason.NotFound)]
    [InlineData(484, "Address Incomplete", CallEndReason.NotFound)]
    [InlineData(408, "Request Timeout", CallEndReason.Timeout)]
    [InlineData(480, "Temporarily Unavailable", CallEndReason.Timeout)]
    [InlineData(487, "Request Terminated", CallEndReason.LocalCancel)]
    [InlineData(488, "Not Acceptable Here", CallEndReason.MediaFailure)]
    [InlineData(401, "Unauthorized", CallEndReason.Unauthorized)]
    [InlineData(503, "Service Unavailable", CallEndReason.TransportFailure)]
    [InlineData(400, "Bad Request", CallEndReason.Rejected)]
    public void Status_codes_map_to_reasons(int status, string phrase, CallEndReason expected)
    {
        var (reason, message) = SipResponseMapper.MapFailure(status, phrase, null);
        Assert.Equal(expected, reason);
        Assert.False(string.IsNullOrWhiteSpace(message));
    }

    [Theory]
    [InlineData("Registration to fritz.box timed out.", CallEndReason.Timeout)]
    [InlineData("Could not resolve destination when placing call", CallEndReason.TransportFailure)]
    [InlineData("Could not generate an offer.", CallEndReason.MediaFailure)]
    [InlineData("Call cancelled", CallEndReason.LocalCancel)]
    [InlineData("something odd", CallEndReason.Error)]
    public void Stack_messages_without_status_are_classified(string message, CallEndReason expected)
    {
        var (reason, _) = SipResponseMapper.MapFailure(null, null, message);
        Assert.Equal(expected, reason);
    }
}
