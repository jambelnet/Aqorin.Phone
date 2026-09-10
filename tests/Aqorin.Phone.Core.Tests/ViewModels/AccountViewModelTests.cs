using Aqorin.Phone.App.ViewModels;
using Aqorin.Phone.Core.Abstractions;
using Aqorin.Phone.Core.Diagnostics;
using Aqorin.Phone.Core.Model;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aqorin.Phone.Core.Tests.ViewModels;

public class AccountViewModelTests
{
    private readonly FakeRegistrationService _registration = new();
    private readonly InMemorySettingsStore _store = new();
    private readonly FakeDiagnosticsSwitch _diagnostics = new();
    private readonly FakeAudioDeviceService _audio = new();
    private readonly FakeCallService _calls = new();

    private AccountViewModel Create() =>
        new(_registration, _store, _diagnostics, _audio, _calls, new ImmediateDispatcher(), NullLogger<AccountViewModel>.Instance);

    [Fact]
    public void Defaults_match_the_fritzbox_conventions()
    {
        var vm = Create();
        Assert.Equal("fritz.box", vm.Registrar);
        Assert.Equal("5060", vm.PortText);
        Assert.Equal(SipTransport.Udp, vm.Transport);
        Assert.Equal("300", vm.ExpiryText);
        Assert.Equal(RegistrationState.Disconnected, vm.State);
        Assert.Equal(2, vm.TransportOptions.Count);
        Assert.Equal(SipRouterProfile.GenericSip, vm.SelectedRouterProfile.Profile);
        Assert.Single(vm.InputDeviceOptions);
        Assert.Single(vm.OutputDeviceOptions);
    }

    [Fact]
    public void Register_requires_host_username_and_password()
    {
        var vm = Create();
        Assert.False(vm.RegisterCommand.CanExecute(null));
        vm.Username = "620";
        Assert.False(vm.RegisterCommand.CanExecute(null));
        vm.Password = "pw";
        Assert.True(vm.RegisterCommand.CanExecute(null));
        vm.Registrar = "";
        Assert.False(vm.RegisterCommand.CanExecute(null));
    }

    [Fact]
    public async Task Register_passes_a_validated_account_and_persists_settings_with_the_password_when_remembered()
    {
        var vm = Create();
        vm.Username = "620";
        vm.Password = "s3cret";
        vm.DisplayName = "Desk";
        vm.Transport = SipTransport.Tcp;
        Assert.True(vm.RememberPassword); // default: remember (encrypted at rest by the store)
        Assert.False(string.IsNullOrWhiteSpace(vm.PasswordStorageHint));

        await vm.RegisterCommand.ExecuteAsync(null);

        var account = Assert.Single(_registration.RegisterCalls);
        Assert.Equal("620", account.Settings.Username);
        Assert.Equal("s3cret", account.Password);
        Assert.Equal(SipTransport.Tcp, account.Settings.Transport);
        Assert.Equal(SipRouterProfile.GenericSip, account.Settings.RouterProfile);
        Assert.NotNull(_store.Saved);
        Assert.Equal("620", _store.Saved!.Username);
        Assert.Equal("s3cret", _store.SavedPassword);
        // The settings record itself never carries the password (it is handed to the store separately for encryption).
        Assert.DoesNotContain("s3cret", System.Text.Json.JsonSerializer.Serialize(_store.Saved));
        Assert.Equal(RegistrationState.Registering, vm.State);
        Assert.True(vm.IsBusy);
        Assert.False(vm.CanEditSettings);
    }

    [Fact]
    public async Task Password_is_not_persisted_when_remember_is_off_and_is_removed_when_switched_off()
    {
        var vm = Create();
        vm.Username = "620";
        vm.Password = "s3cret";
        vm.RememberPassword = false;

        await vm.RegisterCommand.ExecuteAsync(null);
        Assert.Null(_store.SavedPassword);
        Assert.Equal("620", _store.Saved!.Username);

        vm.RememberPassword = true;
        await vm.SaveSettingsAsync();
        Assert.Equal("s3cret", _store.SavedPassword);

        vm.RememberPassword = false; // forgets immediately
        Assert.Null(_store.SavedPassword);
        Assert.NotNull(_store.Saved);
    }

    [Fact]
    public async Task Invalid_port_shows_a_friendly_error_and_does_not_register()
    {
        var vm = Create();
        vm.Username = "620";
        vm.Password = "pw";
        vm.PortText = "abc";

        await vm.RegisterCommand.ExecuteAsync(null);

        Assert.Empty(_registration.RegisterCalls);
        Assert.True(vm.HasValidationError);
        Assert.Contains("Port", vm.ValidationError);
    }

    [Fact]
    public async Task Validation_errors_from_the_validator_are_shown()
    {
        var vm = Create();
        vm.Username = "6 20";
        vm.Password = "pw";

        await vm.RegisterCommand.ExecuteAsync(null);

        Assert.Empty(_registration.RegisterCalls);
        Assert.Contains("Username", vm.ValidationError);
    }

    [Fact]
    public void Status_updates_drive_commands_and_texts()
    {
        var vm = Create();
        vm.Username = "620";
        vm.Password = "pw";

        _registration.Set(new RegistrationStatus { State = RegistrationState.Registering, Message = "Registering…" });
        Assert.False(vm.RegisterCommand.CanExecute(null));
        Assert.True(vm.UnregisterCommand.CanExecute(null));
        Assert.True(vm.IsBusy);

        _registration.Set(new RegistrationStatus
        {
            State = RegistrationState.Registered,
            Message = "Registered as 620@fritz.box",
            Detail = "Registration expires in 300s",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
        });
        Assert.True(vm.IsRegistered);
        Assert.Equal("Registered", vm.StateText);
        Assert.Equal("Registered as 620@fritz.box", vm.StatusMessage);
        Assert.True(vm.HasStatusDetail);
        Assert.StartsWith("Expires", vm.ExpiryInfo);
        Assert.False(vm.RegisterCommand.CanExecute(null));
        Assert.True(vm.UnregisterCommand.CanExecute(null));
        Assert.False(vm.HasValidationError);

        _registration.Set(new RegistrationStatus { State = RegistrationState.Failed, Message = "Registration rejected: wrong username or password.", IsAuthenticationFailure = true });
        Assert.True(vm.RegisterCommand.CanExecute(null));
        Assert.False(vm.UnregisterCommand.CanExecute(null));
        Assert.Contains("wrong username or password", vm.ValidationError);
        Assert.True(vm.CanEditSettings);
    }

    [Fact]
    public async Task Unregister_calls_the_service()
    {
        var vm = Create();
        _registration.Set(new RegistrationStatus { State = RegistrationState.Registered, Message = "Registered" });

        await vm.UnregisterCommand.ExecuteAsync(null);

        Assert.Equal(1, _registration.UnregisterCalls);
        Assert.Equal(RegistrationState.Disconnected, vm.State);
    }

    [Fact]
    public async Task Register_twice_error_from_service_is_shown()
    {
        var vm = Create();
        vm.Username = "620";
        vm.Password = "pw";
        _registration.ThrowOnRegister = new InvalidRegistrationOperationException("register", RegistrationState.Registered);

        await vm.RegisterCommand.ExecuteAsync(null);

        Assert.Contains("Cannot register", vm.ValidationError);
    }

    [Fact]
    public async Task Saved_settings_are_loaded_including_a_remembered_password()
    {
        _audio.Inputs.Add(new AudioDeviceInfo("in-1:Mic", "Mic", true, 1, 0, 48000));
        _audio.Outputs.Add(new AudioDeviceInfo("out-1:Speaker", "Speaker", true, 0, 2, 48000));
        _store.Saved = new SipAccountSettings
        {
            Registrar = "192.168.178.1",
            RouterProfile = SipRouterProfile.FritzBox,
            Port = 5070,
            Username = "621",
            DisplayName = "Kitchen",
            Transport = SipTransport.Tcp,
            RegistrationExpirySeconds = 600,
            DiagnosticLogging = true,
            InputDeviceId = "in-1:Mic",
            OutputDeviceId = "out-1:Speaker"
        };
        _store.SavedPassword = "remembered";
        var vm = Create();

        await vm.LoadSettingsAsync();

        Assert.Equal("192.168.178.1", vm.Registrar);
        Assert.Equal("5070", vm.PortText);
        Assert.Equal("621", vm.Username);
        Assert.Equal("Kitchen", vm.DisplayName);
        Assert.Equal(SipTransport.Tcp, vm.Transport);
        Assert.Equal(SipRouterProfile.FritzBox, vm.SelectedRouterProfile.Profile);
        Assert.Equal("600", vm.ExpiryText);
        Assert.Equal("remembered", vm.Password);
        Assert.Equal("in-1:Mic", vm.SelectedInputDevice!.DeviceId);
        Assert.Equal("out-1:Speaker", vm.SelectedOutputDevice!.DeviceId);
        Assert.Equal("in-1:Mic", _calls.MediaOptions.InputDeviceId);
        Assert.Equal("out-1:Speaker", _calls.MediaOptions.OutputDeviceId);
        Assert.True(vm.RememberPassword);
        Assert.True(vm.RegisterCommand.CanExecute(null));
        Assert.True(vm.DiagnosticLogging);
        Assert.True(_diagnostics.Verbose);
    }

    [Fact]
    public async Task Saved_settings_without_a_password_leave_the_field_empty()
    {
        _store.Saved = new SipAccountSettings { Username = "621" };
        var vm = Create();

        await vm.LoadSettingsAsync();

        Assert.Equal("621", vm.Username);
        Assert.Equal(string.Empty, vm.Password);
        Assert.False(vm.RememberPassword);
        Assert.False(vm.RegisterCommand.CanExecute(null));
        Assert.Equal(0, _store.SaveCalls);
    }

    [Fact]
    public async Task Startup_auto_registers_when_saved_settings_include_a_remembered_password()
    {
        _store.Saved = new SipAccountSettings
        {
            Registrar = "192.168.178.1",
            Username = "620",
            Port = 5060,
            RegistrationExpirySeconds = 300
        };
        _store.SavedPassword = "remembered";
        var account = Create();
        var dispatcher = new ImmediateDispatcher();
        var dialer = new DialerViewModel(_calls, _registration, new InMemoryContactStore(), new InMemoryCallHistoryStore(), dispatcher, NullLogger<DialerViewModel>.Instance);
        var diagnostics = new DiagnosticsViewModel(new DiagnosticsLog(), _audio, dispatcher);
        var vm = new MainWindowViewModel(account, dialer, diagnostics, _registration, _calls, _audio, new InMemoryUiPreferencesStore(), new FakeRingtonePlayer(), dispatcher);

        await vm.InitializeAsync();

        var registeredAccount = Assert.Single(_registration.RegisterCalls);
        Assert.Equal("620", registeredAccount.Settings.Username);
        Assert.Equal("remembered", registeredAccount.Password);
        Assert.Equal("192.168.178.1", registeredAccount.Settings.Registrar);
        Assert.Equal(0, _store.SaveCalls);
    }

    [Fact]
    public async Task Startup_shows_settings_when_password_was_not_remembered()
    {
        _store.Saved = new SipAccountSettings
        {
            Registrar = "192.168.178.1",
            Username = "620",
            Port = 5060,
            RegistrationExpirySeconds = 300
        };
        var account = Create();
        var dispatcher = new ImmediateDispatcher();
        var dialer = new DialerViewModel(_calls, _registration, new InMemoryContactStore(), new InMemoryCallHistoryStore(), dispatcher, NullLogger<DialerViewModel>.Instance);
        var diagnostics = new DiagnosticsViewModel(new DiagnosticsLog(), _audio, dispatcher);
        var vm = new MainWindowViewModel(account, dialer, diagnostics, _registration, _calls, _audio, new InMemoryUiPreferencesStore(), new FakeRingtonePlayer(), dispatcher);

        await vm.InitializeAsync();

        Assert.Empty(_registration.RegisterCalls);
        Assert.Equal(4, vm.SelectedTabIndex);
        Assert.False(account.RememberPassword);
    }

    [Fact]
    public void Diagnostic_toggle_drives_the_switch()
    {
        var vm = Create();
        vm.DiagnosticLogging = true;
        Assert.True(_diagnostics.Verbose);
        vm.DiagnosticLogging = false;
        Assert.False(_diagnostics.Verbose);
    }

    [Fact]
    public async Task Register_persists_audio_preferences_and_applies_media_options()
    {
        _audio.Inputs.Add(new AudioDeviceInfo("2:Studio Mic", "Studio Mic", false, 1, 0, 48000));
        _audio.Outputs.Add(new AudioDeviceInfo("5:Headset", "Headset", false, 0, 2, 48000));
        var vm = Create();
        vm.Username = "620";
        vm.Password = "pw";
        vm.SelectedInputDevice = vm.InputDeviceOptions.Single(d => d.DeviceId == "2:Studio Mic");
        vm.SelectedOutputDevice = vm.OutputDeviceOptions.Single(d => d.DeviceId == "5:Headset");

        await vm.RegisterCommand.ExecuteAsync(null);

        Assert.Equal("2:Studio Mic", _store.Saved!.InputDeviceId);
        Assert.Equal("5:Headset", _store.Saved.OutputDeviceId);
        Assert.Equal("2:Studio Mic", _calls.MediaOptions.InputDeviceId);
        Assert.Equal("5:Headset", _calls.MediaOptions.OutputDeviceId);
    }
}
