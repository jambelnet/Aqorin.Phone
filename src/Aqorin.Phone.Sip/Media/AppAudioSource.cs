using System.Collections.Concurrent;
using Aqorin.Phone.Core.Abstractions;
using Microsoft.Extensions.Logging;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;

namespace Aqorin.Phone.Sip.Media;

/// <summary>
/// Bridges the application's <see cref="IAudioCaptureStream"/> (microphone) to SIPSorcery's <see cref="IAudioSource"/>.
/// <para>
/// Threading contract (important — see docs/architecture-notes.md):
/// <list type="bullet">
/// <item>SIPSorcery calls <see cref="StartAudio"/>/<see cref="CloseAudio"/> from its single SIP transport thread. Opening or
/// closing a native audio device there would stall all SIP processing, so device work runs on the thread pool with a timeout.</item>
/// <item>The native audio callback only encodes and enqueues frames. A dedicated 20 ms pacing loop raises
/// <see cref="OnAudioSourceEncodedSample"/>, so PortAudio's callback thread never blocks inside the RTP stack and
/// stopping the device can never wait on SIPSorcery.</item>
/// </list>
/// </para>
/// </summary>
internal sealed class AppAudioSource : IAudioSource, IDisposable
{
    private const int MaxQueuedFrames = 5; // ~100 ms of backlog before old frames are dropped

    /// <summary>Upper bound for opening a device; afterwards the call proceeds without a microphone.</summary>
    internal static TimeSpan OpenTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Upper bound we wait for a device to close; a hung driver must not hold up the call teardown.</summary>
    internal static TimeSpan CloseTimeout { get; set; } = TimeSpan.FromSeconds(5);

    private readonly IAudioDeviceService _devices;
    private readonly AudioMediaSessionOptions _options;
    private readonly ILogger _logger;
    private readonly AudioEncoder _encoder = new();
    private readonly ConcurrentQueue<byte[]> _pending = new();
    private readonly object _gate = new();
    private List<AudioFormat> _formats;
    private FormatBox _format;
    private IAudioCaptureStream? _capture;
    private CancellationTokenSource? _pacer;
    private volatile bool _paused;
    private volatile bool _closed;
    private byte[]? _silenceFrame;

    public AppAudioSource(IAudioDeviceService devices, AudioMediaSessionOptions options, ILogger logger)
    {
        _devices = devices;
        _options = options;
        _logger = logger;
        _formats = CodecCatalog.Formats(options.CodecPreference);
        _format = new FormatBox(_formats[0]);
    }

    public event EncodedSampleDelegate? OnAudioSourceEncodedSample;
#pragma warning disable CS0067 // required by IAudioSource; frames are delivered via OnAudioSourceEncodedSample
    public event Action<EncodedAudioFrame>? OnAudioSourceEncodedFrameReady;
#pragma warning restore CS0067
    public event RawAudioSampleDelegate? OnAudioSourceRawSample;
    public event SourceErrorDelegate? OnAudioSourceError;

    public List<AudioFormat> GetAudioSourceFormats() => new(_formats);

    public void SetAudioSourceFormat(AudioFormat audioFormat)
    {
        Volatile.Write(ref _format, new FormatBox(audioFormat));
        _silenceFrame = null;
        _logger.LogInformation("Audio source format set to {Codec} @ {Rate} Hz.", audioFormat.FormatName, audioFormat.ClockRate);
    }

    public void RestrictFormats(Func<AudioFormat, bool> filter)
    {
        var filtered = _formats.Where(filter).ToList();
        if (filtered.Count > 0)
        {
            _formats = filtered;
            if (!filtered.Contains(_format.Format))
            {
                SetAudioSourceFormat(filtered[0]);
            }
        }
    }

    public Task StartAudio()
    {
        if (_closed)
        {
            return Task.CompletedTask;
        }

        // Start the RTP pacer immediately so the remote side receives a steady stream (silence until the mic is up).
        StartPacer();

        var open = Task.Factory.StartNew(OpenCapture, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        return Task.WhenAny(open, Task.Delay(OpenTimeout)).ContinueWith(t =>
        {
            if (!open.IsCompleted)
            {
                _logger.LogError("Opening the microphone did not complete within {Timeout}s; continuing without microphone audio.", OpenTimeout.TotalSeconds);
                OnAudioSourceError?.Invoke($"Microphone did not respond within {OpenTimeout.TotalSeconds:0}s.");
            }
        }, TaskScheduler.Default);
    }

    public Task PauseAudio()
    {
        _paused = true;
        return Task.CompletedTask;
    }

    public Task ResumeAudio()
    {
        _paused = false;
        return Task.CompletedTask;
    }

    public Task CloseAudio()
    {
        _ = CloseAsync();
        return Task.CompletedTask;
    }

    public void ExternalAudioSourceRawSample(AudioSamplingRatesEnum samplingRate, uint durationMilliseconds, short[] sample)
    {
        // Not used: audio always comes from the microphone.
    }

    public bool HasEncodedAudioSubscribers() => OnAudioSourceEncodedSample is not null;

    public bool IsAudioSourcePaused() => _paused;

    public void Dispose()
    {
        // Synchronous callers (e.g. media session teardown) must not block on native device code either.
        _ = CloseAsync();
    }

    /// <summary>Stops the pacer and releases the device on the thread pool. Completes within <see cref="CloseTimeout"/>.</summary>
    internal Task CloseAsync()
    {
        IAudioCaptureStream? capture;
        CancellationTokenSource? pacer;
        lock (_gate)
        {
            if (_closed)
            {
                return Task.CompletedTask;
            }

            _closed = true;
            capture = _capture;
            _capture = null;
            pacer = _pacer;
            _pacer = null;
        }

        pacer?.Cancel();
        _pending.Clear();

        var release = Task.Factory.StartNew(() =>
        {
            if (capture is not null)
            {
                capture.FrameCaptured -= OnFrameCaptured;
                try
                {
                    capture.Stop();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Stopping the microphone failed.");
                }

                try
                {
                    capture.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Disposing the microphone failed.");
                }
            }

            pacer?.Dispose();
            _encoder.Dispose();
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        return Task.WhenAny(release, Task.Delay(CloseTimeout)).ContinueWith(_ =>
        {
            if (!release.IsCompleted)
            {
                _logger.LogError("Closing the microphone did not complete within {Timeout}s; abandoning the device handle.", CloseTimeout.TotalSeconds);
            }
        }, TaskScheduler.Default);
    }

    private void OpenCapture()
    {
        try
        {
            var format = Volatile.Read(ref _format).Format;
            var request = new AudioStreamRequest(format.ClockRate, _options.FrameDurationMilliseconds, _options.InputDeviceId);
            var capture = _devices.OpenCapture(request);
            capture.FrameCaptured += OnFrameCaptured;
            capture.Error += message => OnAudioSourceError?.Invoke("Microphone: " + message);

            lock (_gate)
            {
                if (_closed)
                {
                    capture.Dispose();
                    return;
                }

                _capture = capture;
            }

            capture.Start();
            _logger.LogInformation("Microphone capture started ({Rate} Hz, {Frame} ms frames).", request.SampleRate, request.FrameDurationMilliseconds);
        }
        catch (Exception ex)
        {
            // Keep the call alive (one-way audio) but surface the problem to the user.
            _logger.LogError(ex, "Microphone could not be started.");
            OnAudioSourceError?.Invoke("Microphone unavailable: " + ex.Message);
        }
    }

    private void StartPacer()
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_pacer is not null || _closed)
            {
                return;
            }

            cts = new CancellationTokenSource();
            _pacer = cts;
        }

        _ = Task.Run(() => PaceAsync(cts.Token), CancellationToken.None);
    }

    /// <summary>Emits exactly one encoded frame every frame period, using silence when the microphone has not delivered one.</summary>
    private async Task PaceAsync(CancellationToken token)
    {
        var period = TimeSpan.FromMilliseconds(_options.FrameDurationMilliseconds);
        using var timer = new PeriodicTimer(period);
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                var handler = OnAudioSourceEncodedSample;
                if (handler is null || _paused || _closed)
                {
                    _pending.Clear();
                    continue;
                }

                // Bound latency: if the mic is ahead of the pacer, keep only the newest frames.
                while (_pending.Count > MaxQueuedFrames && _pending.TryDequeue(out _))
                {
                }

                var format = Volatile.Read(ref _format).Format;
                if (!_pending.TryDequeue(out var frame))
                {
                    frame = Silence(format);
                }

                var samples = format.ClockRate * _options.FrameDurationMilliseconds / 1000;
                var rtpUnits = (uint)((long)samples * format.RtpClockRate / format.ClockRate);
                try
                {
                    handler(rtpUnits, frame);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "RTP send failed.");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Audio pacing loop stopped unexpectedly.");
            OnAudioSourceError?.Invoke("Audio sending stopped: " + ex.Message);
        }
    }

    private byte[] Silence(AudioFormat format)
    {
        var cached = _silenceFrame;
        if (cached is not null)
        {
            return cached;
        }

        var samples = format.ClockRate * _options.FrameDurationMilliseconds / 1000;
        try
        {
            cached = _encoder.EncodeAudio(new short[samples], format);
        }
        catch (Exception)
        {
            cached = new byte[samples];
        }

        _silenceFrame = cached;
        return cached;
    }

    /// <summary>Runs on the native audio thread: encode and enqueue only, never block.</summary>
    private void OnFrameCaptured(short[] pcm)
    {
        if (_paused || _closed)
        {
            return;
        }

        var format = Volatile.Read(ref _format).Format;
        try
        {
            _pending.Enqueue(_encoder.EncodeAudio(pcm, format));
            OnAudioSourceRawSample?.Invoke(CodecCatalog.SamplingRate(format.ClockRate), (uint)(pcm.Length * 1000 / format.ClockRate), pcm);
        }
        catch (ObjectDisposedException)
        {
            // Closing; ignore the tail of the stream.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Encoding a microphone frame failed.");
            OnAudioSourceError?.Invoke("Audio encoding failed: " + ex.Message);
        }
    }

    private sealed record FormatBox(AudioFormat Format);
}
