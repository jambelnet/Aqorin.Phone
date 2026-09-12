using System.ComponentModel;
using Avalonia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Aqorin.Phone.App.Services;
using Aqorin.Phone.Core.Abstractions;
using Aqorin.Phone.Core.Model;

namespace Aqorin.Phone.App.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly ISipRegistrationService _registration;
    private readonly ICallService _calls;
    private readonly IUiPreferencesStore _preferences;
    private readonly IRingtonePlayer _ringtone;
    private readonly IUiDispatcher _dispatcher;
    private bool _loadingPreferences;

    public MainWindowViewModel(
        AccountViewModel account,
        DialerViewModel dialer,
        DiagnosticsViewModel diagnostics,
        ISipRegistrationService registration,
        ICallService calls,
        IAudioDeviceService audio,
        IUiPreferencesStore preferences,
        IRingtonePlayer ringtone,
        IUiDispatcher dispatcher)
    {
        Account = account;
        Dialer = dialer;
        Diagnostics = diagnostics;
        _registration = registration;
        _calls = calls;
        _preferences = preferences;
        _ringtone = ringtone;
        _dispatcher = dispatcher;

        AudioWarning = audio.Backend.IsAvailable ? string.Empty : "No audio backend: " + audio.Backend.UnavailableReason;
        UpdateStatusBar(_registration.Status, _calls.CurrentCall);
        Account.PropertyChanged += OnAccountPropertyChanged;
        _registration.StatusChanged += (_, s) => _dispatcher.Post(() => UpdateStatusBar(s, _calls.CurrentCall));
        _calls.CallChanged += (_, c) => _dispatcher.Post(() =>
        {
            HandleRingtone(c);
            UpdateStatusBar(_registration.Status, c);
            if (c.IsInProgress)
            {
                SelectedTabIndex = 1;
            }
        });
    }

    public AccountViewModel Account { get; }

    public DialerViewModel Dialer { get; }

    public DiagnosticsViewModel Diagnostics { get; }

    [ObservableProperty]
    public partial int SelectedTabIndex { get; set; }

    [ObservableProperty]
    public partial string StatusBarText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool StatusIsGood { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAudioWarning))]
    public partial string AudioWarning { get; set; }

    public bool HasAudioWarning => !string.IsNullOrEmpty(AudioWarning);

    public string Title => "Aqorin Phone";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThemeButtonText))]
    [NotifyPropertyChangedFor(nameof(ThemeToolTip))]
    public partial bool IsDarkTheme { get; set; }

    public string ThemeButtonText => IsDarkTheme ? "Light theme" : "Dark theme";

    public string ThemeToolTip => IsDarkTheme ? "Switch to light theme" : "Switch to dark theme";

    public async Task InitializeAsync()
    {
        await LoadPreferencesAsync();
        await Dialer.LoadContactsAsync();
        await Dialer.LoadRecentCallsAsync();
        var loadedSettings = await Account.LoadSettingsAsync();
        if (!loadedSettings || string.IsNullOrWhiteSpace(Account.Username))
        {
            SelectedTabIndex = 4; // first run: show account setup
            return;
        }

        if (string.IsNullOrEmpty(Account.Password))
        {
            SelectedTabIndex = 4; // password was not remembered; user must re-enter it.
            return;
        }

        await Account.RegisterLoadedSettingsAsync();
    }

    [RelayCommand]
    private async Task ToggleThemeAsync()
    {
        IsDarkTheme = !IsDarkTheme;
        ApplyTheme();
        await SavePreferencesAsync();
    }

    public async Task LoadPreferencesAsync(CancellationToken cancellationToken = default)
    {
        var preferences = await _preferences.LoadAsync(cancellationToken);
        _loadingPreferences = true;
        try
        {
            IsDarkTheme = preferences.IsDarkTheme;
            Account.ApplyUiPreferences(preferences);
            Dialer.ShowIncomingCallPreviews = preferences.ShowIncomingCallPreviews;
        }
        finally
        {
            _loadingPreferences = false;
        }

        ApplyTheme();
        HandleRingtone(_calls.CurrentCall);
    }

    public Task SavePreferencesAsync(CancellationToken cancellationToken = default) =>
        _preferences.SaveAsync(
            new UiPreferences(IsDarkTheme, Account.ShowIncomingCallPreviews, Account.PlayIncomingCallSound),
            cancellationToken);

    private void ApplyTheme()
    {
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = IsDarkTheme ? ThemeVariant.Dark : ThemeVariant.Light;
        }
    }

    private void UpdateStatusBar(RegistrationStatus registration, CallInfo call)
    {
        StatusIsGood = registration.IsRegistered;
        StatusBarText = call.IsInProgress
            ? $"{registration.Message} · {StatusTextFor(call)}"
            : registration.Message;
    }

    private string StatusTextFor(CallInfo call) =>
        call.State == CallState.Incoming && !Account.ShowIncomingCallPreviews
            ? "Incoming call"
            : Dialer.DisplayStatusMessage;

    private void HandleRingtone(CallInfo call)
    {
        if (call.State == CallState.Incoming && Account.PlayIncomingCallSound)
        {
            _ringtone.Start(Account.SelectedOutputDevice?.DeviceId);
            return;
        }

        _ringtone.Stop();
    }

    private void OnAccountPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(AccountViewModel.ShowIncomingCallPreviews) or nameof(AccountViewModel.PlayIncomingCallSound) or nameof(AccountViewModel.SelectedOutputDevice)))
        {
            return;
        }

        Dialer.ShowIncomingCallPreviews = Account.ShowIncomingCallPreviews;
        UpdateStatusBar(_registration.Status, _calls.CurrentCall);
        HandleRingtone(_calls.CurrentCall);
        if (!_loadingPreferences)
        {
            _ = SavePreferencesAsync();
        }
    }
}
