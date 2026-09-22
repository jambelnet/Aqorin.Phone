using System.Collections.Concurrent;
using System.Runtime.Versioning;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Media;
using Android.OS;
using Aqorin.Phone.Core.Abstractions;
using CoreAudioDeviceInfo = Aqorin.Phone.Core.Abstractions.AudioDeviceInfo;

namespace Aqorin.Phone.Mobile.Platforms.Android;

/// <summary>Android microphone and call-audio backend using AudioRecord and AudioTrack.</summary>
public sealed class AndroidAudioDeviceService : IAudioDeviceService, ICallAudioRouteController
{
    private readonly AudioManager? _audioManager;
    private readonly Mode _originalMode;
    private readonly object _communicationGate = new();
    private readonly AudioFocusListener _focusListener = new();
    private AudioFocusRequestClass? _focusRequest;
    private int _communicationUsers;
    private bool _disposed;

    public AndroidAudioDeviceService()
    {
        _audioManager = global::Android.App.Application.Context.GetSystemService(Context.AudioService) as AudioManager;
        _originalMode = _audioManager?.Mode ?? Mode.Normal;
    }

    public AudioBackendInfo Backend { get; } = new(
        "Android AudioRecord/AudioTrack",
        Build.VERSION.Release,
        true,
        null);

    public IReadOnlyList<CoreAudioDeviceInfo> GetInputDevices() =>
    [
        new CoreAudioDeviceInfo("default", "Android microphone", true, 1, 0, 48000),
    ];

    public IReadOnlyList<CoreAudioDeviceInfo> GetOutputDevices() =>
    [
        new CoreAudioDeviceInfo("default", "Android call audio", true, 0, 1, 48000),
    ];

    public IAudioCaptureStream OpenCapture(AudioStreamRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);

        if (OperatingSystem.IsAndroidVersionAtLeast(23) && !HasRecordAudioPermission())
        {
            throw new AudioDeviceException("Microphone permission has not been granted.");
        }

        return new AndroidAudioCaptureStream(request, BeginCommunication, EndCommunication);
    }

    public IAudioPlaybackStream OpenPlayback(AudioStreamRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        return new AndroidAudioPlaybackStream(request, BeginCommunication, EndCommunication);
    }

    public Task SetSpeakerEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (_audioManager is null)
        {
            throw new AudioDeviceException("Android audio routing is unavailable.");
        }

        if (OperatingSystem.IsAndroidVersionAtLeast(31))
        {
            if (enabled)
            {
                var speaker = _audioManager.AvailableCommunicationDevices
                    .FirstOrDefault(device => device.Type == AudioDeviceType.BuiltinSpeaker);
                if (speaker is null || !_audioManager.SetCommunicationDevice(speaker))
                {
                    throw new AudioDeviceException("Android could not switch the call to the speaker.");
                }
            }
            else
            {
                _audioManager.ClearCommunicationDevice();
            }
        }
        else
        {
#pragma warning disable CS0618
            _audioManager.SpeakerphoneOn = enabled;
#pragma warning restore CS0618
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        EndAllCommunication();
        if (_audioManager is not null)
        {
            _audioManager.Mode = _originalMode;
        }
    }

    private void BeginCommunication()
    {
        lock (_communicationGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (++_communicationUsers != 1 || _audioManager is null)
            {
                return;
            }

            _audioManager.Mode = Mode.InCommunication;
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
            {
                using var attributesBuilder = new AudioAttributes.Builder();
                var attributes = attributesBuilder
                    .SetUsage(AudioUsageKind.VoiceCommunication)
                    ?.SetContentType(AudioContentType.Speech)
                    ?.Build();
                if (attributes is not null)
                {
                    using var requestBuilder = new AudioFocusRequestClass.Builder(AudioFocus.GainTransient);
                    _focusRequest = requestBuilder
                        .SetAudioAttributes(attributes)
                        ?.SetOnAudioFocusChangeListener(_focusListener)
                        ?.Build();
                    if (_focusRequest is not null)
                    {
                        _ = _audioManager.RequestAudioFocus(_focusRequest);
                    }
                }
            }
            else
            {
#pragma warning disable CS0618
                _ = _audioManager.RequestAudioFocus(_focusListener, global::Android.Media.Stream.VoiceCall, AudioFocus.GainTransient);
#pragma warning restore CS0618
            }
        }
    }

    private void EndCommunication()
    {
        lock (_communicationGate)
        {
            if (_communicationUsers == 0 || --_communicationUsers != 0)
            {
                return;
            }

            ReleaseCommunicationLocked();
        }
    }

    private void EndAllCommunication()
    {
        lock (_communicationGate)
        {
            _communicationUsers = 0;
            ReleaseCommunicationLocked();
        }
    }

    private void ReleaseCommunicationLocked()
    {
        if (_audioManager is null)
        {
            return;
        }

        if (OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            if (_focusRequest is not null)
            {
                _ = _audioManager.AbandonAudioFocusRequest(_focusRequest);
                _focusRequest.Dispose();
                _focusRequest = null;
            }
        }
        else
        {
#pragma warning disable CS0618
            _ = _audioManager.AbandonAudioFocus(_focusListener);
#pragma warning restore CS0618
        }

        if (OperatingSystem.IsAndroidVersionAtLeast(31))
        {
            _audioManager.ClearCommunicationDevice();
        }
        else
        {
#pragma warning disable CS0618
            _audioManager.SpeakerphoneOn = false;
#pragma warning restore CS0618
        }

        _audioManager.Mode = _originalMode;
    }

    [SupportedOSPlatform("android23.0")]
    private static bool HasRecordAudioPermission() =>
        global::Android.App.Application.Context.CheckSelfPermission(global::Android.Manifest.Permission.RecordAudio) == Permission.Granted;

    private sealed class AudioFocusListener : Java.Lang.Object, AudioManager.IOnAudioFocusChangeListener
    {
        public void OnAudioFocusChange(AudioFocus focusChange)
        {
        }
    }
}

internal sealed class AndroidAudioCaptureStream : IAudioCaptureStream
{
    private readonly AudioRecord _record;
    private readonly Action _beginCommunication;
    private readonly Action _endCommunication;
    private readonly object _gate = new();
    private CancellationTokenSource? _lifetime;
    private Task? _worker;
    private bool _disposed;
    private bool _communicationStarted;

    public AndroidAudioCaptureStream(AudioStreamRequest request, Action beginCommunication, Action endCommunication)
    {
        Request = request;
        _beginCommunication = beginCommunication;
        _endCommunication = endCommunication;
        var minimumBytes = AudioRecord.GetMinBufferSize(
            request.SampleRate,
            ChannelIn.Mono,
            global::Android.Media.Encoding.Pcm16bit);
        if (minimumBytes <= 0)
        {
            throw new AudioDeviceException($"Android does not support microphone capture at {request.SampleRate} Hz.");
        }

        var bufferBytes = Math.Max(minimumBytes, request.FrameSizeSamples * sizeof(short) * 4);
        _record = new AudioRecord(
            AudioSource.VoiceCommunication,
            request.SampleRate,
            ChannelIn.Mono,
            global::Android.Media.Encoding.Pcm16bit,
            bufferBytes);

        if (_record.State != State.Initialized)
        {
            _record.Dispose();
            throw new AudioDeviceException("Android could not initialize the microphone.");
        }
    }

    public AudioStreamRequest Request { get; }

    public event Action<short[]>? FrameCaptured;

    public event Action<string>? Error;

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_worker is not null)
            {
                return;
            }

            try
            {
                _beginCommunication();
                _communicationStarted = true;
                _record.StartRecording();
                if (_record.RecordingState != RecordState.Recording)
                {
                    throw new AudioDeviceException("Android did not start microphone capture.");
                }

                _lifetime = new CancellationTokenSource();
                _worker = Task.Factory.StartNew(
                    () => CaptureLoop(_lifetime.Token),
                    _lifetime.Token,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);
            }
            catch (Exception ex)
            {
                EndCommunication();
                Error?.Invoke(ex.Message);
                throw ex is AudioDeviceException ? ex : new AudioDeviceException("Could not start Android microphone capture.", ex);
            }
        }
    }

    public void Stop()
    {
        Task? worker;
        lock (_gate)
        {
            worker = _worker;
            if (worker is null)
            {
                return;
            }

            _worker = null;
            _lifetime?.Cancel();
            try
            {
                _record.Stop();
            }
            catch (InvalidOperationException)
            {
            }
        }

        try
        {
            worker.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(inner => inner is System.OperationCanceledException))
        {
        }

        _lifetime?.Dispose();
        _lifetime = null;
        EndCommunication();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _disposed = true;
        _record.Release();
        _record.Dispose();
    }

    private void EndCommunication()
    {
        if (_communicationStarted)
        {
            _communicationStarted = false;
            _endCommunication();
        }
    }

    private void CaptureLoop(CancellationToken cancellationToken)
    {
        var frame = new short[Request.FrameSizeSamples];
        var filled = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = _record.Read(frame, filled, frame.Length - filled);
                if (read < 0)
                {
                    throw new AudioDeviceException($"Android microphone read failed ({read}).");
                }

                if (read == 0)
                {
                    continue;
                }

                filled += read;
                if (filled != frame.Length)
                {
                    continue;
                }

                FrameCaptured?.Invoke(frame);
                frame = new short[Request.FrameSizeSamples];
                filled = 0;
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            Error?.Invoke(ex.Message);
        }
    }
}

internal sealed class AndroidAudioPlaybackStream : IAudioPlaybackStream
{
    private const int MaxBufferedMilliseconds = 400;
    private readonly AudioTrack _track;
    private readonly ConcurrentQueue<short[]> _frames = new();
    private readonly SemaphoreSlim _available = new(0);
    private readonly object _gate = new();
    private readonly Action _beginCommunication;
    private readonly Action _endCommunication;
    private CancellationTokenSource? _lifetime;
    private Task? _worker;
    private int _queuedSamples;
    private bool _disposed;
    private bool _communicationStarted;

    public AndroidAudioPlaybackStream(AudioStreamRequest request, Action beginCommunication, Action endCommunication)
    {
        Request = request;
        _beginCommunication = beginCommunication;
        _endCommunication = endCommunication;
        var minimumBytes = AudioTrack.GetMinBufferSize(
            request.SampleRate,
            ChannelOut.Mono,
            global::Android.Media.Encoding.Pcm16bit);
        if (minimumBytes <= 0)
        {
            throw new AudioDeviceException($"Android does not support call playback at {request.SampleRate} Hz.");
        }

        var bufferBytes = Math.Max(minimumBytes, request.FrameSizeSamples * sizeof(short) * 4);
        using var attributesBuilder = new AudioAttributes.Builder();
        _ = attributesBuilder.SetUsage(AudioUsageKind.VoiceCommunication);
        _ = attributesBuilder.SetContentType(AudioContentType.Speech);
        using var attributes = attributesBuilder.Build()
            ?? throw new AudioDeviceException("Android could not create call audio attributes.");

        using var formatBuilder = new AudioFormat.Builder();
        _ = formatBuilder.SetSampleRate(request.SampleRate);
        _ = formatBuilder.SetChannelMask(ChannelOut.Mono);
        _ = formatBuilder.SetEncoding(global::Android.Media.Encoding.Pcm16bit);
        using var format = formatBuilder.Build()
            ?? throw new AudioDeviceException("Android could not create the call audio format.");
        _track = new AudioTrack(
            attributes,
            format,
            bufferBytes,
            AudioTrackMode.Stream,
            AudioManager.AudioSessionIdGenerate);

        if (_track.State != AudioTrackState.Initialized)
        {
            _track.Dispose();
            throw new AudioDeviceException("Android could not initialize call audio playback.");
        }
    }

    public AudioStreamRequest Request { get; }

    public event Action<string>? Error;

    public TimeSpan Buffered => TimeSpan.FromSeconds((double)Math.Max(0, Volatile.Read(ref _queuedSamples)) / Request.SampleRate);

    public void Enqueue(ReadOnlySpan<short> pcm)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (pcm.IsEmpty)
        {
            return;
        }

        var copy = pcm.ToArray();
        _frames.Enqueue(copy);
        Interlocked.Add(ref _queuedSamples, copy.Length);

        var maximumSamples = Request.SampleRate * MaxBufferedMilliseconds / 1000;
        while (Volatile.Read(ref _queuedSamples) > maximumSamples && _frames.TryDequeue(out var dropped))
        {
            Interlocked.Add(ref _queuedSamples, -dropped.Length);
        }

        _available.Release();
    }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_worker is not null)
            {
                return;
            }

            try
            {
                _beginCommunication();
                _communicationStarted = true;
                _track.Play();
                _lifetime = new CancellationTokenSource();
                _worker = Task.Factory.StartNew(
                    () => PlaybackLoop(_lifetime.Token),
                    _lifetime.Token,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);
            }
            catch (Exception ex)
            {
                EndCommunication();
                Error?.Invoke(ex.Message);
                throw new AudioDeviceException("Could not start Android call audio playback.", ex);
            }
        }
    }

    public void Stop()
    {
        Task? worker;
        lock (_gate)
        {
            worker = _worker;
            if (worker is null)
            {
                return;
            }

            _worker = null;
            _lifetime?.Cancel();
            _available.Release();
        }

        try
        {
            worker.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(inner => inner is System.OperationCanceledException))
        {
        }

        try
        {
            _track.Pause();
            _track.Flush();
            _track.Stop();
        }
        catch (InvalidOperationException)
        {
        }

        while (_frames.TryDequeue(out _))
        {
        }

        Interlocked.Exchange(ref _queuedSamples, 0);
        _lifetime?.Dispose();
        _lifetime = null;
        EndCommunication();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _disposed = true;
        _available.Dispose();
        _track.Release();
        _track.Dispose();
    }

    private void EndCommunication()
    {
        if (_communicationStarted)
        {
            _communicationStarted = false;
            _endCommunication();
        }
    }

    private void PlaybackLoop(CancellationToken cancellationToken)
    {
        var silence = new short[Request.FrameSizeSamples];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var hasFrame = _available.Wait(Request.FrameDurationMilliseconds, cancellationToken);
                var frame = silence;
                if (hasFrame && _frames.TryDequeue(out var queued))
                {
                    frame = queued;
                    Interlocked.Add(ref _queuedSamples, -queued.Length);
                }

                var offset = 0;
                while (offset < frame.Length && !cancellationToken.IsCancellationRequested)
                {
                    var written = _track.Write(frame, offset, frame.Length - offset);
                    if (written < 0)
                    {
                        throw new AudioDeviceException($"Android call audio write failed ({written}).");
                    }

                    offset += written;
                }
            }
        }
        catch (System.OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            Error?.Invoke(ex.Message);
        }
    }
}
