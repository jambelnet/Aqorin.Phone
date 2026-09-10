using Aqorin.Phone.App.Services;
using Aqorin.Phone.App.ViewModels;
using Aqorin.Phone.Core.Abstractions;
using Aqorin.Phone.Core.Model;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aqorin.Phone.Core.Tests.ViewModels;

public class DialerViewModelTests
{
    private readonly FakeCallService _calls = new();
    private readonly FakeRegistrationService _registration = new();
    private readonly InMemoryContactStore _contacts = new();
    private readonly InMemoryCallHistoryStore _history = new();

    private DialerViewModel Create() =>
        new(_calls, _registration, _contacts, _history, new ImmediateDispatcher(), NullLogger<DialerViewModel>.Instance);

    private void Registered() => _registration.Set(new RegistrationStatus { State = RegistrationState.Registered, Message = "Registered" });

    [Fact]
    public void Call_is_disabled_until_registered_and_a_destination_is_entered()
    {
        using var vm = Create();
        Assert.False(vm.CallCommand.CanExecute(null));
        Assert.Contains("Register", vm.RegistrationHint);

        vm.Destination = "0301234";
        Assert.False(vm.CallCommand.CanExecute(null));

        Registered();
        Assert.True(vm.CallCommand.CanExecute(null));
        Assert.Equal(string.Empty, vm.RegistrationHint);

        vm.Destination = "  ";
        Assert.False(vm.CallCommand.CanExecute(null));
    }

    [Fact]
    public async Task Placing_a_call_disables_call_and_enables_hangup()
    {
        using var vm = Create();
        Registered();
        vm.Destination = "0301234";

        await vm.CallCommand.ExecuteAsync(null);

        Assert.Equal(["0301234"], _calls.PlacedCalls);
        Assert.Equal(CallState.Dialing, vm.State);
        Assert.False(vm.CallCommand.CanExecute(null));
        Assert.True(vm.HangupCommand.CanExecute(null));
        Assert.False(vm.AnswerCommand.CanExecute(null));
        Assert.False(vm.RejectCommand.CanExecute(null));
        Assert.True(vm.IsInCall);
        Assert.True(vm.ShowHangup);
    }

    [Fact]
    public void Incoming_call_enables_answer_and_reject_only()
    {
        using var vm = Create();
        Registered();
        _calls.Set(new CallInfo { State = CallState.Incoming, RemoteParty = "0301234", Direction = CallDirection.Incoming, Message = "Incoming" });

        Assert.True(vm.IsIncoming);
        Assert.True(vm.AnswerCommand.CanExecute(null));
        Assert.True(vm.RejectCommand.CanExecute(null));
        Assert.False(vm.HangupCommand.CanExecute(null));
        Assert.False(vm.CallCommand.CanExecute(null));
        Assert.Equal("0301234", vm.RemoteParty);
        Assert.Equal("Incoming call", vm.StateText);
    }

    [Fact]
    public async Task Answering_moves_to_active_and_shows_duration()
    {
        using var vm = Create();
        Registered();
        _calls.Set(new CallInfo { State = CallState.Incoming, RemoteParty = "0301234", Direction = CallDirection.Incoming });

        await vm.AnswerCommand.ExecuteAsync(null);

        Assert.Equal(1, _calls.Answers);
        Assert.Equal(CallState.Active, vm.State);
        Assert.True(vm.IsActive);
        Assert.True(vm.HangupCommand.CanExecute(null));
        Assert.False(vm.AnswerCommand.CanExecute(null));
        Assert.Matches(@"^\d\d:\d\d$", vm.Duration);

        await vm.HangupCommand.ExecuteAsync(null);
        Assert.Equal(1, _calls.Hangups);
        Assert.Equal(CallState.Idle, vm.State);
        Assert.False(vm.HangupCommand.CanExecute(null));
        Assert.False(vm.CallCommand.CanExecute(null)); // no destination entered
        vm.Destination = "0301234";
        Assert.True(vm.CallCommand.CanExecute(null));
    }

    [Fact]
    public async Task Mute_hold_and_speaker_commands_reach_the_call_service()
    {
        using var vm = Create();
        Registered();
        _calls.Set(new CallInfo { State = CallState.Active, RemoteParty = "0301234", Direction = CallDirection.Outgoing, ConnectedAt = DateTimeOffset.UtcNow });

        await vm.ToggleMuteCommand.ExecuteAsync(null);
        await vm.ToggleHoldCommand.ExecuteAsync(null);
        await vm.ToggleSpeakerCommand.ExecuteAsync(null);

        Assert.True(_calls.IsMuted);
        Assert.True(_calls.IsOnHold);
        Assert.True(_calls.IsSpeakerEnabled);
        Assert.Equal("Unmute", vm.MuteButtonText);
        Assert.Equal("Resume", vm.HoldButtonText);
        Assert.Equal("Speaker on", vm.SpeakerButtonText);
    }

    [Fact]
    public async Task Rejecting_returns_to_idle()
    {
        using var vm = Create();
        Registered();
        _calls.Set(new CallInfo { State = CallState.Incoming, RemoteParty = "0301234" });

        await vm.RejectCommand.ExecuteAsync(null);

        Assert.Equal(1, _calls.Rejects);
        Assert.Equal(CallState.Idle, vm.State);
    }

    [Fact]
    public async Task Invalid_destination_error_is_shown_not_thrown()
    {
        using var vm = Create();
        Registered();
        vm.Destination = "abc";
        _calls.ThrowOnPlaceCall = new InvalidDestinationException("The telephone number may only contain digits.");

        await vm.CallCommand.ExecuteAsync(null);

        Assert.True(vm.HasError);
        Assert.Contains("digits", vm.ErrorMessage);
        Assert.Equal(CallState.Idle, vm.State);
    }

    [Fact]
    public void Failed_call_shows_reason_and_allows_a_new_call()
    {
        using var vm = Create();
        Registered();
        vm.Destination = "0301234";
        _calls.Set(new CallInfo { State = CallState.Failed, RemoteParty = "0301234", Message = "Busy", Detail = "486 Busy Here", EndReason = CallEndReason.Busy });

        Assert.True(vm.IsFailed);
        Assert.Equal("Busy", vm.StatusMessage);
        Assert.Equal("486 Busy Here", vm.Detail);
        Assert.True(vm.HasDetail);
        Assert.True(vm.CallCommand.CanExecute(null));
        Assert.False(vm.HangupCommand.CanExecute(null));
    }

    [Fact]
    public void Losing_registration_disables_calling()
    {
        using var vm = Create();
        Registered();
        vm.Destination = "0301234";
        Assert.True(vm.CallCommand.CanExecute(null));

        _registration.Set(new RegistrationStatus { State = RegistrationState.Failed, Message = "Failed" });

        Assert.False(vm.CallCommand.CanExecute(null));
    }

    [Fact]
    public async Task Contact_add_flow_saves_a_real_contact()
    {
        using var vm = Create();

        vm.ShowAddContactCommand.Execute(null);
        vm.NewContactName = "Alice Example";
        vm.NewContactNumber = "1007";
        await vm.SaveContactCommand.ExecuteAsync(null);

        Assert.Single(vm.Contacts);
        Assert.Equal("Alice Example", vm.Contacts[0].Name);
        Assert.Equal("1007", vm.Contacts[0].Number);
        Assert.Single(_contacts.Saved);
    }

    [Fact]
    public async Task Contact_filter_switches_between_favorites_and_all()
    {
        _contacts.Saved.Add(new ContactEntry("Favorite Person", "1001", IsFavorite: true));
        _contacts.Saved.Add(new ContactEntry("All Only", "1002", IsFavorite: false));
        using var vm = Create();

        await vm.LoadContactsAsync();
        Assert.Single(vm.VisibleContacts);

        vm.ShowAllContactsCommand.Execute(null);
        Assert.Equal(2, vm.VisibleContacts.Count);

        vm.ShowFavoriteContactsCommand.Execute(null);
        Assert.Single(vm.VisibleContacts);
        Assert.Equal("Favorite Person", vm.VisibleContacts[0].Name);
    }

    [Fact]
    public async Task Incoming_call_preview_resolves_contact_name_and_can_be_hidden()
    {
        _contacts.Saved.Add(new ContactEntry("Alice Example", "1001", IsFavorite: true));
        using var vm = Create();
        await vm.LoadContactsAsync();

        _calls.Set(new CallInfo
        {
            State = CallState.Incoming,
            RemoteParty = "1001",
            RemoteUri = "sip:1001@pbx.local",
            Direction = CallDirection.Incoming,
            Message = "Incoming call from 1001"
        });

        Assert.Equal("Alice Example", vm.DisplayRemoteParty);
        Assert.Equal("Incoming call from Alice Example", vm.DisplayStatusMessage);

        vm.ShowIncomingCallPreviews = false;

        Assert.Equal("Unknown caller", vm.DisplayRemoteParty);
        Assert.Equal("Incoming call", vm.DisplayStatusMessage);
    }

    [Fact]
    public async Task Recent_calls_are_loaded_from_history_store()
    {
        _history.Saved.Add(new CallHistoryEntry("Alice", "1001", CallDirection.Incoming, "Sep 10, 08:15", "Missed", IsMissed: true));
        using var vm = Create();

        await vm.LoadRecentCallsAsync();

        var call = Assert.Single(vm.RecentCalls);
        Assert.Equal("Alice", call.Name);
        Assert.Equal("1001", call.Number);
        Assert.True(call.IsMissed);
        Assert.False(vm.ShowEmptyRecentCalls);
    }

    [Fact]
    public async Task Calling_a_recent_call_places_the_call()
    {
        _history.Saved.Add(new CallHistoryEntry("Alice", "1001", CallDirection.Incoming, "Sep 10, 08:15", "Missed", IsMissed: true));
        using var vm = Create();
        Registered();
        await vm.LoadRecentCallsAsync();

        await vm.CallRecentCommand.ExecuteAsync(vm.RecentCalls[0]);

        Assert.Equal("1001", vm.Destination);
        Assert.Equal(["1001"], _calls.PlacedCalls);
        Assert.Equal(CallState.Dialing, vm.State);
    }

    [Fact]
    public async Task Completed_calls_are_saved_to_history_store()
    {
        using var vm = Create();
        _calls.Set(new CallInfo
        {
            State = CallState.Active,
            RemoteParty = "0301234",
            Direction = CallDirection.Outgoing,
            StartedAt = DateTimeOffset.Now.AddMinutes(-1),
            ConnectedAt = DateTimeOffset.Now.AddMinutes(-1),
            Message = "Connected"
        });

        _calls.Set(CallInfo.Idle);
        await vm.SaveRecentCallsAsync();

        var call = Assert.Single(_history.Saved);
        Assert.Equal("0301234", call.Number);
        Assert.Equal(CallDirection.Outgoing, call.Direction);
        Assert.False(call.IsMissed);
    }

    [Fact]
    public async Task Clear_recent_calls_removes_visible_and_persisted_history()
    {
        _history.Saved.Add(new CallHistoryEntry("Alice", "1001", CallDirection.Incoming, "Sep 10, 08:15", "Missed", IsMissed: true));
        using var vm = Create();
        await vm.LoadRecentCallsAsync();

        await vm.ClearRecentCallsCommand.ExecuteAsync(null);

        Assert.Empty(vm.RecentCalls);
        Assert.True(vm.ShowEmptyRecentCalls);
        Assert.Empty(_history.Saved);
        Assert.Equal(1, _history.ClearCalls);
    }
}
