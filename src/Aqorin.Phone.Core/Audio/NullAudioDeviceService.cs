using Aqorin.Phone.Core.Abstractions;

namespace Aqorin.Phone.Core.Audio;

/// <summary>
/// Backend used when no native audio is available (unit tests, CI, headless machines).
/// Streams open successfully but capture produces no frames and playback discards audio,
/// so signalling can be exercised without devices. The UI shows <see cref="AudioBackendInfo.UnavailableReason"/>.
/// </summary>
public sealed class NullAudioDeviceService(string? reason = null) : IAudioDeviceService
{
    public AudioBackendInfo Backend { get; } = new("None", null, IsAvailable: false, reason ?? "No audio backend is available on this machine.");

    public IReadOnlyList<AudioDeviceInfo> GetInputDevices() => Array.Empty<AudioDeviceInfo>();

    public IReadOnlyList<AudioDeviceInfo> GetOutputDevices() => Array.Empty<AudioDeviceInfo>();

    public IAudioCaptureStream OpenCapture(AudioStreamRequest request) => new NullCapture(request);

    public IAudioPlaybackStream OpenPlayback(AudioStreamRequest request) => new NullPlayback(request);

    public void Dispose()
    {
    }

    private sealed class NullCapture(AudioStreamRequest request) : IAudioCaptureStream
    {
        public AudioStreamRequest Request { get; } = request;
        public event Action<short[]>? FrameCaptured { add { } remove { } }
        public event Action<string>? Error { add { } remove { } }
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
    }

    private sealed class NullPlayback(AudioStreamRequest request) : IAudioPlaybackStream
    {
        public AudioStreamRequest Request { get; } = request;
        public event Action<string>? Error { add { } remove { } }
        public TimeSpan Buffered => TimeSpan.Zero;
        public void Enqueue(ReadOnlySpan<short> pcm) { }
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
    }
}
