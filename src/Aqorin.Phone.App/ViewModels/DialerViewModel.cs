using System.Collections.ObjectModel;
using Aqorin.Phone.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Aqorin.Phone.Core.Abstractions;
using Aqorin.Phone.Core.Model;
using Aqorin.Phone.Core.StateMachines;
using Microsoft.Extensions.Logging;

namespace Aqorin.Phone.App.ViewModels;

/// <summary>Dialer view: destination, call/answer/reject/hang-up and the live call state.</summary>
public sealed partial class DialerViewModel : ViewModelBase, IDisposable
{
    private const int MaxRecentCalls = 100;
    private readonly ICallService _calls;
    private readonly ISipRegistrationService _registration;
    private readonly IContactStore _contacts;
    private readonly ICallHistoryStore _history;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<DialerViewModel> _logger;
    private readonly Timer _durationTimer;
    private CallInfo _call = CallInfo.Idle;
    private CallInfo _previousCall = CallInfo.Idle;

    public DialerViewModel(ICallService calls, ISipRegistrationService registration, IContactStore contacts, ICallHistoryStore history, IUiDispatcher dispatcher, ILogger<DialerViewModel> logger)
    {
        _calls = calls;
        _registration = registration;
        _contacts = contacts;
        _history = history;
        _dispatcher = dispatcher;
        _logger = logger;
        Destination = string.Empty;

        _durationTimer = new Timer(_ => _dispatcher.Post(UpdateDuration), null, Timeout.Infinite, Timeout.Infinite);

        ApplyCall(_calls.CurrentCall);
        IsRegistered = _registration.Status.IsRegistered;
        IsSpeakerEnabled = _calls.IsSpeakerEnabled;
        _calls.CallChanged += (_, info) => _dispatcher.Post(() => ApplyCall(info));
        _registration.StatusChanged += (_, status) => _dispatcher.Post(() => IsRegistered = status.IsRegistered);
    }

    public ObservableCollection<ContactItem> Contacts { get; } = [];

    public ObservableCollection<ContactItem> VisibleContacts { get; } = [];

    public ObservableCollection<CallHistoryItem> RecentCalls { get; } = [];

    public IReadOnlyList<KeypadButton> KeypadButtons { get; } =
    [
        new("1"),
        new("2"),
        new("3"),
        new("4"),
        new("5"),
        new("6"),
        new("7"),
        new("8"),
        new("9"),
        new("*"),
        new("0"),
        new("#")
    ];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CallCommand))]
    public partial string Destination { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CallCommand))]
    [NotifyPropertyChangedFor(nameof(RegistrationHint))]
    public partial bool IsRegistered { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CallCommand))]
    [NotifyCanExecuteChangedFor(nameof(AnswerCommand))]
    [NotifyCanExecuteChangedFor(nameof(RejectCommand))]
    [NotifyCanExecuteChangedFor(nameof(HangupCommand))]
    [NotifyPropertyChangedFor(nameof(StateText))]
    [NotifyPropertyChangedFor(nameof(IsIncoming))]
    [NotifyPropertyChangedFor(nameof(IsInCall))]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    [NotifyPropertyChangedFor(nameof(ShowHangup))]
    [NotifyPropertyChangedFor(nameof(IsFailed))]
    [NotifyPropertyChangedFor(nameof(DisplayRemoteParty))]
    [NotifyPropertyChangedFor(nameof(DisplayStatusMessage))]
    public partial CallState State { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveInitials))]
    [NotifyPropertyChangedFor(nameof(DisplayRemoteParty))]
    [NotifyPropertyChangedFor(nameof(DisplayStatusMessage))]
    public partial string RemoteParty { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayStatusMessage))]
    public partial string StatusMessage { get; set; } = "No call";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayRemoteParty))]
    [NotifyPropertyChangedFor(nameof(DisplayStatusMessage))]
    public partial bool ShowIncomingCallPreviews { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetail))]
    public partial string Detail { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Duration { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Codec { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string ErrorMessage { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MuteButtonText))]
    public partial bool IsMuted { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HoldButtonText))]
    public partial bool IsOnHold { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeakerButtonText))]
    public partial bool IsSpeakerEnabled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyContacts))]
    public partial bool IsAddingContact { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveContactCommand))]
    public partial string NewContactName { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveContactCommand))]
    public partial string NewContactNumber { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasContactError))]
    public partial string ContactError { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFavoritesFilterSelected))]
    [NotifyPropertyChangedFor(nameof(IsAllFilterSelected))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyContacts))]
    public partial ContactFilter SelectedContactFilter { get; set; } = ContactFilter.Favorites;

    public bool IsIncoming => State == CallState.Incoming;

    public bool IsInCall => State is CallState.Dialing or CallState.Ringing or CallState.Incoming or CallState.Connecting or CallState.Active or CallState.Ending;

    public bool IsActive => State == CallState.Active;

    public bool IsFailed => State == CallState.Failed;

    public bool ShowHangup => CallStateMachine.CanHangup(State);

    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public bool HasContactError => !string.IsNullOrWhiteSpace(ContactError);

    public bool ShowEmptyContacts => VisibleContacts.Count == 0 && !IsAddingContact;

    public bool ShowEmptyRecentCalls => RecentCalls.Count == 0;

    public bool IsFavoritesFilterSelected => SelectedContactFilter == ContactFilter.Favorites;

    public bool IsAllFilterSelected => SelectedContactFilter == ContactFilter.All;

    public string RegistrationHint => IsRegistered ? string.Empty : "Register on the Settings tab before calling.";

    public string MuteButtonText => IsMuted ? "Unmute" : "Mute";

    public string HoldButtonText => IsOnHold ? "Resume" : "Hold";

    public string SpeakerButtonText => IsSpeakerEnabled ? "Speaker on" : "Speaker";

    public string ActiveInitials => InitialsFor(string.IsNullOrWhiteSpace(RemoteParty) ? "Aqorin Phone" : RemoteParty);

    public string DisplayRemoteParty => ShouldHideIncomingPreview ? "Unknown caller" : ResolvedRemoteParty;

    public string DisplayStatusMessage => ShouldHideIncomingPreview
        ? "Incoming call"
        : State == CallState.Incoming ? $"Incoming call from {ResolvedRemoteParty}" : StatusMessage;

    public string StateText => State switch
    {
        CallState.Idle => "Idle",
        CallState.Dialing => "Dialing…",
        CallState.Ringing => "Ringing…",
        CallState.Incoming => "Incoming call",
        CallState.Connecting => "Connecting…",
        CallState.Active => "In call",
        CallState.Ending => "Ending…",
        CallState.Failed => "Call failed",
        _ => State.ToString(),
    };

    public bool CanCall => IsRegistered && CallStateMachine.CanPlaceCall(State) && !string.IsNullOrWhiteSpace(Destination);

    public bool CanAnswer => CallStateMachine.CanAnswer(State);

    public bool CanReject => CallStateMachine.CanReject(State);

    public bool CanHangup => CallStateMachine.CanHangup(State);

    public bool CanSaveContact => !string.IsNullOrWhiteSpace(NewContactName) && !string.IsNullOrWhiteSpace(NewContactNumber);

    public async Task LoadContactsAsync(CancellationToken cancellationToken = default)
    {
        var saved = await _contacts.LoadAsync(cancellationToken);
        Contacts.Clear();
        foreach (var contact in saved)
        {
            Contacts.Add(new ContactItem(contact.Name, contact.Number, contact.IsFavorite, true));
        }

        RefreshVisibleContacts();
        OnPropertyChanged(nameof(ShowEmptyContacts));
        NotifyCallerDisplayChanged();
    }

    public async Task LoadRecentCallsAsync(CancellationToken cancellationToken = default)
    {
        var saved = await _history.LoadAsync(cancellationToken);
        RecentCalls.Clear();
        foreach (var call in saved.Take(MaxRecentCalls))
        {
            RecentCalls.Add(new CallHistoryItem(call.Name, call.Number, call.Direction, call.When, call.Status, call.IsMissed));
        }

        OnPropertyChanged(nameof(ShowEmptyRecentCalls));
    }

    [RelayCommand]
    private void AppendDigit(KeypadButton key)
    {
        if (IsInCall)
        {
            return;
        }

        Destination += key.Value;
    }

    partial void OnSelectedContactFilterChanged(ContactFilter value) => RefreshVisibleContacts();

    [RelayCommand]
    private void ShowFavoriteContacts() => SelectedContactFilter = ContactFilter.Favorites;

    [RelayCommand]
    private void ShowAllContacts() => SelectedContactFilter = ContactFilter.All;

    [RelayCommand]
    private void Backspace()
    {
        if (!IsInCall && Destination.Length > 0)
        {
            Destination = Destination[..^1];
        }
    }

    [RelayCommand]
    private void ClearDestination()
    {
        if (!IsInCall)
        {
            Destination = string.Empty;
        }
    }

    [RelayCommand]
    private void SelectContact(ContactItem contact)
    {
        Destination = contact.Number;
    }

    [RelayCommand]
    private async Task CallContactAsync(ContactItem contact)
    {
        Destination = contact.Number;
        if (!CanCall)
        {
            return;
        }

        await CallAsync();
    }

    [RelayCommand]
    private async Task RemoveContactAsync(ContactItem contact)
    {
        var existing = Contacts.FirstOrDefault(c => ReferenceEquals(c, contact)
            || string.Equals(c.Number, contact.Number, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            return;
        }

        Contacts.Remove(existing);
        await SaveContactsAsync();
        RefreshVisibleContacts();
        ContactError = string.Empty;
        OnPropertyChanged(nameof(ShowEmptyContacts));
        NotifyCallerDisplayChanged();
    }

    [RelayCommand]
    private async Task CallRecentAsync(CallHistoryItem item)
    {
        Destination = item.Number;
        if (!CanCall)
        {
            return;
        }

        await CallAsync();
    }

    [RelayCommand]
    private async Task ClearRecentCallsAsync()
    {
        RecentCalls.Clear();
        await _history.ClearAsync();
        OnPropertyChanged(nameof(ShowEmptyRecentCalls));
    }

    [RelayCommand]
    private void ShowAddContact()
    {
        ContactError = string.Empty;
        NewContactName = string.Empty;
        NewContactNumber = Destination.Trim();
        IsAddingContact = true;
    }

    [RelayCommand]
    private void CancelAddContact()
    {
        ContactError = string.Empty;
        IsAddingContact = false;
    }

    [RelayCommand(CanExecute = nameof(CanSaveContact))]
    private async Task SaveContactAsync()
    {
        var name = NewContactName.Trim();
        var number = NewContactNumber.Trim();
        if (Contacts.Any(c => string.Equals(c.Number, number, StringComparison.OrdinalIgnoreCase)))
        {
            ContactError = "That number is already in contacts.";
            return;
        }

        Contacts.Add(new ContactItem(name, number, true, true));
        await SaveContactsAsync();
        RefreshVisibleContacts();
        IsAddingContact = false;
        ContactError = string.Empty;
        OnPropertyChanged(nameof(ShowEmptyContacts));
        NotifyCallerDisplayChanged();
    }

    [RelayCommand(CanExecute = nameof(CanCall))]
    private async Task CallAsync()
    {
        ErrorMessage = string.Empty;
        try
        {
            await _calls.PlaceCallAsync(Destination);
        }
        catch (InvalidDestinationException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (InvalidOperationException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Placing the call failed.");
            ErrorMessage = "Call failed: " + ex.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(CanAnswer))]
    private async Task AnswerAsync()
    {
        ErrorMessage = string.Empty;
        try
        {
            await _calls.AnswerAsync();
        }
        catch (InvalidOperationException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Answering failed.");
            ErrorMessage = "Answer failed: " + ex.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(CanReject))]
    private async Task RejectAsync()
    {
        ErrorMessage = string.Empty;
        try
        {
            await _calls.RejectAsync();
        }
        catch (InvalidOperationException ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(CanHangup))]
    private async Task HangupAsync()
    {
        ErrorMessage = string.Empty;
        try
        {
            await _calls.HangupAsync();
        }
        catch (InvalidOperationException ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task ToggleMuteAsync()
    {
        if (!IsInCall)
        {
            return;
        }

        try
        {
            await _calls.SetMutedAsync(!IsMuted);
            IsMuted = _calls.IsMuted;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Toggling mute failed.");
            ErrorMessage = "Mute failed: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task ToggleHoldAsync()
    {
        if (!IsActive)
        {
            return;
        }

        try
        {
            await _calls.SetHoldAsync(!IsOnHold);
            IsOnHold = _calls.IsOnHold;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Toggling hold failed.");
            ErrorMessage = "Hold failed: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task ToggleSpeakerAsync()
    {
        if (!IsInCall)
        {
            return;
        }

        try
        {
            await _calls.SetSpeakerAsync(!IsSpeakerEnabled);
            IsSpeakerEnabled = _calls.IsSpeakerEnabled;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Toggling speaker failed.");
            ErrorMessage = "Speaker failed: " + ex.Message;
        }
    }

    public void Dispose() => _durationTimer.Dispose();

    private void ApplyCall(CallInfo info)
    {
        if (_previousCall.IsInProgress && info.IsResting)
        {
            AddRecentCall(_previousCall with
            {
                State = info.State,
                EndedAt = info.EndedAt ?? DateTimeOffset.UtcNow,
                EndReason = info.EndReason,
                Message = info.Message,
                Detail = info.Detail
            });
        }

        _previousCall = info;
        _call = info;
        State = info.State;
        RemoteParty = info.RemoteParty;
        StatusMessage = info.Message;
        Detail = info.Detail ?? string.Empty;
        Codec = info.Codec ?? string.Empty;
        UpdateDuration();

        if (info.State == CallState.Active)
        {
            _durationTimer.Change(TimeSpan.Zero, TimeSpan.FromMilliseconds(500));
        }
        else
        {
            _durationTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        if (!info.IsInProgress)
        {
            IsMuted = false;
            IsOnHold = false;
        }
        else
        {
            IsMuted = _calls.IsMuted;
            IsOnHold = _calls.IsOnHold;
        }

        IsSpeakerEnabled = _calls.IsSpeakerEnabled;
        NotifyCallerDisplayChanged();
    }

    private void UpdateDuration()
    {
        var duration = _call.Duration(DateTimeOffset.UtcNow);
        Duration = duration is { } d ? d.ToString(d.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss") : string.Empty;
    }

    private void AddRecentCall(CallInfo call)
    {
        if (string.IsNullOrWhiteSpace(call.RemoteParty))
        {
            return;
        }

        var missed = call.Direction == CallDirection.Incoming && call.ConnectedAt is null;
        var number = ContactNumberFor(call.RemoteParty);
        var status = missed
            ? "Missed"
            : call.Duration(call.EndedAt ?? DateTimeOffset.UtcNow) is { } duration
                ? duration.ToString(duration.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss")
                : call.EndReason == CallEndReason.Busy ? "Busy" : call.EndReason == CallEndReason.Timeout ? "No answer" : "Ended";

        RecentCalls.Insert(0, new CallHistoryItem(
            call.RemoteParty,
            number,
            call.Direction,
            call.StartedAt?.ToLocalTime().ToString("MMM d, HH:mm") ?? DateTimeOffset.Now.ToString("MMM d, HH:mm"),
            status,
            missed));

        OnPropertyChanged(nameof(ShowEmptyRecentCalls));

        while (RecentCalls.Count > MaxRecentCalls)
        {
            RecentCalls.RemoveAt(RecentCalls.Count - 1);
        }

        _ = SaveRecentCallsAsync();
    }

    private string ContactNumberFor(string party) =>
        Contacts.FirstOrDefault(c => string.Equals(c.Name, party, StringComparison.OrdinalIgnoreCase))?.Number ?? party;

    private bool ShouldHideIncomingPreview => State == CallState.Incoming && !ShowIncomingCallPreviews;

    private string ResolvedRemoteParty =>
        ContactForRemoteParty()?.Name
        ?? (string.IsNullOrWhiteSpace(RemoteParty) ? "Unknown caller" : RemoteParty);

    private ContactItem? ContactForRemoteParty()
    {
        var candidates = RemotePartyCandidates().ToArray();
        if (candidates.Length == 0)
        {
            return null;
        }

        return Contacts.FirstOrDefault(contact => candidates.Any(candidate =>
            string.Equals(contact.Name, candidate, StringComparison.OrdinalIgnoreCase)
            || string.Equals(contact.Number, candidate, StringComparison.OrdinalIgnoreCase)
            || SameDialString(contact.Number, candidate)));
    }

    private IEnumerable<string> RemotePartyCandidates()
    {
        if (!string.IsNullOrWhiteSpace(_call.RemoteParty))
        {
            yield return _call.RemoteParty.Trim();
        }

        if (!string.IsNullOrWhiteSpace(_call.RemoteUri))
        {
            var userPart = SipUriUserPart(_call.RemoteUri);
            if (!string.IsNullOrWhiteSpace(userPart))
            {
                yield return userPart;
            }
        }
    }

    private static string SipUriUserPart(string uri)
    {
        var value = uri.Trim();
        var scheme = value.IndexOf(':');
        if (scheme >= 0)
        {
            value = value[(scheme + 1)..];
        }

        var at = value.IndexOf('@');
        if (at >= 0)
        {
            value = value[..at];
        }

        var parameter = value.IndexOf(';');
        if (parameter >= 0)
        {
            value = value[..parameter];
        }

        return value.Trim();
    }

    private static bool SameDialString(string left, string right)
    {
        var normalizedLeft = NormalizeDialString(left);
        var normalizedRight = NormalizeDialString(right);
        return normalizedLeft.Length > 0
            && string.Equals(normalizedLeft, normalizedRight, StringComparison.Ordinal);
    }

    private static string NormalizeDialString(string value)
    {
        Span<char> buffer = stackalloc char[value.Length];
        var count = 0;
        foreach (var ch in value)
        {
            if (char.IsDigit(ch))
            {
                buffer[count++] = ch;
            }
        }

        return new string(buffer[..count]);
    }

    private void NotifyCallerDisplayChanged()
    {
        OnPropertyChanged(nameof(DisplayRemoteParty));
        OnPropertyChanged(nameof(DisplayStatusMessage));
    }

    private Task SaveContactsAsync() =>
        _contacts.SaveAsync(Contacts.Select(c => new ContactEntry(c.Name, c.Number, c.IsFavorite)));

    public Task SaveRecentCallsAsync() =>
        _history.SaveAsync(RecentCalls.Select(c => new CallHistoryEntry(c.Name, c.Number, c.Direction, c.When, c.Status, c.IsMissed)));

    private void RefreshVisibleContacts()
    {
        VisibleContacts.Clear();
        foreach (var contact in Contacts.Where(c => SelectedContactFilter == ContactFilter.All || c.IsFavorite))
        {
            VisibleContacts.Add(contact);
        }

        OnPropertyChanged(nameof(ShowEmptyContacts));
    }

    private static string InitialsFor(string text)
    {
        var parts = text.Split([' ', '.', '@', '-'], StringSplitOptions.RemoveEmptyEntries);
        return string.Concat(parts.Take(2).Select(part => part[0])).ToUpperInvariant();
    }
}

public enum ContactFilter
{
    Favorites,
    All
}

public sealed record KeypadButton(string Value);

public sealed record ContactItem(string Name, string Number, bool IsFavorite, bool IsAvailable)
{
    public string Initials => string.Concat(Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(part => part[0])).ToUpperInvariant();
}

public sealed record CallHistoryItem(string Name, string Number, CallDirection Direction, string When, string Status, bool IsMissed)
{
    public string DirectionText => Direction == CallDirection.Incoming ? "In" : "Out";
}
