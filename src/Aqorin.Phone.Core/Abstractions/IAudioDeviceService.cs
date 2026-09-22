namespace Aqorin.Phone.Core.Abstractions;

public sealed record AudioDeviceInfo(string Id, string Name, bool IsDefault, int MaxInputChannels, int MaxOutputChannels, double DefaultSampleRate);

/// <summary>Describes the native audio backend so the UI can explain missing audio honestly.</summary>
public sealed record AudioBackendInfo(string Name, string? Version, bool IsAvailable, string? UnavailableReason);

/// <summary>Request to open a mono, 16-bit PCM stream delivering/consuming fixed-size frames.</summary>
public sealed record AudioStreamRequest(int SampleRate, int FrameDurationMilliseconds = 20, string? DeviceId = null)
{
    public int FrameSizeSamples => SampleRate * FrameDurationMilliseconds / 1000;
}

/// <summary>
/// Application-owned microphone/speaker abstraction. Implemented once per backend (PortAudio) plus a
/// <see cref="Aqorin.Phone.Core.Audio.NullAudioDeviceService"/> for tests and machines without audio.
/// PCM is always signed 16-bit mono at the requested sample rate; implementations resample if the device cannot.
/// </summary>
public interface IAudioDeviceService : IDisposable
{
    AudioBackendInfo Backend { get; }

    IReadOnlyList<AudioDeviceInfo> GetInputDevices();

    IReadOnlyList<AudioDeviceInfo> GetOutputDevices();

    /// <summary>Opens (but does not start) a capture stream. Throws <see cref="AudioDeviceException"/> on failure.</summary>
    IAudioCaptureStream OpenCapture(AudioStreamRequest request);

    /// <summary>Opens (but does not start) a playback stream. Throws <see cref="AudioDeviceException"/> on failure.</summary>
    IAudioPlaybackStream OpenPlayback(AudioStreamRequest request);
}

/// <summary>
/// Optional native call-route control implemented by mobile audio backends. Desktop backends can continue
/// switching concrete output devices through <see cref="AudioStreamRequest.DeviceId"/>.
/// </summary>
public interface ICallAudioRouteController
{
    Task SetSpeakerEnabledAsync(bool enabled, CancellationToken cancellationToken = default);
}

/// <summary>Delivers captured frames of exactly <see cref="AudioStreamRequest.FrameSizeSamples"/> samples on an audio thread.</summary>
public interface IAudioCaptureStream : IDisposable
{
    AudioStreamRequest Request { get; }

    /// <summary>Raised on the native audio thread. Handlers must be fast and must not block.</summary>
    event Action<short[]>? FrameCaptured;

    /// <summary>Raised when the device fails after opening.</summary>
    event Action<string>? Error;

    void Start();

    void Stop();
}

/// <summary>Plays PCM frames; buffers internally and plays silence when starved.</summary>
public interface IAudioPlaybackStream : IDisposable
{
    AudioStreamRequest Request { get; }

    event Action<string>? Error;

    /// <summary>Queues PCM at <see cref="AudioStreamRequest.SampleRate"/>. Thread-safe.</summary>
    void Enqueue(ReadOnlySpan<short> pcm);

    /// <summary>Approximate audio queued but not yet played.</summary>
    TimeSpan Buffered { get; }

    void Start();

    void Stop();
}

public sealed class AudioDeviceException(string message, Exception? inner = null) : Exception(message, inner);
