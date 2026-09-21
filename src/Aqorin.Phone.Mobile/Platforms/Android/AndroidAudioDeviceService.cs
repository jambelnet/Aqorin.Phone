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
public sealed class AndroidAudioDeviceService : IAudioDeviceService
{
    private readonly AudioManager? _audioManager;
    private readonly Mode _originalMode;
    private bool _disposed;

    public AndroidAudioDeviceService()
    {
        _audioManager = global::Android.App.Application.Context.GetSystemService(Context.AudioService) as AudioManager;
        _originalMode = _audioManager?.Mode ?? Mode.Normal;
        if (_audioManager is not null)
        {
            _audioManager.Mode = Mode.InCommunication;
        }
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

        if (Build.VERSION.SdkInt >= BuildVersionCodes.M && !HasRecordAudioPermission())
        {
            throw new AudioDeviceException("Microphone permission has not been granted.");
        }

        return new AndroidAudioCaptureStream(request);
    }

    public IAudioPlaybackStream OpenPlayback(AudioStreamRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        return new AndroidAudioPlaybackStream(request);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_audioManager is not null)
        {
            _audioManager.Mode = _originalMode;
        }
    }

    [SupportedOSPlatform("android23.0")]
    private static bool HasRecordAudioPermission() =>
        global::Android.App.Application.Context.CheckSelfPermission(global::Android.Manifest.Permission.RecordAudio) == Permission.Granted;
}

internal sealed class AndroidAudioCaptureStream : IAudioCaptureStream
{
    private readonly AudioRecord _record;
    private readonly object _gate = new();
    private CancellationTokenSource? _lifetime;
    private Task? _worker;
    private bool _disposed;

    public AndroidAudioCaptureStream(AudioStreamRequest request)
    {
        Request = request;
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
    private CancellationTokenSource? _lifetime;
    private Task? _worker;
    private int _queuedSamples;
    private bool _disposed;

    public AndroidAudioPlaybackStream(AudioStreamRequest request)
    {
        Request = request;
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
