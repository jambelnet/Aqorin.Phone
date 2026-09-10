using Aqorin.Phone.Core.Abstractions;
using Aqorin.Phone.App.Services;
using Aqorin.Phone.Core.Model;

namespace Aqorin.Phone.Core.Tests.ViewModels;

internal sealed class FakeRegistrationService : ISipRegistrationService
{
    public RegistrationStatus Status { get; private set; } = RegistrationStatus.Disconnected;
    public SipAccountSettings? CurrentAccount { get; private set; }
    public event EventHandler<RegistrationStatus>? StatusChanged;
    public List<SipAccount> RegisterCalls { get; } = [];
    public int UnregisterCalls { get; private set; }
    public Exception? ThrowOnRegister { get; set; }

    public Task RegisterAsync(SipAccount account, CancellationToken cancellationToken = default)
    {
        if (ThrowOnRegister is not null)
        {
            throw ThrowOnRegister;
        }

        RegisterCalls.Add(account);
        CurrentAccount = account.Settings;
        Set(new RegistrationStatus { State = RegistrationState.Registering, Message = "Registering…" });
        return Task.CompletedTask;
    }

    public Task UnregisterAsync(CancellationToken cancellationToken = default)
    {
        UnregisterCalls++;
        Set(RegistrationStatus.Disconnected);
        return Task.CompletedTask;
    }

    public void Set(RegistrationStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, status);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeCallService : ICallService
{
    public CallInfo CurrentCall { get; private set; } = CallInfo.Idle;
    public AudioMediaSessionOptions MediaOptions { get; set; } = new();
    public bool IsMuted { get; private set; }
    public bool IsOnHold { get; private set; }
    public bool IsSpeakerEnabled { get; private set; }
    public event EventHandler<CallInfo>? CallChanged;
    public List<string> PlacedCalls { get; } = [];
    public int Answers { get; private set; }
    public int Rejects { get; private set; }
    public int Hangups { get; private set; }
    public Exception? ThrowOnPlaceCall { get; set; }

    public Task<CallInfo> PlaceCallAsync(string destination, CancellationToken cancellationToken = default)
    {
        if (ThrowOnPlaceCall is not null)
        {
            throw ThrowOnPlaceCall;
        }

        PlacedCalls.Add(destination);
        Set(new CallInfo { State = CallState.Dialing, RemoteParty = destination, Direction = CallDirection.Outgoing, Message = "Calling…" });
        return Task.FromResult(CurrentCall);
    }

    public Task AnswerAsync(CancellationToken cancellationToken = default)
    {
        Answers++;
        Set(CurrentCall with { State = CallState.Active, ConnectedAt = DateTimeOffset.UtcNow, Message = "Connected" });
        return Task.CompletedTask;
    }

    public Task RejectAsync(CancellationToken cancellationToken = default)
    {
        Rejects++;
        Set(CallInfo.Idle);
        return Task.CompletedTask;
    }

    public Task HangupAsync(CancellationToken cancellationToken = default)
    {
        Hangups++;
        Set(CallInfo.Idle);
        return Task.CompletedTask;
    }

    public Task SetMutedAsync(bool muted, CancellationToken cancellationToken = default)
    {
        IsMuted = muted;
        CallChanged?.Invoke(this, CurrentCall);
        return Task.CompletedTask;
    }

    public Task SetHoldAsync(bool held, CancellationToken cancellationToken = default)
    {
        IsOnHold = held;
        CallChanged?.Invoke(this, CurrentCall);
        return Task.CompletedTask;
    }

    public Task SetSpeakerAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        IsSpeakerEnabled = enabled;
        CallChanged?.Invoke(this, CurrentCall);
        return Task.CompletedTask;
    }

    public void Set(CallInfo info)
    {
        CurrentCall = info;
        CallChanged?.Invoke(this, info);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class InMemorySettingsStore : ISettingsStore
{
    public SipAccountSettings? Saved { get; set; }
    public string? SavedPassword { get; set; }
    public int SaveCalls { get; private set; }

    public string PasswordStorageDescription => "Stored encrypted (test store).";

    public Task<StoredSettings?> LoadAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Saved is null ? null : new StoredSettings(Saved, SavedPassword));

    public Task SaveAsync(SipAccountSettings settings, string? password, CancellationToken cancellationToken = default)
    {
        SaveCalls++;
        Saved = settings;
        SavedPassword = password;
        return Task.CompletedTask;
    }
}

internal sealed class InMemoryContactStore : IContactStore
{
    public List<ContactEntry> Saved { get; } = [];

    public Task<IReadOnlyList<ContactEntry>> LoadAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ContactEntry>>(Saved.ToList());

    public Task SaveAsync(IEnumerable<ContactEntry> contacts, CancellationToken cancellationToken = default)
    {
        Saved.Clear();
        Saved.AddRange(contacts);
        return Task.CompletedTask;
    }
}

internal sealed class InMemoryCallHistoryStore : ICallHistoryStore
{
    public List<CallHistoryEntry> Saved { get; } = [];
    public int SaveCalls { get; private set; }
    public int ClearCalls { get; private set; }

    public Task<IReadOnlyList<CallHistoryEntry>> LoadAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<CallHistoryEntry>>(Saved.ToList());

    public Task SaveAsync(IEnumerable<CallHistoryEntry> calls, CancellationToken cancellationToken = default)
    {
        SaveCalls++;
        Saved.Clear();
        Saved.AddRange(calls);
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        ClearCalls++;
        Saved.Clear();
        return Task.CompletedTask;
    }
}

internal sealed class InMemoryUiPreferencesStore : IUiPreferencesStore
{
    public UiPreferences Saved { get; set; } = new();
    public int SaveCalls { get; private set; }

    public Task<UiPreferences> LoadAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Saved);

    public Task SaveAsync(UiPreferences preferences, CancellationToken cancellationToken = default)
    {
        SaveCalls++;
        Saved = preferences;
        return Task.CompletedTask;
    }
}

internal sealed class FakeRingtonePlayer : IRingtonePlayer
{
    public int StartCalls { get; private set; }
    public int StopCalls { get; private set; }
    public string? OutputDeviceId { get; private set; }
    public bool IsPlaying { get; private set; }

    public void Start(string? outputDeviceId)
    {
        StartCalls++;
        OutputDeviceId = outputDeviceId;
        IsPlaying = true;
    }

    public void Stop()
    {
        StopCalls++;
        IsPlaying = false;
    }

    public void Dispose() => Stop();
}

internal sealed class FakeDiagnosticsSwitch : IDiagnosticsSwitch
{
    public bool Verbose { get; set; }
}

internal sealed class FakeAudioDeviceService : IAudioDeviceService
{
    public AudioBackendInfo Backend { get; set; } = new("FakeAudio", "1.0", true, null);
    public List<AudioDeviceInfo> Inputs { get; } = [];
    public List<AudioDeviceInfo> Outputs { get; } = [];

    public IReadOnlyList<AudioDeviceInfo> GetInputDevices() => Inputs;

    public IReadOnlyList<AudioDeviceInfo> GetOutputDevices() => Outputs;

    public IAudioCaptureStream OpenCapture(AudioStreamRequest request) => throw new NotSupportedException();

    public IAudioPlaybackStream OpenPlayback(AudioStreamRequest request) => throw new NotSupportedException();

    public void Dispose()
    {
    }
}
