using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Aqorin.Phone.Core.Abstractions;
using Aqorin.Phone.Core.Model;
using Aqorin.Phone.Core.StateMachines;
using Microsoft.Extensions.Logging;
using Aqorin.Phone.App.Services;

namespace Aqorin.Phone.App.ViewModels;

/// <summary>Account setup view: registrar, credentials, register/unregister and registration status.</summary>
public sealed partial class AccountViewModel : ViewModelBase
{
    private readonly ISipRegistrationService _registration;
    private readonly ISettingsStore _settingsStore;
    private readonly IDiagnosticsSwitch _diagnostics;
    private readonly IAudioDeviceService _audio;
    private readonly ICallService _calls;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<AccountViewModel> _logger;
    private bool _isLoadingSettings;

    public AccountViewModel(
        ISipRegistrationService registration,
        ISettingsStore settingsStore,
        IDiagnosticsSwitch diagnostics,
        IAudioDeviceService audio,
        ICallService calls,
        IUiDispatcher dispatcher,
        ILogger<AccountViewModel> logger)
    {
        _registration = registration;
        _settingsStore = settingsStore;
        _diagnostics = diagnostics;
        _audio = audio;
        _calls = calls;
        _dispatcher = dispatcher;
        _logger = logger;

        var defaults = new SipAccountSettings();
        Registrar = defaults.Registrar;
        SelectedRouterProfile = RouterProfiles.First(p => p.Profile == defaults.RouterProfile);
        PortText = defaults.Port.ToString();
        Transport = defaults.Transport;
        Username = string.Empty;
        Password = string.Empty;
        DisplayName = string.Empty;
        ExpiryText = defaults.RegistrationExpirySeconds.ToString();
        LocalPortText = "0";
        DiagnosticLogging = _diagnostics.Verbose;
        RememberPassword = true;
        PasswordStorageHint = _settingsStore.PasswordStorageDescription;

        RefreshAudioDevices();
        ApplyAudioPreferences(defaults);
        ApplyStatus(_registration.Status);
        _registration.StatusChanged += (_, status) => _dispatcher.Post(() => ApplyStatus(status));
    }

    public ObservableCollection<SipTransport> TransportOptions { get; } = new(Enum.GetValues<SipTransport>());

    public ObservableCollection<RouterProfileChoice> RouterProfiles { get; } = new(
    [
        new(SipRouterProfile.GenericSip, "Generic SIP router", "Use the router/PBX SIP registrar, port 5060, UDP or TCP."),
        new(SipRouterProfile.FritzBox, "FRITZ!Box", "Typical registrar: fritz.box or the router LAN IP."),
        new(SipRouterProfile.Speedport, "Speedport", "Use the SIP registrar shown in the router telephony settings."),
        new(SipRouterProfile.VodafoneStation, "Vodafone Station", "Use the router/PBX SIP registrar and try TCP if UDP is filtered."),
        new(SipRouterProfile.Livebox, "Livebox", "Use the router/PBX SIP registrar; G.711 audio is preferred."),
        new(SipRouterProfile.Freebox, "Freebox", "Use the router/PBX SIP registrar; G.711 audio is preferred."),
        new(SipRouterProfile.Custom, "Custom PBX", "Any IPv4 SIP registrar reachable on UDP/TCP 5060.")
    ]);

    public ObservableCollection<AudioDeviceChoice> InputDeviceOptions { get; } = [];

    public ObservableCollection<AudioDeviceChoice> OutputDeviceOptions { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RegisterCommand))]
    public partial string Registrar { get; set; }

    [ObservableProperty]
    public partial RouterProfileChoice SelectedRouterProfile { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RegisterCommand))]
    public partial string PortText { get; set; }

    [ObservableProperty]
    public partial SipTransport Transport { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RegisterCommand))]
    public partial string Username { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RegisterCommand))]
    public partial string Password { get; set; }

    [ObservableProperty]
    public partial string DisplayName { get; set; }

    [ObservableProperty]
    public partial string ExpiryText { get; set; }

    [ObservableProperty]
    public partial string LocalPortText { get; set; }

    [ObservableProperty]
    public partial bool DiagnosticLogging { get; set; }

    [ObservableProperty]
    public partial AudioDeviceChoice? SelectedInputDevice { get; set; }

    [ObservableProperty]
    public partial AudioDeviceChoice? SelectedOutputDevice { get; set; }

    [ObservableProperty]
    public partial bool ShowIncomingCallPreviews { get; set; } = true;

    [ObservableProperty]
    public partial bool PlayIncomingCallSound { get; set; } = true;

    [ObservableProperty]
    public partial string AudioBackendText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RouterCompatibilityHint { get; set; } = string.Empty;

    /// <summary>When true the password is saved encrypted (see <see cref="PasswordStorageHint"/>); otherwise it stays in memory only.</summary>
    [ObservableProperty]
    public partial bool RememberPassword { get; set; }

    [ObservableProperty]
    public partial string PasswordStorageHint { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ValidationError { get; set; } = string.Empty;

    // ---- status -----------------------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RegisterCommand))]
    [NotifyCanExecuteChangedFor(nameof(UnregisterCommand))]
    [NotifyPropertyChangedFor(nameof(IsRegistered))]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyPropertyChangedFor(nameof(StateText))]
    [NotifyPropertyChangedFor(nameof(CanEditSettings))]
    public partial RegistrationState State { get; set; }

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = "Not registered";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusDetail))]
    public partial string StatusDetail { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ExpiryInfo { get; set; } = string.Empty;

    public bool IsRegistered => State == RegistrationState.Registered;

    public bool IsBusy => State is RegistrationState.Registering or RegistrationState.Unregistering;

    public bool CanEditSettings => State is RegistrationState.Disconnected or RegistrationState.Failed;

    public string StateText => State switch
    {
        RegistrationState.Disconnected => "Disconnected",
        RegistrationState.Registering => "Registering…",
        RegistrationState.Registered => "Registered",
        RegistrationState.Unregistering => "Unregistering…",
        RegistrationState.Failed => "Failed",
        _ => State.ToString(),
    };

    public bool HasStatusDetail => !string.IsNullOrWhiteSpace(StatusDetail);

    public bool HasValidationError => !string.IsNullOrWhiteSpace(ValidationError);

    partial void OnValidationErrorChanged(string value) => OnPropertyChanged(nameof(HasValidationError));

    partial void OnDiagnosticLoggingChanged(bool value) => _diagnostics.Verbose = value;

    partial void OnSelectedInputDeviceChanged(AudioDeviceChoice? value) => ApplyAudioPreferencesFromSelection();

    partial void OnSelectedOutputDeviceChanged(AudioDeviceChoice? value) => ApplyAudioPreferencesFromSelection();

    partial void OnSelectedRouterProfileChanged(RouterProfileChoice value) => RouterCompatibilityHint = value.Hint;

    // ---- commands ---------------------------------------------------------------------------------------------------

    public bool CanRegister => RegistrationStateMachine.CanRegister(State)
        && !string.IsNullOrWhiteSpace(Registrar)
        && !string.IsNullOrWhiteSpace(Username)
        && !string.IsNullOrEmpty(Password);

    public bool CanUnregister => RegistrationStateMachine.CanUnregister(State) && State != RegistrationState.Failed;

    [RelayCommand(CanExecute = nameof(CanRegister))]
    private Task RegisterAsync(CancellationToken cancellationToken) =>
        RegisterCurrentSettingsAsync(cancellationToken, persistSettings: true);

    public Task<bool> RegisterLoadedSettingsAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrEmpty(Password))
        {
            return Task.FromResult(false);
        }

        if (_registration.Status.State is RegistrationState.Registering or RegistrationState.Registered)
        {
            return Task.FromResult(true);
        }

        return RegisterCurrentSettingsAsync(cancellationToken, persistSettings: false);
    }

    private async Task<bool> RegisterCurrentSettingsAsync(CancellationToken cancellationToken, bool persistSettings)
    {
        ValidationError = string.Empty;
        if (!TryBuildSettings(out var settings, out var error))
        {
            ValidationError = error;
            return false;
        }

        var validation = SipAccountValidator.Validate(settings, Password);
        if (!validation.IsValid)
        {
            ValidationError = string.Join("\n", validation.Errors);
            return false;
        }

        if (persistSettings)
        {
            await _settingsStore.SaveAsync(settings, RememberPassword ? Password : null, CancellationToken.None);
        }

        ApplyAudioPreferences(settings);

        try
        {
            await _registration.RegisterAsync(new SipAccount(settings, Password), cancellationToken);
            return true;
        }
        catch (InvalidRegistrationOperationException ex)
        {
            ValidationError = ex.Message;
        }
        catch (ArgumentException ex)
        {
            ValidationError = ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Registration failed unexpectedly.");
            ValidationError = "Registration failed: " + ex.Message;
        }

        return false;
    }

    [RelayCommand(CanExecute = nameof(CanUnregister))]
    private async Task UnregisterAsync(CancellationToken cancellationToken)
    {
        ValidationError = string.Empty;
        try
        {
            await _registration.UnregisterAsync(cancellationToken);
        }
        catch (InvalidRegistrationOperationException ex)
        {
            ValidationError = ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unregister failed unexpectedly.");
            ValidationError = "Unregister failed: " + ex.Message;
        }
    }

    [RelayCommand]
    private void RefreshAudioDevices()
    {
        var selectedInputId = SelectedInputDevice?.DeviceId;
        var selectedOutputId = SelectedOutputDevice?.DeviceId;

        InputDeviceOptions.Clear();
        InputDeviceOptions.Add(AudioDeviceChoice.SystemDefaultInput);
        OutputDeviceOptions.Clear();
        OutputDeviceOptions.Add(AudioDeviceChoice.SystemDefaultOutput);

        foreach (var device in _audio.GetInputDevices())
        {
            InputDeviceOptions.Add(AudioDeviceChoice.FromDevice(device));
        }

        foreach (var device in _audio.GetOutputDevices())
        {
            OutputDeviceOptions.Add(AudioDeviceChoice.FromDevice(device));
        }

        SelectedInputDevice = FindAudioChoice(InputDeviceOptions, selectedInputId);
        SelectedOutputDevice = FindAudioChoice(OutputDeviceOptions, selectedOutputId);
        AudioBackendText = _audio.Backend.IsAvailable
            ? $"{_audio.Backend.Name} {_audio.Backend.Version}".Trim()
            : "Audio unavailable: " + _audio.Backend.UnavailableReason;
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    public async Task<bool> LoadSettingsAsync(CancellationToken cancellationToken = default)
    {
        var saved = await _settingsStore.LoadAsync(cancellationToken);
        if (saved is null)
        {
            return false;
        }

        await _dispatcher.InvokeAsync(() =>
        {
            _isLoadingSettings = true;
            try
            {
                ApplySettings(saved.Settings);
                Password = saved.Password ?? string.Empty;
                RememberPassword = saved.Password is not null;
                ApplyAudioPreferences(saved.Settings);
            }
            finally
            {
                _isLoadingSettings = false;
            }
        });

        return true;
    }

    /// <summary>Persists the current (validated) settings and, if chosen, the password. Called on register and on request.</summary>
    public Task SaveSettingsAsync(CancellationToken cancellationToken = default)
    {
        if (!TryBuildSettings(out var settings, out _))
        {
            return Task.CompletedTask;
        }

        ApplyAudioPreferences(settings);
        return _settingsStore.SaveAsync(settings, RememberPassword ? Password : null, cancellationToken);
    }

    public Task SaveConfiguredSettingsAsync(CancellationToken cancellationToken = default) =>
        string.IsNullOrWhiteSpace(Username) ? Task.CompletedTask : SaveSettingsAsync(cancellationToken);

    partial void OnRememberPasswordChanged(bool value)
    {
        if (_isLoadingSettings)
        {
            return;
        }

        if (!value)
        {
            // Forget immediately: remove the stored password but keep the other settings.
            if (TryBuildSettings(out var settings, out _))
            {
                _ = _settingsStore.SaveAsync(settings, null, CancellationToken.None);
            }
        }
    }

    public void ApplySettings(SipAccountSettings settings)
    {
        Registrar = settings.Registrar;
        SelectedRouterProfile = RouterProfiles.FirstOrDefault(p => p.Profile == settings.RouterProfile)
            ?? RouterProfiles.First(p => p.Profile == SipRouterProfile.GenericSip);
        PortText = settings.Port.ToString();
        Transport = settings.Transport;
        Username = settings.Username;
        DisplayName = settings.DisplayName;
        ExpiryText = settings.RegistrationExpirySeconds.ToString();
        LocalPortText = settings.LocalPort.ToString();
        DiagnosticLogging = settings.DiagnosticLogging;
        SelectedInputDevice = FindAudioChoice(InputDeviceOptions, settings.InputDeviceId);
        SelectedOutputDevice = FindAudioChoice(OutputDeviceOptions, settings.OutputDeviceId);
    }

    public void ApplyUiPreferences(UiPreferences preferences)
    {
        ShowIncomingCallPreviews = preferences.ShowIncomingCallPreviews;
        PlayIncomingCallSound = preferences.PlayIncomingCallSound;
    }

    public bool TryBuildSettings(out SipAccountSettings settings, out string error)
    {
        var errors = new List<string>();
        if (!int.TryParse(PortText?.Trim(), out var port))
        {
            errors.Add("Port must be a number (SIP default: 5060).");
        }

        if (!int.TryParse(ExpiryText?.Trim(), out var expiry))
        {
            errors.Add("Registration expiry must be a number of seconds.");
        }

        if (!int.TryParse(string.IsNullOrWhiteSpace(LocalPortText) ? "0" : LocalPortText.Trim(), out var localPort))
        {
            errors.Add("Local port must be a number (0 = automatic).");
        }

        settings = new SipAccountSettings
        {
            Registrar = Registrar?.Trim() ?? string.Empty,
            RouterProfile = SelectedRouterProfile.Profile,
            Port = port,
            Transport = Transport,
            Username = Username?.Trim() ?? string.Empty,
            DisplayName = DisplayName?.Trim() ?? string.Empty,
            RegistrationExpirySeconds = expiry,
            LocalPort = localPort,
            DiagnosticLogging = DiagnosticLogging,
            InputDeviceId = SelectedInputDevice?.DeviceId,
            OutputDeviceId = SelectedOutputDevice?.DeviceId,
        };
        error = string.Join("\n", errors);
        return errors.Count == 0;
    }

    private void ApplyAudioPreferencesFromSelection()
    {
        if (TryBuildSettings(out var settings, out _))
        {
            ApplyAudioPreferences(settings);
        }
    }

    private void ApplyAudioPreferences(SipAccountSettings settings)
    {
        _calls.MediaOptions = _calls.MediaOptions with
        {
            InputDeviceId = string.IsNullOrWhiteSpace(settings.InputDeviceId) ? null : settings.InputDeviceId,
            OutputDeviceId = string.IsNullOrWhiteSpace(settings.OutputDeviceId) ? null : settings.OutputDeviceId,
        };
    }

    private static AudioDeviceChoice FindAudioChoice(ObservableCollection<AudioDeviceChoice> choices, string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return choices[0];
        }

        return choices.FirstOrDefault(c => c.DeviceId == deviceId) ?? choices[0];
    }

    private void ApplyStatus(RegistrationStatus status)
    {
        State = status.State;
        StatusMessage = status.Message;
        StatusDetail = status.Detail ?? string.Empty;
        ExpiryInfo = status.ExpiresAt is { } expires
            ? $"Expires {expires.ToLocalTime():HH:mm:ss}"
            : status.NextRetryAt is { } retry ? $"Next attempt {retry.ToLocalTime():HH:mm:ss}" : string.Empty;
        if (status.State == RegistrationState.Failed && !string.IsNullOrWhiteSpace(status.Message))
        {
            ValidationError = status.Message;
        }
        else if (status.State == RegistrationState.Registered)
        {
            ValidationError = string.Empty;
        }
    }
}

public sealed record RouterProfileChoice(SipRouterProfile Profile, string Label, string Hint)
{
    public override string ToString() => Label;
}

public sealed record AudioDeviceChoice(string? DeviceId, string Label)
{
    public static AudioDeviceChoice SystemDefaultInput { get; } = new(null, "System default microphone");
    public static AudioDeviceChoice SystemDefaultOutput { get; } = new(null, "System default speaker");

    public static AudioDeviceChoice FromDevice(AudioDeviceInfo device)
    {
        var label = device.IsDefault ? $"{device.Name} (default)" : device.Name;
        return new AudioDeviceChoice(device.Id, label);
    }

    public override string ToString() => Label;
}
