using Aqorin.Phone.App.ViewModels;
using Aqorin.Phone.Core.Abstractions;
using Aqorin.Phone.Core.Diagnostics;
using Aqorin.Phone.Core.Model;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aqorin.Phone.Core.Tests.ViewModels;

public class MainWindowViewModelTests
{
    private readonly FakeCallService _calls = new();
    private readonly FakeRegistrationService _registration = new();
    private readonly InMemorySettingsStore _settings = new();
    private readonly InMemoryContactStore _contacts = new();
    private readonly InMemoryCallHistoryStore _history = new();
    private readonly InMemoryUiPreferencesStore _preferences = new();
    private readonly FakeRingtonePlayer _ringtone = new();
    private readonly FakeDiagnosticsSwitch _diagnostics = new();
    private readonly FakeAudioDeviceService _audio = new();
    private readonly ImmediateDispatcher _dispatcher = new();

    private MainWindowViewModel Create()
    {
        var account = new AccountViewModel(_registration, _settings, _diagnostics, _audio, _calls, _dispatcher, NullLogger<AccountViewModel>.Instance);
        var dialer = new DialerViewModel(_calls, _registration, _contacts, _history, _dispatcher, NullLogger<DialerViewModel>.Instance);
        var diagnostics = new DiagnosticsViewModel(new DiagnosticsLog(), _audio, _dispatcher);
        return new MainWindowViewModel(account, dialer, diagnostics, _registration, _calls, _audio, _preferences, _ringtone, _dispatcher);
    }

    [Fact]
    public async Task Theme_is_loaded_and_saved()
    {
        _preferences.Saved = new(IsDarkTheme: true);
        var vm = Create();

        await vm.LoadPreferencesAsync();

        Assert.True(vm.IsDarkTheme);
        Assert.Equal("Switch to light theme", vm.ThemeToolTip);

        await vm.ToggleThemeCommand.ExecuteAsync(null);

        Assert.False(vm.IsDarkTheme);
        Assert.False(_preferences.Saved.IsDarkTheme);
        Assert.Equal(1, _preferences.SaveCalls);
    }

    [Fact]
    public async Task Incoming_call_ringtone_follows_saved_preference()
    {
        _preferences.Saved = new(PlayIncomingCallSound: true);
        var vm = Create();
        await vm.LoadPreferencesAsync();

        _calls.Set(new CallInfo { State = CallState.Incoming, RemoteParty = "1001", Direction = CallDirection.Incoming, Message = "Incoming call from 1001" });

        Assert.True(_ringtone.IsPlaying);
        Assert.Equal(1, _ringtone.StartCalls);

        _calls.Set(_calls.CurrentCall with { State = CallState.Active, ConnectedAt = DateTimeOffset.UtcNow, Message = "Connected" });

        Assert.False(_ringtone.IsPlaying);
        Assert.True(_ringtone.StopCalls > 0);
    }

    [Fact]
    public async Task Incoming_call_ringtone_can_be_disabled()
    {
        _preferences.Saved = new(PlayIncomingCallSound: false);
        var vm = Create();
        await vm.LoadPreferencesAsync();

        _calls.Set(new CallInfo { State = CallState.Incoming, RemoteParty = "1001", Direction = CallDirection.Incoming, Message = "Incoming call from 1001" });

        Assert.False(_ringtone.IsPlaying);
        Assert.Equal(0, _ringtone.StartCalls);
    }

    [Fact]
    public async Task Incoming_preview_preference_is_loaded_saved_and_masks_status()
    {
        _preferences.Saved = new(ShowIncomingCallPreviews: false, PlayIncomingCallSound: false);
        var vm = Create();
        await vm.LoadPreferencesAsync();

        Assert.False(vm.Account.ShowIncomingCallPreviews);
        Assert.False(vm.Dialer.ShowIncomingCallPreviews);

        _registration.Set(new RegistrationStatus { State = RegistrationState.Registered, Message = "Registered" });
        _calls.Set(new CallInfo { State = CallState.Incoming, RemoteParty = "1001", Direction = CallDirection.Incoming, Message = "Incoming call from 1001" });

        Assert.Equal("Registered · Incoming call", vm.StatusBarText);
        Assert.Equal("Incoming call", vm.Dialer.DisplayStatusMessage);
        Assert.Equal("Unknown caller", vm.Dialer.DisplayRemoteParty);

        vm.Account.ShowIncomingCallPreviews = true;

        Assert.True(_preferences.Saved.ShowIncomingCallPreviews);
        Assert.Contains("1001", vm.StatusBarText);
    }
}
